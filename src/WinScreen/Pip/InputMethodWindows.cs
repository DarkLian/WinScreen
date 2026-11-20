using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

// Windows puts the input method's list under the caret of the window with the keys: the hidden original's, at the
// screen's edge.
// Only Windows 11's own list (TextInputHost, in an Explorer frame, and only beside the original's caret) is moved;
// other input methods' windows are left alone (INV-13).
// Nothing is journalled: the input method places the list again each time it opens.
internal sealed class InputMethodWindows : IDisposable
{
    internal const string ListHost = "TextInputHost";
    private const uint Shown = 0x8002, LocationChange = 0x800B, Uncloaked = 0x8018, OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    internal const int Near = 64;
    private const int Gap = 2;

    private readonly nint _source;
    private readonly uint _sourceThread, _sourceProcess;
    private readonly Func<PipMapping> _geometry;
    private readonly Func<Point?> _lastPress;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private uint _threadId;
    private volatile bool _stopping;
    private nint _held;
    private Point _heldFrom, _heldAt;
    private Rectangle? _caret; // On the screen, where the original really is.
    private uint _host;
    private long _hostLookedAt = -100_000;
    private int _moves;

    [ThreadStatic] private static InputMethodWindows? _current;
    private static readonly unsafe nint Callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnEvent;

