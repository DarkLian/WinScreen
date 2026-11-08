using System.Diagnostics;
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

    /// <summary>Whether interaction can run: sliding across displays of different scaling would reflow the window.</summary>
    public static bool SharedScaling(IEnumerable<int> displayDpis) => displayDpis.Distinct().Count() <= 1;
}

/// <summary>
/// One pointer visit to an interactive view: the hidden source slides so that the point under the pointer
/// is the point the view shows there, and real input reaches the real window. A low-level hook and one
/// mover thread exist only for this visit.
/// </summary>
internal sealed class PipInteraction : IDisposable
{
    private const int MoveGapMilliseconds = 16; // About 60 moves a second; final alignment keeps presses exact.
    private const double AlignMilliseconds = 8;
    private const uint HitTestMilliseconds = 30;
    private const uint AbortIfHung = 0x2;
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x12, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    private const int VK_MENU = 0x12;
    private const int Keep = 256;
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;

    private readonly nint _source;
    private readonly Windows.WindowIdentity _identity;
    private readonly uint _sourceProcess;
    private readonly HookProc _hook;
    private readonly AutoResetEvent _moved = new(false);
    private readonly Queue<(PressOutcome Outcome, Point At, nint Hit)> _log = new();
    private Thread? _hookThread, _mover;
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
    private readonly Dictionary<nint, Point> _hiddenPopups = []; // Mover thread only: popup and where it was.

    public PipInteraction(nint source, Rectangle view, Rectangle shown)
    {
        _source = source;
        _identity = Windows.WindowIdentity.Capture(source);
        _view = view;
        _shown = shown;
        _hook = Callback;
        User32.GetWindowThreadProcessId(source, out _sourceProcess);
    }

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

    /// <summary>Follows the view when it moves or the source changes shape.</summary>
    internal bool InPopup => _inPopup;
    internal bool ObservedPointer(Point point) => Interlocked.Read(ref _observedPointer) == Pack(point);
    private static long Pack(Point point) => ((long)point.X << 32) | (uint)point.Y;

    public void Update(Rectangle view, Rectangle shown)
    {
        lock (_moved)
        {
            _view = view;
            _shown = shown;
        }
    }

    private Point PlaceFor(Point pointer)
    {
        lock (_moved)
        {
            Point spot = PipGeometry.PointToSource(pointer, _view, _shown);
            return new Point(pointer.X - spot.X, pointer.Y - spot.Y);
        }
    }

    private bool InView(Point pointer)
    {
        lock (_moved) return _view.Contains(pointer);
    }

    public void Start(Point pointer)
    {
        Interlocked.Exchange(ref _observedPointer, Pack(pointer));
        Point place = PlaceFor(pointer);
        User32.SetWindowPos(_source, 0, place.X, place.Y, 0, 0, Move);
        Publish(pointer);
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
        _mover = new Thread(MoveWindow) { IsBackground = true, Name = "WinScreen interaction mover" };
        _mover.Start();
        Active++;
    }

    private void Publish(Point pointer)
    {
        Interlocked.Exchange(ref _pointer, ((long)pointer.X << 32) | (uint)pointer.Y);
        _moved.Set();
    }

