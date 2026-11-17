using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// Keeps the input method's list beside the caret in the view while someone types into a hidden original through
/// it. Windows places that list (the candidates of a Chinese or Japanese input method, and the emoji panel) under the
/// caret of the window that has the keys: the hidden original's, at the edge of the screen. One of these lives for
/// one visit, on a thread of its own.
/// <para>
/// Only Windows 11's own list is moved: shown by <c>TextInputHost</c>, in a frame window of Explorer's, and only when
/// it is beside the original's caret (the touch keyboard, docked elsewhere, never is). Other input methods' windows
/// aren't known and are left alone (INV-13). Nothing is journalled: the input method places the list again each time
/// it opens, and it closes once the original loses the keys.
/// </para>
/// </summary>
internal sealed class InputMethodWindows : IDisposable
{
    /// <summary>The program that shows Windows 11's input method lists and emoji panel.</summary>
    internal const string ListHost = "TextInputHost";
    private const uint Shown = 0x8002, LocationChange = 0x800B, Uncloaked = 0x8018, OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    /// <summary>How near the caret (both kept on the screen, as Windows keeps the list) the list must be to be the original's.</summary>
    internal const int Near = 64;
    /// <summary>Space between the caret line and the list.</summary>
    private const int Gap = 2;

    private readonly nint _source;
    private readonly uint _sourceThread, _sourceProcess;
    private readonly Func<(Rectangle View, Rectangle Shown)> _geometry;
    private readonly Func<Point?> _lastPress;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private uint _threadId;
    private volatile bool _stopping;
    private nint _held; // The list's frame while it is kept beside the caret.
    private Point _heldFrom, _heldAt;
    private Rectangle? _caret; // On the screen, where the original really is.
    private uint _host;
    private long _hostLookedAt = -100_000;
    private int _moves;

    [ThreadStatic] private static InputMethodWindows? _current;
    private static readonly unsafe nint Callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnEvent;

    /// <param name="geometry">Where the view shows the original: the picture on the screen, and the part of the original it shows.</param>
    /// <param name="lastPress">Where the latest press in the view was, used for the caret when the app doesn't report one.</param>
    public InputMethodWindows(nint source, Func<(Rectangle View, Rectangle Shown)> geometry, Func<Point?> lastPress)
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
        // Accessibility objects of other programs are reached through COM, which wants a single-threaded apartment here.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    /// <summary>Whether there is such a list to keep: Windows 11's (Windows 10's input methods aren't known).</summary>
    internal static bool Wanted => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Whether <paramref name="root"/> is the list, kept beside the caret: the pointer going there is still the visit.</summary>
    internal bool Holds(nint root) => root != 0 && root == Volatile.Read(ref _held);

    /// <summary>The list's frame while it is kept beside the caret, or 0; for tests.</summary>
    internal nint HeldWindow => Volatile.Read(ref _held);

    /// <summary>How often the list was put beside the caret during this visit, for tests.</summary>
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

    private (nint Window, int Child)? _caretEvent; // The latest caret the app reported, asked about when needed.

    /// <summary>Where the caret the app last reported is now.</summary>
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

    /// <summary>Why the last window of the list's program that came up wasn't kept beside the caret; for tests and diagnostics.</summary>
    internal string LastRefusal { get; private set; } = "";

    /// <summary>A window of the list's program came up: kept beside the caret if it is the list the original's keys opened.</summary>
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

    /// <summary>The list that came up a moment ago but wasn't ready yet, looked at again.</summary>
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

    /// <summary>
    /// Whether the list is the one opened for this caret. Windows keeps the list on the screen, so the caret is too
    /// (pulled onto the display it is nearest), before measuring.
    /// </summary>
    internal static bool BesideCaret(Rectangle list, Rectangle caret)
    {
        Rectangle display = Screen.FromRectangle(caret).Bounds;
        Rectangle onScreen = new(PipGeometry.KeepInside(caret, display), new Size(Math.Min(caret.Width, display.Width), Math.Min(caret.Height, display.Height)));
        Rectangle around = onScreen;
        around.Inflate(Near, Near);
        return around.IntersectsWith(list);
    }

    /// <summary>Puts the list where it belongs now, and keeps it there; or lets it go once it is closed or the keys went elsewhere.</summary>
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

    /// <summary>Where the list goes: under the caret as the view shows it, or over it where there is no room below.</summary>
    private Point? Place(Size list, Rectangle? caret)
    {
        var (view, shown) = _geometry();
        if (view.Width <= 0 || view.Height <= 0 || shown.Width <= 0 || shown.Height <= 0) return null;
        Rectangle line;
        if (caret is { } c && User32.GetWindowRect(_source, out Rect source))
            line = Mapped(new Rectangle(c.Left - source.Left, c.Top - source.Top, c.Width, c.Height), view, shown);
        else if (_lastPress() is { } press)
            line = new Rectangle(press.X, press.Y - 8, 1, 16);
        else return null;
        return Below(line, list, Screen.FromPoint(line.Location).WorkingArea);
    }

    /// <summary>A rectangle of the original (from its window's corner) as the view shows it, kept inside the picture.</summary>
    internal static Rectangle Mapped(Rectangle inOriginal, Rectangle view, Rectangle shown)
    {
        double sx = view.Width / (double)shown.Width, sy = view.Height / (double)shown.Height;
        int left = Math.Clamp(view.Left + (int)Math.Round((inOriginal.Left - shown.Left) * sx), view.Left, view.Right - 1);
        int top = Math.Clamp(view.Top + (int)Math.Round((inOriginal.Top - shown.Top) * sy), view.Top, view.Bottom - 1);
        int bottom = Math.Clamp(view.Top + (int)Math.Round((inOriginal.Bottom - shown.Top) * sy), top + 1, view.Bottom);
        return Rectangle.FromLTRB(left, top, left + 1, bottom);
    }

    /// <summary>Under <paramref name="line"/>, or over it when the list wouldn't fit below; inside <paramref name="area"/>.</summary>
    internal static Point Below(Rectangle line, Size list, Rectangle area)
    {
        int y = line.Bottom + Gap;
        if (y + list.Height > area.Bottom && line.Top - Gap - list.Height >= area.Top) y = line.Top - Gap - list.Height;
        return PipGeometry.KeepInside(new Rectangle(line.Left, y, list.Width, list.Height), area);
    }

    private bool KeysWithOriginal() => User32.GetForegroundWindow() == _source;

    /// <summary>The frame Explorer puts round the list's windows.</summary>
    internal const string Frame = "ApplicationFrameWindow";

    /// <summary>
    /// Whether a window is the list's program's own, or Explorer's frame holding one of its windows (Windows 11's
    /// input method lists and emoji panel are).
    /// </summary>
    private bool ShowsHost(nint root)
    {
        if (root == 0 || _host == 0) return false;
        User32.GetWindowThreadProcessId(root, out uint own);
        if (own == _host) return true;
        return Windows.WindowInfo.ClassName(root) == Frame && ChildWindows.AnyOf(root, _host);
    }

    private static bool Cloaked(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The list's program in this session, looked up at most every two seconds while it isn't known.</summary>
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

    /// <summary>Stops keeping the list; if it is still where it was put, it goes back where its input method had it.</summary>
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
