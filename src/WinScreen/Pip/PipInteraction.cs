using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>What became of one press while a view was interactive. Every press gets exactly one.</summary>
public enum PressOutcome
{
    /// <summary>It reached the content of the hidden window, at the mapped point.</summary>
    Delivered,
    /// <summary>It reached a menu or popup the program opened.</summary>
    Popup,
    /// <summary>It reached WinScreen's own controls on the view.</summary>
    ViewControl,
    /// <summary>It mapped to the hidden window's title bar or frame, so it moves the view instead.</summary>
    MovedView,
    /// <summary>It fell outside the view before the session ended.</summary>
    Outside,
    /// <summary>The window couldn't confirm where it was in time, so the press was withheld.</summary>
    Unconfirmed,
    /// <summary>It mapped to the hidden window's close, minimize or maximize button, or its icon, which act on the view.</summary>
    CaptionButton,
}

/// <summary>What a press on the hidden window's caption buttons or icon does, by the standard for views used directly.</summary>
public enum CaptionAction
{
    /// <summary>Close: the original is asked to close, and the view goes with it.</summary>
    Close,
    /// <summary>Minimize: the view minimizes; the original never does, or its picture would stop.</summary>
    Minimize,
    /// <summary>Maximize or restore: go to the original.</summary>
    GoToOriginal,
    /// <summary>The window icon, or a right-click on the title bar: the view's own menu.</summary>
    Menu,
}

/// <summary>What each hit-test answer means for a press that would land in a hidden window.</summary>
internal static class PressRules
{
    private const int Client = 1, Menu = 5, VerticalScroll = 6, HorizontalScroll = 7;

    /// <summary>
    /// Content only. The title bar, frame and caption buttons of an invisible window must never take a
    /// press: a double-click would maximize it and a drag would start a system move nobody can see.
    /// </summary>
    public static bool ReachesContent(nint hit) => (int)hit is Client or Menu or VerticalScroll or HorizontalScroll;

    private const int Caption = 2, SystemMenu = 3, MinButton = 8, MaxButton = 9, Left = 10, BottomRight = 17, Border = 18, Close = 20;

    /// <summary>
    /// The standard for the original's own caption controls in a view used directly. Null means none: a title
    /// bar moves the view, and a border does nothing (resizing the original would reflow what the view shows).
    /// </summary>
    public static CaptionAction? CaptionActionFor(nint hit, MouseButtons button) => ((int)hit, button) switch
    {
        (Close, MouseButtons.Left) => CaptionAction.Close,
        (MinButton, MouseButtons.Left) => CaptionAction.Minimize,
        (MaxButton, MouseButtons.Left) => CaptionAction.GoToOriginal,
        (SystemMenu, MouseButtons.Left or MouseButtons.Right) or (Caption, MouseButtons.Right) => CaptionAction.Menu,
        _ => null,
    };

    /// <summary>Borders and corners: nothing happens, and nothing is dragged.</summary>
    public static bool IsBorder(nint hit) => (int)hit is >= Left and <= BottomRight or Border;
}