    // lastPress stands in for the caret when the app reports none.
    public InputMethodWindows(nint source, Func<PipMapping> geometry, Func<Point?> lastPress)
    {
        _source = source;
        _sourceThread = User32.GetWindowThreadProcessId(source, out _sourceProcess);
        _geometry = geometry;
        _lastPress = lastPress;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _current = this;
            _threadId = TextCaret.PrepareThread();
            ready.Set();
            nint caretHook = _sourceProcess == 0 ? 0 : User32.SetWinEventHook(Shown, LocationChange, 0, Callback, _sourceProcess, 0, OutOfContext);
            // The emoji panel's frame (Explorer's) is uncloaked rather than shown. Rare events, so any program's.
            nint uncloakHook = User32.SetWinEventHook(Uncloaked, Uncloaked, 0, Callback, 0, 0, OutOfContext | SkipOwnProcess);
            nint hostHook = 0;
            try
            {
                TextCaret.Pump(50, () =>
                {
                    if (hostHook == 0 && HostProcess() is var host and not 0)
                        hostHook = User32.SetWinEventHook(Shown, Shown, 0, Callback, host, 0, OutOfContext);
                    ConsiderShown();
                    Follow();
                });
            }
            finally
            {
                if (caretHook != 0) User32.UnhookWinEvent(caretHook);
                if (uncloakHook != 0) User32.UnhookWinEvent(uncloakHook);
                if (hostHook != 0) User32.UnhookWinEvent(hostHook);
            }
        }) { IsBackground = true, Name = "WinScreen input method" };
        // Accessibility objects of other programs are reached through COM, which wants a single-threaded apartment
        // here.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    internal static bool Wanted => Environment.OSVersion.Version.Build >= 22000;

    internal bool Holds(nint root) => root != 0 && root == Volatile.Read(ref _held);

    internal nint HeldWindow => Volatile.Read(ref _held);

    internal int Moves => Volatile.Read(ref _moves);

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        // Never let an exception out of a callback Windows makes into native code.
        try { _current?.Handle(kind, window, item, child); }
        catch (Exception e) when (e is not OutOfMemoryException) { Diagnostics.Log.Swallowed(e); }
    }

    private void Handle(uint kind, nint window, int item, int child)
    {
        if (_stopping) return;
        if (item == -8) // The caret: it moves as the text being put together grows.
        {
            // Asked of the app only while a list is kept beside it (and when one comes up): not on every key.
            if (kind is Shown or LocationChange)
            {
                lock (_gate) _caretEvent = (window, child);
                if (Volatile.Read(ref _held) != 0) { ReadCaret(); Follow(); }
            }
            return;
        }
        if (item != 0 || child != 0) return;
        nint root = User32.GetAncestor(window, User32.GA_ROOT);
        // Only the list's program's windows: the original's own (its suggestions, its menus) are its view's to place.
        User32.GetWindowThreadProcessId(window, out uint process);
        // A frame that is uncloaked may get the list's window inside it only a moment later: Consider checks again.
        if (kind == Uncloaked ? Windows.WindowInfo.ClassName(root) != Frame : kind != Shown || process == 0 || process != _host) return;
        // It may still be on its way (its frame still cloaked): looked at again on the next ticks for a while.
        // A window of the list's program itself makes its frame known for this visit (the list's own window is one
        // Windows doesn't list inside the frame); an uncloaked frame not known yet is checked inside.
        bool fromHost;
        lock (_gate)
        {
            if (kind == Shown) _hostFrames.Add(root);
            fromHost = _hostFrames.Contains(root);
            _shownRoot = root;
            _shownFromHost = fromHost;
            _shownAt = Environment.TickCount64;
        }
        Consider(root, fromHost);
    }

    private (nint Window, int Child)? _caretEvent;

    private void ReadCaret()
    {
        (nint Window, int Child)? latest;
        lock (_gate) latest = _caretEvent;
        if (latest is { } at && TextCaret.FromEvent(at.Window, -8, at.Child) is { } caret)
            lock (_gate) _caret = caret;
    }

    private nint _shownRoot;
    private bool _shownFromHost;
    private readonly HashSet<nint> _hostFrames = [];
    private long _shownAt = -100_000;

    internal string LastRefusal { get; private set; } = "";

    private void Consider(nint root, bool fromHost)
    {
        LastRefusal = root == 0 ? "no window" : !User32.IsWindowVisible(root) ? "not visible" : Cloaked(root) ? "cloaked"
            : !KeysWithOriginal() ? "keys elsewhere" : !fromHost && !ShowsHost(root) ? "not the list's program" : "";
        if (LastRefusal != "") return;
        ReadCaret();
        Rectangle? caret;
        lock (_gate) caret = _caret;
        caret ??= TextCaret.In(TextCaret.FocusOf(_sourceThread));
        if (!User32.GetWindowRect(root, out Rect r) || !User32.GetWindowRect(_source, out Rect source)) { LastRefusal = "no bounds"; return; }
        Rectangle list = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        Rectangle original = Rectangle.FromLTRB(source.Left, source.Top, source.Right, source.Bottom);
        if (!BesideCaret(list, caret ?? original)) { LastRefusal = $"not beside the caret: {list} caret {caret?.ToString() ?? "unknown"}"; return; }
        lock (_gate)
        {
            if (caret is not null) _caret ??= caret;
            if (_held != root) { _held = root; _heldFrom = list.Location; _heldAt = new Point(int.MinValue, int.MinValue); }
            _shownRoot = 0;
        }
        Follow();
    }

    private void ConsiderShown()
    {
        nint root;
        bool fromHost;
        lock (_gate)
        {
            if (_held != 0 || _shownRoot == 0) return;
            if (Environment.TickCount64 - _shownAt > 1500) { _shownRoot = 0; return; }
            root = _shownRoot;
            fromHost = _shownFromHost;
        }
        Consider(root, fromHost);
    }

    // Windows keeps the list on the screen, so the caret is pulled onto the nearest display too before measuring.
    internal static bool BesideCaret(Rectangle list, Rectangle caret)
    {
        Rectangle display = Screen.FromRectangle(caret).Bounds;
        Rectangle onScreen = new(PipGeometry.KeepInside(caret, display), new Size(Math.Min(caret.Width, display.Width), Math.Min(caret.Height, display.Height)));
        Rectangle around = onScreen;
        around.Inflate(Near, Near);
        return around.IntersectsWith(list);
    }

    private void Follow()
    {
        nint held;
        Rectangle? caret;
        lock (_gate) { held = _held; caret = _caret; }
        if (held == 0) return;
        if (_stopping || !User32.IsWindow(held) || !User32.IsWindowVisible(held) || Cloaked(held) || !KeysWithOriginal())
        {
            Release();
            return;
        }
        if (!User32.GetWindowRect(held, out Rect r)) return;
        Rectangle list = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (Place(list.Size, caret) is not { } place) return;
        bool moved;
        lock (_gate)
        {
            if (_held != held) return;
            moved = _heldAt != place;
            _heldAt = place;
        }
        // The input method puts it back as it opens again and as it pages: put straight back. Held first, so the move
        // below isn't taken for one of the input method's own and undone.
        PopupGuard.Hold(held, place);
        if (list.Location != place)
        {
            User32.SetWindowPos(held, 0, place.X, place.Y, 0, 0, Move);
            if (moved && Interlocked.Increment(ref _moves) == 1)
                Diagnostics.Log.Event("pip.inputMethod.list", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("caret", caret is not null));
        }
    }

    private Point? Place(Size list, Rectangle? caret)
    {
        PipMapping map = _geometry();
        if (map.View.Width <= 0 || map.View.Height <= 0 || map.Shown.Width <= 0 || map.Shown.Height <= 0) return null;
        Rectangle line;
        if (caret is { } c && User32.GetWindowRect(_source, out Rect source))
            line = Mapped(new Rectangle(c.Left - source.Left, c.Top - source.Top, c.Width, c.Height), map);
        else if (_lastPress() is { } press)
            line = new Rectangle(press.X, press.Y - 8, 1, 16);
        else return null;
        return Below(line, list, Screen.FromPoint(line.Location).WorkingArea);
    }

    // Turned, the caret may lie across: the list goes under the rectangle around it.
    internal static Rectangle Mapped(Rectangle inOriginal, PipMapping map) =>
        map.Turn.IsUpright ? Mapped(inOriginal, map.View, map.Shown) : map.ToView(inOriginal);

    internal static Rectangle Mapped(Rectangle inOriginal, Rectangle view, Rectangle shown)
    {
        double sx = view.Width / (double)shown.Width, sy = view.Height / (double)shown.Height;
        int left = Math.Clamp(view.Left + (int)Math.Round((inOriginal.Left - shown.Left) * sx), view.Left, view.Right - 1);
        int top = Math.Clamp(view.Top + (int)Math.Round((inOriginal.Top - shown.Top) * sy), view.Top, view.Bottom - 1);
        int bottom = Math.Clamp(view.Top + (int)Math.Round((inOriginal.Bottom - shown.Top) * sy), top + 1, view.Bottom);
        return Rectangle.FromLTRB(left, top, left + 1, bottom);
    }

    internal static Point Below(Rectangle line, Size list, Rectangle area)
    {
        int y = line.Bottom + Gap;
        if (y + list.Height > area.Bottom && line.Top - Gap - list.Height >= area.Top) y = line.Top - Gap - list.Height;
        return PipGeometry.KeepInside(new Rectangle(line.Left, y, list.Width, list.Height), area);
    }

    private bool KeysWithOriginal() => User32.GetForegroundWindow() == _source;

    internal const string Frame = "ApplicationFrameWindow";

    private bool ShowsHost(nint root)
    {
        if (root == 0 || _host == 0) return false;
        User32.GetWindowThreadProcessId(root, out uint own);
        if (own == _host) return true;
        return Windows.WindowInfo.ClassName(root) == Frame && ChildWindows.AnyOf(root, _host);
    }

    private static bool Cloaked(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private uint HostProcess()
    {
        if (_host != 0) return _host;
        long now = Environment.TickCount64;
        if (now - _hostLookedAt < 2000) return 0;
        _hostLookedAt = now;
        using Process self = Process.GetCurrentProcess();
        int session = self.SessionId;
        foreach (Process process in Process.GetProcessesByName(ListHost))
            using (process)
                if (process.SessionId == session) _host = (uint)process.Id;
        return _host;
    }

    private void Release()
    {
        nint held;
        Point from, at;
        lock (_gate)
        {
            held = _held;
            from = _heldFrom;
            at = _heldAt;
            _held = 0;
        }
        if (held == 0) return;
        PopupGuard.Release(held);
        if (User32.IsWindow(held) && User32.GetWindowRect(held, out Rect r) && new Point(r.Left, r.Top) == at)
            User32.SetWindowPos(held, 0, from.X, from.Y, 0, 0, Move);
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        if (_threadId != 0) TextCaret.Quit(_threadId);
        // A caret question to an app that stopped answering can't hold up the end of the visit.
        _thread.Join(300);
        Release();
    }
}