    private void MoveWindow()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (!_stopping)
            {
                _moved.WaitOne(250);
                if (_stopping)
                    return;
                long wait = MoveGapMilliseconds - clock.ElapsedMilliseconds;
                if (wait > 0)
                    Thread.Sleep((int)wait);
                if (_stopping || !_identity.Matches()) return;
                HideStrayPopups();
                if (_inPopup) continue;
                clock.Restart();
                long packed = Interlocked.Read(ref _pointer);
                Point place = PlaceFor(new Point((int)(packed >> 32), (int)packed));
                User32.SetWindowPos(_source, 0, place.X, place.Y, 0, 0, Move | User32.SWP_ASYNCWINDOWPOS);
            }
        }
        finally
        {
            ShowHiddenPopups();
        }
    }

    /// <summary>
    /// The original's own popups appear where the original really is, not where its view is. Menus and lists
    /// open by the pointer, inside the view, and stay; anything wholly outside the view, such as a browser's
    /// link preview at the original's bottom edge, is moved off the screen until it comes back in or the visit ends.
    /// </summary>
    private void HideStrayPopups()
    {
        Rectangle view;
        lock (_moved) view = _view;
        uint thread = User32.GetWindowThreadProcessId(_source, out _);
        var seen = new HashSet<nint>();
        EnumThreadWindows(thread, (window, _) =>
        {
            if (window != _source && User32.IsWindowVisible(window) &&
                Windows.WindowInfo.IsOwnedBy(window, _source))
                seen.Add(window);
            return true;
        }, 0);
        foreach (nint popup in seen)
        {
            if (!User32.GetWindowRect(popup, out Rect r)) continue;
            bool stray = !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(view);
            // Sent off before but put back by its app: send it off again.
            if (stray && (!_hiddenPopups.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)) HidePopup(popup);
            else if (!stray && _hiddenPopups.ContainsKey(popup)) ShowPopup(popup);
        }
        foreach (nint gone in _hiddenPopups.Keys.Where(popup => !seen.Contains(popup)).ToArray())
            ShowPopup(gone); // Closed, or hidden by its app: put back as it was for next time.
    }

    private void HidePopup(nint popup)
    {
        if (!User32.GetWindowRect(popup, out Rect r)) return;
        _hiddenPopups[popup] = new Point(r.Left, r.Top);
        Rectangle all = SystemInformation.VirtualScreen;
        User32.SetWindowPos(popup, 0, all.Left - (r.Right - r.Left) - 100, all.Top - (r.Bottom - r.Top) - 100, 0, 0,
            Move | User32.SWP_ASYNCWINDOWPOS);
    }

    private void ShowPopup(nint popup)
    {
        if (!_hiddenPopups.Remove(popup, out Point at) || !User32.IsWindow(popup)) return;
        // Only if it's still where it was sent: if the app has placed it since, that place wins.
        if (User32.GetWindowRect(popup, out Rect r) && r.Right <= SystemInformation.VirtualScreen.Left)
            User32.SetWindowPos(popup, 0, at.X, at.Y, 0, 0, Move | User32.SWP_ASYNCWINDOWPOS);
    }

    private void ShowHiddenPopups()
    {
        foreach (nint popup in _hiddenPopups.Keys.ToArray())
            ShowPopup(popup);
    }

    private delegate bool EnumWindowsProc(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, nint data);

    private void Record(PressOutcome outcome, Point at, nint hit)
    {
        lock (_log)
        {
            _log.Enqueue((outcome, at, hit));
            if (_log.Count > Keep)
                _log.Dequeue();
        }

        Pressed?.Invoke(outcome);
    }

    private nint Callback(int code, nint message, nint data)
    {
        if (code != 0 || _stopping)
            return CallNextHookEx(0, code, message, data);
        var info = Marshal.PtrToStructure<MouseHookInfo>(data);
        var point = new Point(info.X, info.Y);
        Interlocked.Exchange(ref _observedPointer, Pack(point));
        bool swallow = (int)message switch
        {
            WM_MOUSEMOVE => OnMove(point),
            WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN => OnPress(point, ButtonOf((int)message)),
            WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP => OnRelease(point, ButtonOf((int)message - 1)),
            WM_XBUTTONDOWN => OnPress(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
            WM_XBUTTONUP => OnRelease(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
            WM_MOUSEWHEEL or WM_MOUSEHWHEEL => _outside || !_identity.StillThere() || ProcessOf(RootAt(point)) != _sourceProcess,
            _ => false,
        };
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

        if (_heldDelivered != MouseButtons.None)
        {
            Publish(point); // Dragging out of the view keeps a selection going.
            return false;
        }

        if (InView(point))
        {
            _inPopup = false;
            if (!_outside)
                Publish(point);
            return false;
        }

        if (_outside)
            return false;
        // A program's own menu can reach past the view; going there isn't leaving.
        nint root = RootAt(point);
        if (root != _source && ProcessOf(root) == _sourceProcess)
        {
            _inPopup = true;
            return false;
        }

        _outside = true;
        Left?.Invoke();
        return false;
    }

    private bool OnPress(Point point, MouseButtons button)
    {
        if (_outside || (!InView(point) && !_inPopup))
        {
            Record(PressOutcome.Outside, point, 0);
            _heldSwallowed |= button;
            if (!_outside)
            {
                _outside = true;
                Left?.Invoke();
            }

            return true;
        }

        nint root = RootAt(point);
        if (ProcessOf(root) == (uint)Environment.ProcessId)
        {
            Record(PressOutcome.ViewControl, point, 0);
            return false;
        }

        if (root != _source && ProcessOf(root) == _sourceProcess)
        {
            if (!_identity.StillThere()) { _heldSwallowed |= button; return true; }
            Record(PressOutcome.Popup, point, 0);
            _heldDelivered |= button;
            return false;
        }

        if (!_identity.StillThere() || root != _source || !Align(point))
        {
            Record(PressOutcome.Unconfirmed, point, 0);
            _heldSwallowed |= button;
            return true;
        }

        nint position = (nint)((point.Y << 16) | (point.X & 0xFFFF));
        nint hit = -2;
        bool answered = User32.SendMessageTimeout(_source, User32.WM_NCHITTEST, 0, position, AbortIfHung, HitTestMilliseconds, out hit) != 0;
        if (!answered || !_identity.StillThere() || RootAt(point) != _source)
        {
            Record(PressOutcome.Unconfirmed, point, 0);
            _heldSwallowed |= button;
            return true;
        }

        if (!PressRules.ReachesContent(hit) || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            Record(PressOutcome.MovedView, point, hit);
            _heldSwallowed |= button;
            _dragging = button == MouseButtons.Left;
            _dragFrom = point;
            return true;
        }

        Record(PressOutcome.Delivered, point, hit);
        _heldDelivered |= button;
        return false;
    }

    private static MouseButtons ButtonOf(int message) => message switch
    {
        WM_LBUTTONDOWN => MouseButtons.Left,
        WM_RBUTTONDOWN => MouseButtons.Right,
        _ => MouseButtons.Middle,
    };

    private bool OnRelease(Point point, MouseButtons button)
    {
        bool swallow = (_heldSwallowed & button) != 0 ||
            (_heldDelivered & button) != 0 && !_identity.StillThere();
        _heldSwallowed &= ~button;
        _heldDelivered &= ~button;
        if (button == MouseButtons.Left) _dragging = false;
        if (!_outside && !InView(point) && !_inPopup && RootAt(point) != 0 && ProcessOf(RootAt(point)) != _sourceProcess)
        {
            _outside = true;
            Left?.Invoke();
        }

        return swallow;
    }

    /// <summary>Puts the window exactly right before a press goes on, without waiting on a stuck app.</summary>
    private bool Align(Point point)
    {
        Point place = PlaceFor(point);
        User32.SetWindowPos(_source, 0, place.X, place.Y, 0, 0, Move | User32.SWP_ASYNCWINDOWPOS);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < AlignMilliseconds)
        {
            if (User32.GetWindowRect(_source, out Rect now) && now.Left == place.X && now.Top == place.Y)
                return true;
            Thread.SpinWait(200);
        }

        return false;
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
        _moved.Set();
        if (_hookThread is not null)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread.Join(2000);
        }

        _mover?.Join();
        if (_mover is not null)
            Active--;
        _moved.Dispose();
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