//// <summary>
/// One pointer visit to a view used directly. The original stays parked and invisible; the pointer's moves,
/// presses and wheel turns reach it as messages at the matching point. The hook and its threads exist only
/// for this visit.
/// </summary>
internal sealed class PipInteraction : IDisposable
{
    // Opt-in, per-visit and bounded. Contains geometry and window classes only, never UI text or keys.
    internal static bool DiagnoseInput { get; set; } = Environment.GetEnvironmentVariable("WINSCREEN_PIP_INPUT_DIAGNOSTICS") == "1";
    private readonly Queue<string> _diagnostics = new();
    internal string[] InputDiagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }

    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x12, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    private const int VK_MENU = 0x12;
    private const int Keep = 256;

    private readonly nint _source;
    private readonly Windows.WindowIdentity _identity;
    private readonly uint _sourceProcess;
    private readonly HookProc _hook;
    private readonly AutoResetEvent _moved = new(false);
    private readonly Queue<(PressOutcome Outcome, Point At, nint Hit)> _log = new();
    private Thread? _hookThread, _watcher;
    private uint _hookThreadId;
    private volatile bool _stopping;
    private volatile bool _outside;
    private volatile bool _inPopup;
    private long _pointer;
    private long _observedPointer;
    private Rectangle _view;
    private Rectangle _shown;
    private MouseButtons _heldDelivered;
    private MouseButtons _heldSwallowed;
    private Point _dragFrom;
    private bool _dragging;
    private readonly SourcePopups _popups;
    private readonly nint _viewWindow;
    private readonly PipMessageInput _messages;
    private readonly uint _viewThread, _sourceThread;
    private int _sharing; // 1 while the view's thread shares input state with the original's.

    /// <summary>Whether visits share input state with the original, for tests.</summary>
    internal static bool ShareInput { get; set; } = true;

    /// <summary>Whether the view's thread shares input state with the original's right now.</summary>
    internal bool SharesInput => Volatile.Read(ref _sharing) == 1;

    /// <param name="viewWindow">The view itself, which stays under the pointer for the whole visit.</param>
    public PipInteraction(nint source, Rectangle view, Rectangle shown, nint viewWindow = 0, SourcePopups? popups = null)
    {
        _source = source;
        _identity = Windows.WindowIdentity.Capture(source);
        _view = view;
        _shown = shown;
        _hook = Callback;
        _viewWindow = viewWindow;
        _messages = new PipMessageInput(source, _identity);
        _popups = popups ?? new SourcePopups(source);
        _sourceThread = User32.GetWindowThreadProcessId(source, out _sourceProcess);
        _viewThread = viewWindow == 0 ? 0 : User32.GetWindowThreadProcessId(viewWindow, out _);
    }

    /// <summary>
    /// While the pointer is on the view, the view's thread and the original's share input state. The pointer's
    /// shape then follows what the app sets as it handles the moves it is sent: a text cursor over text, a hand
    /// over links. While a button is held it stops, or the app's hold on the mouse would take the real pointer's
    /// moves, at the wrong place, as well as the ones sent to it.
    /// </summary>
    private void Share(bool on)
    {
        if (on)
        {
            if (!ShareInput || _stopping || _viewThread == 0 || _sourceThread == 0 || _viewThread == _sourceThread) return;
            if (Interlocked.CompareExchange(ref _sharing, 1, 0) == 0 && !AttachThreadInput(_viewThread, _sourceThread, true))
                Volatile.Write(ref _sharing, 0);
        }
        else if (Interlocked.Exchange(ref _sharing, 0) == 1)
            AttachThreadInput(_viewThread, _sourceThread, false);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool on);

    /// <summary>Raised on the hook thread for a press on the original's caption buttons or icon.</summary>
    public event Action<CaptionAction>? CaptionPressed;

    /// <summary>Raised on the watcher thread when the original asks something: a dialog, or it was disabled.</summary>
    public event Action? QuestionShown;

    /// <summary>How many sessions exist now, so tests can prove there is no hook once the pointer leaves.</summary>
    internal static int Active { get; private set; }

    /// <summary>Raised on the hook thread when the pointer has left the view.</summary>
    public event Action? Left;

    /// <summary>Raised on the hook thread with how far to move the view.</summary>
    public event Action<Size>? ViewDragged;

    /// <summary>Called with each press's outcome, so a lost press shows up in tests rather than in use.</summary>
    public event Action<PressOutcome>? Pressed;

    internal IReadOnlyList<(PressOutcome Outcome, Point At, nint Hit)> Log
    {
        get { lock (_log) return _log.ToArray(); }
    }

    internal bool InPopup => _inPopup;
    internal (Rectangle View, Rectangle Shown) CurrentGeometry => Geometry();
    internal bool ObservedPointer(Point point) => Interlocked.Read(ref _observedPointer) == Pack(point);
    private static long Pack(Point point) => ((long)point.X << 32) | (uint)point.Y;

    /// <summary>Follows the view when it moves or the source changes shape.</summary>
    public void Update(Rectangle view, Rectangle shown)
    {
        lock (_moved)
        {
            _view = view;
            _shown = shown;
        }
    }

    private (Rectangle View, Rectangle Shown) Geometry()
    {
        lock (_moved) return (_view, _shown);
    }

    private bool InView(Point pointer)
    {
        lock (_moved) return _view.Contains(pointer);
    }

    public void Start(Point pointer)
    {
        Interlocked.Exchange(ref _observedPointer, Pack(pointer));
        Publish(pointer);
        var (view, shown) = Geometry();
        _messages.Move(pointer, view, shown);
        using var ready = new ManualResetEventSlim();
        nint hook = 0;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_MOUSE_LL, _hook, GetModuleHandle(0), 0);
            PeekMessage(out _, 0, 0, 0, 0); // Make the thread-message queue before publishing readiness.
            ready.Set();
            if (hook == 0) return;
            while (GetMessage(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "WinScreen interaction hook", Priority = ThreadPriority.AboveNormal };
        _hookThread.Start();
        ready.Wait();
        if (hook == 0)
            throw new Windows.WindowOperationException(Localization.UiText.T("errors.pip.clickUnverified"));
        _watcher = new Thread(FollowPopups) { IsBackground = true, Name = "WinScreen interaction popups" };
        _watcher.Start();
        Share(true);
        Active++;
    }

    private void Publish(Point pointer)
    {
        Interlocked.Exchange(ref _pointer, Pack(pointer));
        _moved.Set();
    }

    /// <summary>Keeps the original's menus next to the pointer and its display-only popups out of sight.</summary>
    private void FollowPopups()
    {
        try
        {
            while (!_stopping)
            {
                _moved.WaitOne(30);
                if (_stopping || !_identity.StillThere()) return;
                var (view, shown) = Geometry();
                long packed = Interlocked.Read(ref _pointer);
                var pointer = new Point((int)(packed >> 32), (int)packed);
                if (_popups.SweepMapped(view, pointer, _messages.MappedScreen(pointer, view, shown), _viewWindow))
                {
                    QuestionShown?.Invoke();
                    return;
                }
            }
        }
        catch (Windows.WindowOperationException) { Left?.Invoke(); }
    }

    private void Record(PressOutcome outcome, Point at, nint hit, nint queuedTarget = 0, bool? queued = null)
    {
        lock (_log)
        {
            _log.Enqueue((outcome, at, hit));
            if (_log.Count > Keep)
                _log.Dequeue();
        }

        if (DiagnoseInput)
        {
            try
            {
                var (view, shown) = Geometry();
                Point mapped = _messages.MappedScreen(at, view, shown);
                nint target = queuedTarget != 0 ? queuedTarget : _messages.TargetAt(mapped), root = RootAt(at);
                User32.GetWindowRect(_source, out Rect sourceBounds);
                User32.GetWindowRect(target, out Rect targetBounds);
                User32.GetWindowRect(_viewWindow, out Rect viewBounds);
                string entry = $"time={Environment.TickCount64} viewBounds={viewBounds.Left},{viewBounds.Top},{viewBounds.Right},{viewBounds.Bottom} " +
                    $"physical={at} image={view} shown={shown} source={sourceBounds.Left},{sourceBounds.Top},{sourceBounds.Right},{sourceBounds.Bottom} " +
                    $"mapped={mapped} hit={hit}({HitName(hit)}) viewHwnd=0x{_viewWindow:X} sourceHwnd=0x{_source:X} target=0x{target:X} " +
                    $"class={Windows.WindowInfo.ClassName(target)} targetBounds={targetBounds.Left},{targetBounds.Top},{targetBounds.Right},{targetBounds.Bottom} " +
                    $"visible={User32.IsWindowVisible(target)} pid={ProcessOf(target)} physicalRoot=0x{root:X} physicalClass={Windows.WindowInfo.ClassName(root)} " +
                    $"foreground=0x{User32.GetForegroundWindow():X} sharing={SharesInput} sourceExStyle=0x{User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE):X} " +
                    $"border={AtViewBorder(at)} outcome={outcome} queued={queued?.ToString() ?? "not-attempted"} effect=unobserved";
                lock (_diagnostics)
                {
                    _diagnostics.Enqueue(entry);
                    if (_diagnostics.Count > Keep) _diagnostics.Dequeue();
                }
            }
            catch (Windows.WindowOperationException) { }
        }
        Pressed?.Invoke(outcome);
    }

    internal static string HitName(nint hit) => (int)hit switch
    {
        -2 => "HTERROR", -1 => "HTTRANSPARENT", 0 => "HTNOWHERE", 1 => "HTCLIENT", 2 => "HTCAPTION",
        3 => "HTSYSMENU", 4 => "HTGROWBOX", 5 => "HTMENU", 6 => "HTHSCROLL", 7 => "HTVSCROLL",
        8 => "HTMINBUTTON", 9 => "HTMAXBUTTON", 10 => "HTLEFT", 11 => "HTRIGHT", 12 => "HTTOP",
        13 => "HTTOPLEFT", 14 => "HTTOPRIGHT", 15 => "HTBOTTOM", 16 => "HTBOTTOMLEFT", 17 => "HTBOTTOMRIGHT",
        18 => "HTBORDER", 19 => "HTOBJECT", 20 => "HTCLOSE", 21 => "HTHELP", _ => "unknown",
    };

    /// <summary>
    /// Asks the window under the mapped pointer to set its cursor, as Windows would ask it if the pointer were
    /// really there. It doesn't wait on a busy app.
    /// </summary>
    internal void AskForCursor()
    {
        try
        {
            var (view, shown) = Geometry();
            long packed = Interlocked.Read(ref _pointer);
            var pointer = new Point((int)(packed >> 32), (int)packed);
            nint target = _messages.TargetAt(_messages.MappedScreen(pointer, view, shown));
            if (target != 0)
                User32.SendMessageTimeout(target, 0x0020 /* WM_SETCURSOR */, target, (nint)(1 | (0x0200 << 16)), 0x2, 20, out _);
        }
        catch (Windows.WindowOperationException) { }
    }

    private nint Callback(int code, nint message, nint data)
    {
        if (code != 0 || _stopping)
            return CallNextHookEx(0, code, message, data);
        var info = Marshal.PtrToStructure<MouseHookInfo>(data);
        var point = new Point(info.X, info.Y);
        Interlocked.Exchange(ref _observedPointer, Pack(point));
        bool swallow;
        try
        {
            swallow = (int)message switch
            {
                WM_MOUSEMOVE => OnMove(point),
                WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN => OnPress(point, ButtonOf((int)message)),
                WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP => OnRelease(point, ButtonOf((int)message - 1)),
                WM_XBUTTONDOWN => OnPress(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
                WM_XBUTTONUP => OnRelease(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
                WM_MOUSEWHEEL or WM_MOUSEHWHEEL => OnWheel(point, (int)message == WM_MOUSEHWHEEL, info.MouseData),
                _ => false,
            };
        }
        catch (Windows.WindowOperationException)
        {
            _outside = true;
            Left?.Invoke();
            swallow = (int)message != WM_MOUSEMOVE;
        }
        return swallow ? 1 : CallNextHookEx(0, code, message, data);
    }

    private bool OnMove(Point point)
    {
        if (_dragging)
        {
            var moved = new Size(point.X - _dragFrom.X, point.Y - _dragFrom.Y);
            _dragFrom = point;
            ViewDragged?.Invoke(moved);
            return false;
        }
        if (_outside)
            return false;
        if (_heldDelivered != MouseButtons.None)
        {
            // Dragging out of the view keeps a selection going.
            var (view, shown) = Geometry();
            Publish(point);
            _messages.Move(point, view, shown);
            return false;
        }
        nint root = RootAt(point);
        if (root != _viewWindow && root != _source && ProcessOf(root) == _sourceProcess)
        {
            _inPopup = true; // A program's own menu can reach past the view; going there isn't leaving.
            return false;
        }
        if (InView(point))
        {
            var (view, shown) = Geometry();
            Publish(point);
            if (!AtViewBorder(point)) _messages.Move(point, view, shown);
            _inPopup = false;
            return false;
        }
        _outside = true;
        Left?.Invoke();
        return false;
    }

    private bool AtViewBorder(Point point)
    {
        if (_viewWindow == 0 || !User32.GetWindowRect(_viewWindow, out Rect bounds)) return false;
        return PipGeometry.SizingHit(point, Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            Dpi.Scale(PipGeometry.DirectEdgeBand, Dpi.Of(_viewWindow)), Dpi.Scale(PipGeometry.DirectCornerBand, Dpi.Of(_viewWindow))) != 0;
    }

    private bool OnPress(Point point, MouseButtons button)
    {
        if (AtViewBorder(point))
        {
            Record(PressOutcome.ViewControl, point, 0);
            return false;
        }
        nint root = RootAt(point);
        if (root != _viewWindow && ProcessOf(root) == (uint)Environment.ProcessId)
        {
            Record(PressOutcome.ViewControl, point, 0);
            return false;
        }
        if (!_outside && root != _viewWindow && root != _source && ProcessOf(root) == _sourceProcess)
        {
            _inPopup = true;
            Record(PressOutcome.Popup, point, 0);
            return false;
        }
        if (_outside || !InView(point))
        {
            Record(PressOutcome.Outside, point, 0);
            if (!_outside)
            {
                _outside = true;
                Left?.Invoke();
            }
            return false; // It goes to whatever is really there.
        }

        var (view, shown) = Geometry();
        nint hit = _messages.Hit(point, view, shown);
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        if (!alt && PressRules.CaptionActionFor(hit, button) is { } action)
        {
            _heldSwallowed |= button;
            Record(PressOutcome.CaptionButton, point, hit);
            CaptionPressed?.Invoke(action);
            return true;
        }
        if (!alt && PressRules.IsBorder(hit))
        {
            _heldSwallowed |= button; // Nothing happens: the view has its own border.
            Record(PressOutcome.MovedView, point, hit);
            return true;
        }
        if (hit == 2 || alt)
        {
            _heldSwallowed |= button;
            _dragging = button == MouseButtons.Left;
            _dragFrom = point;
            Record(PressOutcome.MovedView, point, hit);
            return true;
        }
        if (!PressRules.ReachesContent(hit))
        {
            _heldSwallowed |= button;
            Record(PressOutcome.Unconfirmed, point, hit);
            return true;
        }
        Share(false);
        User32.SetForegroundWindow(_source);
        bool delivered = _messages.Press(point, button, view, shown);
        if (delivered) _heldDelivered |= button;
        else _heldSwallowed |= button;
        Record(delivered ? PressOutcome.Delivered : PressOutcome.Unconfirmed, point, hit, _messages.PressTarget, delivered);
        return true;
    }

    private bool OnWheel(Point point, bool horizontal, uint mouseData)
    {
        if (_outside || !InView(point))
            return false;
        var (view, shown) = Geometry();
        _messages.Wheel(point, horizontal, mouseData, view, shown);
        return true;
    }

    private static MouseButtons ButtonOf(int message) => message switch
    {
        WM_LBUTTONDOWN => MouseButtons.Left,
        WM_RBUTTONDOWN => MouseButtons.Right,
        _ => MouseButtons.Middle,
    };

    private bool OnRelease(Point point, MouseButtons button)
    {
        bool swallow = (_heldSwallowed & button) != 0;
        if ((_heldDelivered & button) != 0)
        {
            var (view, shown) = Geometry();
            try { _messages.Release(point, button, view, shown); }
            catch (Windows.WindowOperationException) { }
            swallow = true;
        }
        _heldSwallowed &= ~button;
        _heldDelivered &= ~button;
        if (button == MouseButtons.Left) _dragging = false;
        if (_heldDelivered == MouseButtons.None && InView(point)) Share(true);
        nint root = RootAt(point);
        if (!_outside && !InView(point) && !_inPopup && root != 0 && ProcessOf(root) != _sourceProcess)
        {
            _outside = true;
            Left?.Invoke();
        }

        return swallow;
    }

    private static nint RootAt(Point point) =>
        User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);

    private static uint ProcessOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return process;
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        Share(false);
        _moved.Set();
        if (_hookThread is not null)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread.Join(2000);
        }

        _watcher?.Join();
        var (view, shown) = Geometry();
        // A press whose release never came must not leave the button held down in the app.
        try { _messages.ReleaseAll(view, shown); } catch (Windows.WindowOperationException) { }
        if (_watcher is not null)
            Active--;
        _moved.Dispose();
        if (DiagnoseInput && InputDiagnostics.Length > 0)
        {
            // One bounded file, written after the hook has stopped, never from its callback.
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinScreen");
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, "pip-input-last.log"), InputDiagnostics);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public int Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int kind, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg message, nint window, uint first, uint last);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, nint window, uint first, uint last, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(nint name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
