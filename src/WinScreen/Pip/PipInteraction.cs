using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

// Every press gets exactly one.
public enum PressOutcome
{
    Delivered,
    Popup,
    ViewControl,
    MovedView,
    Outside,
    Unconfirmed,
    CaptionButton,
    Unsupported,
    ScrollMode,
}

public enum CaptionAction
{
    Close,
    // The original never minimizes, or its picture would stop.
    Minimize,
    GoToOriginal,
    Menu,
}

internal static class PressRules
{
    private const int Client = 1, Menu = 5, VerticalScroll = 6, HorizontalScroll = 7;

    // Content only: a double-click on an invisible window's title bar would maximize it, and a drag would start a
    // system move nobody can see.
    public static bool ReachesContent(nint hit) => (int)hit is Client or Menu or VerticalScroll or HorizontalScroll;

    private const int Caption = 2, SystemMenu = 3, MinButton = 8, MaxButton = 9, Left = 10, BottomRight = 17, Border = 18, Close = 20;

    // A title bar moves the view; a border does nothing, since resizing the original would reflow what the view shows.
    public static CaptionAction? CaptionActionFor(nint hit, MouseButtons button) => ((int)hit, button) switch
    {
        (Close, MouseButtons.Left) => CaptionAction.Close,
        (MinButton, MouseButtons.Left) => CaptionAction.Minimize,
        (MaxButton, MouseButtons.Left) => CaptionAction.GoToOriginal,
        (SystemMenu, MouseButtons.Left or MouseButtons.Right) or (Caption, MouseButtons.Right) => CaptionAction.Menu,
        _ => null,
    };

    public static bool IsBorder(nint hit) => (int)hit is >= Left and <= BottomRight or Border;
}

// Windows drops a low-level hook that answers slowly, so the hook only decides from WinScreen's own state and hands the
// rest, in order, to the input worker. Only the worker talks to the other app.
internal sealed class PipInteraction : IDisposable
{
    // Opt-in, per visit and bounded: geometry and window classes only, never UI text or keys.
    internal static bool DiagnoseInput { get; set; } = Environment.GetEnvironmentVariable("WINSCREEN_PIP_INPUT_DIAGNOSTICS") == "1";
    private readonly Queue<string> _diagnostics = new();
    internal string[] InputDiagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }

    // The last button events, for a test whose press went astray: what the hook saw and kept, what the worker did.
    private readonly (long At, int What, int Data)[] _buttonTrail = new (long, int, int)[32];
    private int _trailNext;

    private void Trail(int what, int data) =>
        _buttonTrail[(Interlocked.Increment(ref _trailNext) - 1) & 31] = (Environment.TickCount64, what, data);

    internal string ButtonTrail
    {
        get
        {
            int next = Volatile.Read(ref _trailNext);
            return string.Join(" ", Enumerable.Range(Math.Max(0, next - 32), Math.Min(next, 32))
                .Select(i => _buttonTrail[i & 31])
                .Select(e => $"{e.At % 100000}:" + (e.What switch
                {
                    -1 => "press", -2 => "release", -3 => "dragPickedUp", WM_MOUSEMOVE => "heldMove", _ => $"hook{e.What:X}",
                }) + (e.What is -3 or WM_MOUSEMOVE ? $"/{TrailText(e.Data)}" : $"/{e.Data}")));
        }
    }

    // x and y in 14 bits each, and 2 bits of flags (1: made by a program).
    private static int TrailPoint(Point point, uint flags) => (int)(flags << 28) | (point.X & 0x3FFF) << 14 | point.Y & 0x3FFF;

    private static string TrailText(int data) => $"{(data >> 14) & 0x3FFF},{data & 0x3FFF}" + ((data >> 28 & 3) != 0 ? "i" : "");

    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x12, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    private const int VK_MENU = 0x12;
    private const int Keep = 256;
    // Moves are merged while the worker is behind; presses and releases never are. A stuck worker can't grow this.
    private const int MaxQueued = 512;

    private readonly nint _source;
    private readonly Windows.WindowIdentity _identity;
    internal Windows.WindowIdentity Identity => _identity;
    private readonly uint _sourceProcess;
    private readonly User32.HookProc _hook;
    private readonly AutoResetEvent _moved = new(false);
    private readonly Queue<(PressOutcome Outcome, Point At, nint Hit)> _log = new();
    private Thread? _hookThread, _watcher, _worker;
    private uint _hookThreadId;
    private volatile bool _stopping;
    private volatile bool _outside;
    private volatile bool _inPopup;
    private long _pointer;
    private long _observedPointer;
    private PipMapping _map;
    private readonly SourcePopups _popups;
    private readonly nint _viewWindow;
    private readonly PipMessageInput _messages;
    private readonly uint _viewThread, _sourceThread;
    private int _sharing; // 1 while the view's thread shares input state with the original's.
    private volatile bool _appBusy; // The app stopped answering; sharing waits until it answers again.

    // The buttons whose press the hook kept from the desktop, so their releases are kept too. Written by the hook, and
    // by the view for a press that reached it first; the worker reads it to know whether a newer press is in progress.
    private int _swallowedBits;
    private MouseButtons _swallowed
    {
        get => (MouseButtons)Volatile.Read(ref _swallowedBits);
        set => Volatile.Write(ref _swallowedBits, (int)value);
    }

    // Of those, the ones whose press reached the view, not the hook: Windows counts that button as down, so its release
    // must pass, or Windows holds the button until the next press and every move is a drag.
    private int _reachedViewBits;

    // Input worker only.
    private MouseButtons _heldDelivered;
    private MouseButtons _pendingClick; // A deferred press: its outcome comes once the control is activated.
    private Point _dragFrom;
    private bool _dragging;
    private volatile bool _middleClickScroll;
    internal bool MiddleClickScroll { get => _middleClickScroll; set => _middleClickScroll = value; }
    internal volatile bool EnhancedHover;

    internal volatile bool AppScrollsWithHover;

    internal volatile Compatibility.DragHandOver HandsOverDrags;
    private int _itemQuery; // Each press's question, so a late answer to an earlier one is ignored.

    internal string LastItemAnswer { get; private set; } = "";

    internal event Action<Point>? DragPickedUp;

    private Point _pressAt;
    private bool _leftWaitsForDrag;
    private long _leftWaiting; // That press's number.
    private readonly LeftPress _left = new();

    internal volatile string PressedItem = "";

    // 1 yes, 0 no, -1 not known yet.
    private volatile int _pressOnItem = -1;
    private volatile bool _pressHandedOver, _handedOverReleased;

    internal bool HandedOverReleased => _handedOverReleased;
    internal string HoverStatus => $"{(EnhancedHover && _messages.MoveRoute != InputRoute.Messages ? "unavailable: " + _messages.MoveRoute + " route" : _messages.Hover.Status)}; target=0x{_messages.MoveTarget:X}; busy={_appBusy}; outside={_outside}; popup={_inPopup}; enabled={EnhancedHover}; install={_messages.Hover.InstallMilliseconds:F3}ms uninstall={_messages.Hover.UninstallMilliseconds:F3}ms";
    private volatile bool _scrolling;
    internal bool Scrolling => _scrolling;
    private Point _scrollAnchor, _scrollPoint;
    private long _scrollTick;
    private double _scrollX, _scrollY;
    internal Cursor ScrollCursor => _scrollPoint.Y < _scrollAnchor.Y - 12 ? Cursors.PanNorth
        : _scrollPoint.Y > _scrollAnchor.Y + 12 ? Cursors.PanSouth
        : _scrollPoint.X < _scrollAnchor.X - 12 ? Cursors.PanWest
        : _scrollPoint.X > _scrollAnchor.X + 12 ? Cursors.PanEast : Cursors.NoMove2D;

    internal void CancelScroll() => Enqueue(new(EventKind.CancelScroll, default));

    private readonly List<InputEvent> _queue = [];
    private readonly AutoResetEvent _work = new(false);

    private enum EventKind { Move, Press, Release, Wheel, Record, CancelScroll }

    private readonly record struct InputEvent(EventKind Kind, Point At, MouseButtons Button = MouseButtons.None,
        bool Alt = false, bool Horizontal = false, uint Data = 0, PressOutcome Outcome = default, bool EndsScroll = false,
        bool OnLink = false, long Press = 0);

    internal static bool ShareInput { get; set; } = true;

    internal bool SharesInput => Volatile.Read(ref _sharing) == 1;

    public PipInteraction(nint source, PipMapping map, nint viewWindow = 0, SourcePopups? popups = null)
    {
        _source = source;
        _identity = Windows.WindowIdentity.Capture(source);
        _map = map;
        _hook = Callback;
        _viewWindow = viewWindow;
        _messages = new PipMessageInput(source, _identity);
        _popups = popups ?? new SourcePopups(source);
        _sourceThread = User32.GetWindowThreadProcessId(source, out _sourceProcess);
        _viewThread = viewWindow == 0 ? 0 : User32.GetWindowThreadProcessId(viewWindow, out _);
    }

    // While a button is held sharing stops, or the app's hold on the mouse would take the real pointer's moves, at the
    // wrong place.
    private bool Share(bool on)
    {
        if (on)
        {
            if (!ShareInput || _stopping || _appBusy || _viewThread == 0 || _sourceThread == 0 || _viewThread == _sourceThread) return false;
            if (Interlocked.CompareExchange(ref _sharing, 1, 0) != 0) return false;
            bool keysWithApp = User32.GetForegroundWindow() == _source;
            if (!User32.AttachThreadInput(_viewThread, _sourceThread, true))
            {
                Volatile.Write(ref _sharing, 0);
                return false;
            }
            // Joined input state takes the view thread's own active window. Every view shares one thread, so that can
            // be another view: with two views used directly, the keys went to a view instead of the app just clicked.
            if (keysWithApp && User32.GetForegroundWindow() != _source) User32.SetForegroundWindow(_source);
            return false;
        }
        if (Interlocked.Exchange(ref _sharing, 0) != 1) return false;
        User32.AttachThreadInput(_viewThread, _sourceThread, false);
        return true;
    }

    // Stopping input sharing makes Windows recheck the real pointer a moment later and tell the app it is elsewhere.
    // Arriving after the press, the app takes that as leaving while held (Chromium then restarts its click count).
    // The same recheck sends the view a move, so the press waits for it, up to this long: it came within 5 ms on
    // Windows 11 and in 31 to 57 ms in a Windows 10 VM.
    private const int MaxRecheckWait = 100;
    private const int ItemAnswerWait = 200;

    // Set once the view has had a pointer move after the hook stopped sharing for the latest press.
    private readonly ManualResetEventSlim _rechecked = new(true);
    private int _unsharedAt; // Environment.TickCount when sharing stopped, comparable with message times.

    internal double LastRecheckWait { get; private set; }

    internal void ViewPointerMoved(int messageTime)
    {
        if (!_rechecked.IsSet && messageTime - Volatile.Read(ref _unsharedAt) >= 0) _rechecked.Set();
    }

    // Raised on the input worker.
    public event Action<CaptionAction>? CaptionPressed;

    // Raised on the watcher thread.
    public event Action? QuestionShown;

    internal static int Active => Volatile.Read(ref _active);
    private static int _active;

    // Raised on the hook thread or the input worker.
    public event Action? Left;

    // Raised on the input worker.
    public event Action<Size>? ViewDragged;

    // A click on a pointer-only surface reports once the control was activated, which can be after the visit ended.
    public event Action<PressOutcome>? Pressed;

    // Only presses that tell the person something about the app: not ones on the hidden window's frame, nor ones cut
    // short by the visit ending.
    public event Action<PressOutcome>? Answered;

    internal IReadOnlyList<(PressOutcome Outcome, Point At, nint Hit)> Log
    {
        get { lock (_log) return _log.ToArray(); }
    }

    internal void ForgetPresses()
    {
        lock (_log) _log.Clear();
    }

    internal bool InPopup => _inPopup;
    internal (Rectangle View, Rectangle Shown) CurrentGeometry => (Geometry().View, Geometry().Shown);
    internal PipMapping CurrentMapping => Geometry();
    internal bool ObservedPointer(Point point) => Interlocked.Read(ref _observedPointer) == Pack(point);
    private static long Pack(Point point) => ((long)point.X << 32) | (uint)point.Y;

    internal PipHookTiming Timing { get; } = new();

    public void Update(PipMapping map)
    {
        lock (_moved) _map = map;
    }

    private PipMapping Geometry()
    {
        lock (_moved) return _map;
    }

    private bool InView(Point pointer)
    {
        lock (_moved) return _map.View.Contains(pointer);
    }

    public void Start(Point pointer)
    {
        _identity.Require(); // The full check, process included, once; moves use the quick one.
        Interlocked.Exchange(ref _observedPointer, Pack(pointer));
        Publish(pointer);
        PipMapping map = Geometry();
        _messages.EnhancedHover = EnhancedHover;
        _messages.Move(pointer, map);
        _worker = new Thread(Work) { IsBackground = true, Name = "WinScreen interaction input", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        using var ready = new ManualResetEventSlim();
        nint hook = 0;
        int hookError = 0;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = Kernel32.GetCurrentThreadId();
            hook = User32.SetWindowsHookEx(WH_MOUSE_LL, _hook, Kernel32.GetModuleHandle(0), 0);
            if (hook == 0) hookError = Marshal.GetLastWin32Error();
            User32.PeekMessage(out _, 0, 0, 0, 0); // Make the thread-message queue before publishing readiness.
            ready.Set();
            if (hook == 0) return;
            while (User32.GetMessage(out _, 0, 0, 0) > 0) { }
            User32.UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "WinScreen interaction hook", Priority = ThreadPriority.AboveNormal };
        _hookThread.Start();
        ready.Wait();
        if (hook == 0)
        {
            Diagnostics.Log.Event("hook.visit.failed", ("code", Diagnostics.LogValue.Code(hookError)));
            _stopping = true;
            _work.Set();
            _worker.Join(2000);
            throw new Windows.WindowOperationException(Localization.UiText.T("errors.pip.clickUnverified"));
        }
        // The visit began where the view last saw the pointer, which can be stale: a move made before the hook existed
        // reached no one. From here every move is seen; take where the pointer really is now.
        if (User32.GetCursorPos(out NativePoint now) && InView(new Point(now.X, now.Y)) && Pack(new Point(now.X, now.Y)) != Interlocked.Read(ref _pointer))
        {
            Interlocked.Exchange(ref _observedPointer, Pack(new Point(now.X, now.Y)));
            Publish(new Point(now.X, now.Y));
            Enqueue(new(EventKind.Move, new Point(now.X, now.Y)));
        }
        _watcher = new Thread(FollowPopups) { IsBackground = true, Name = "WinScreen interaction popups" };
        _watcher.Start();
        _menuOpened = PopupGuard.Watch(_sourceProcess, () => _moved.Set());
        if (InputMethodWindows.Wanted)
            _inputMethod = new InputMethodWindows(_source, Geometry, () =>
                Interlocked.Read(ref _lastPress) is var packed and not long.MinValue ? new Point((int)(packed >> 32), (int)packed) : null);
        Share(true);
        Interlocked.Increment(ref _active);
    }

    private void Publish(Point pointer)
    {
        Interlocked.Exchange(ref _pointer, Pack(pointer));
        _moved.Set();
    }

    private void FollowPopups()
    {
        try
        {
            while (!_stopping)
            {
                _moved.WaitOne(30);
                if (_stopping || !_identity.StillThere()) return;
                PipMapping map = Geometry();
                long packed = Interlocked.Read(ref _pointer);
                var pointer = new Point((int)(packed >> 32), (int)packed);
                if (_popups.SweepMapped(map.View, pointer, _messages.MappedScreen(pointer, map), _viewWindow))
                {
                    QuestionShown?.Invoke();
                    return;
                }
            }
        }
        catch (Windows.WindowOperationException) { Leave(); }
    }

    private void Leave()
    {
        if (_outside && _stopping) return;
        _outside = true;
        Left?.Invoke();
    }

    // ---- Hook thread: decide from WinScreen's own state only, then queue. ----

    private nint Callback(int code, nint message, nint data)
    {
        if (code != 0 || _stopping)
            return User32.CallNextHookEx(0, code, message, data);
        long started = PipHookTiming.Start();
        bool swallow;
        try { swallow = Decide(message, data); }
        finally { Timing.Stop(started); }
        if ((int)message != WM_MOUSEMOVE) Trail((int)message, swallow ? 1 : 0);
        return swallow ? 1 : User32.CallNextHookEx(0, code, message, data);
    }

    private bool Decide(nint message, nint data)
    {
        var info = Marshal.PtrToStructure<MouseHookInfo>(data);
        var point = new Point(info.X, info.Y);
        Interlocked.Exchange(ref _observedPointer, Pack(point));
        // A move while the left button is held can start a drag: where it went, and whether Windows marks it as made by a
        // program (TD-77: a move nobody sent turned a test's click into a drag).
        if ((int)message == WM_MOUSEMOVE && (Volatile.Read(ref _swallowedBits) & (int)MouseButtons.Left) != 0)
            Trail(WM_MOUSEMOVE, TrailPoint(point, info.Flags & 3));
        return (int)message switch
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

    private void Enqueue(InputEvent input)
    {
        lock (_queue)
        {
            if (input.Kind == EventKind.Move && _queue.Count > 0 && _queue[^1].Kind == EventKind.Move)
                _queue[^1] = input;
            else if (input.Kind != EventKind.Move || _queue.Count < MaxQueued)
                _queue.Add(input);
        }
        _work.Set();
    }

    private bool OnMove(Point point)
    {
        if (_swallowed != MouseButtons.None)
        {
            // A held press keeps going, out of the view too: a selection, a scroll bar, or the view being moved.
            Enqueue(new(EventKind.Move, point));
            return false;
        }
        if (_outside)
            return false;
        nint root = RootAt(point);
        if (IsSourcePopup(root))
        {
            _inPopup = true; // A program's own menu can reach past the view; going there isn't leaving.
            return false;
        }
        if (InView(point))
        {
            _inPopup = false;
            Enqueue(new(EventKind.Move, point));
            return false;
        }
        Leave();
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
            Enqueue(new(EventKind.Record, point, Outcome: PressOutcome.ViewControl));
            return false;
        }
        nint root = RootAt(point);
        if (root != _viewWindow && ProcessOf(root) == (uint)Environment.ProcessId)
        {
            Enqueue(new(EventKind.Record, point, Outcome: PressOutcome.ViewControl));
            return false;
        }
        if (!_outside && IsSourcePopup(root))
        {
            _inPopup = true;
            Enqueue(new(EventKind.Record, point, Outcome: PressOutcome.Popup));
            return false;
        }
        // A shell panel open over everything (the emoji panel, the volume or network panels) closes on a press
        // anywhere else, through a see-through layer it lays over the screen: that press is the shell's, as it would be
        // without the view.
        if (!_outside && Windows.WindowInfo.ClassName(root) == LightDismissLayer)
        {
            Enqueue(new(EventKind.Record, point, Outcome: PressOutcome.Popup));
            return false;
        }
        if (_outside || !InView(point))
        {
            Enqueue(new(EventKind.Record, point, Outcome: PressOutcome.Outside));
            if (!_outside) Leave();
            return false; // It goes to whatever is really there.
        }
        // Everything inside the picture is kept from the desktop; the worker decides what it means.
        Interlocked.Exchange(ref _lastPress, Pack(point));
        Interlocked.And(ref _reachedViewBits, ~(int)button);
        long press = 0;
        if (button == MouseButtons.Left)
        {
            press = _left.Pressed();
            _pressHandedOver = _handedOverReleased = false;
        }
        bool alt = (User32.GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        // Over a link the app shows a hand, and the pointer still has the app's shape here (sharing stops just below).
        bool onLink = button == MouseButtons.Left && HandsOverDrags == Compatibility.DragHandOver.Links && PointerShape.IsHand();
        if (!alt)
        {
            // Both return at once. They must happen here, while the hook holds the press: from another thread
            // Windows 10 may refuse the focus change, and an app that takes the mouse while still sharing input
            // with the view would also get the real pointer's moves.
            _rechecked.Reset();
            Volatile.Write(ref _unsharedAt, Environment.TickCount);
            if (!Share(false)) _rechecked.Set(); // Nothing changed, so there is nothing to wait for.
            User32.SetForegroundWindow(_source);
        }
        Interlocked.Or(ref _swallowedBits, (int)button);
        // Scrolling stops with the press itself, not when the worker reaches it: it may still be busy sending steps.
        bool endsScroll = _scrolling;
        if (endsScroll) _scrolling = false;
        Enqueue(new(EventKind.Press, point, button, Alt: alt, EndsScroll: endsScroll, OnLink: onLink, Press: press));
        return true;
    }

    // A right press that reached the view itself, just before this visit's hook was there: it goes on as the hook sends
    // one. The view's thread got it, so it may bring the app forward.
    internal void TakePress(Point point, MouseButtons button)
    {
        if (_stopping || !InView(point)) return;
        Interlocked.Exchange(ref _lastPress, Pack(point));
        bool alt = (User32.GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        if (!alt)
        {
            _rechecked.Reset();
            Volatile.Write(ref _unsharedAt, Environment.TickCount);
            if (!Share(false)) _rechecked.Set();
            User32.SetForegroundWindow(_source);
        }
        Interlocked.Or(ref _reachedViewBits, (int)button);
        Interlocked.Or(ref _swallowedBits, (int)button);
        Enqueue(new(EventKind.Press, point, button, Alt: alt, Press: button == MouseButtons.Left ? _left.Pressed() : 0));
    }

    // Its release, when that reached the view too and not the hook.
    internal void TakeRelease(Point point, MouseButtons button)
    {
        if (button == MouseButtons.Left) _left.Released();
        if ((Interlocked.And(ref _swallowedBits, ~(int)button) & (int)button) != 0)
            Enqueue(new(EventKind.Release, point, button));
    }

    private bool OnWheel(Point point, bool horizontal, uint mouseData)
    {
        if (_outside || !InView(point))
            return false;
        // The input method's list over the picture pages with the wheel itself.
        if (_inputMethod?.Holds(RootAt(point)) == true)
            return false;
        Enqueue(new(EventKind.Wheel, point, Horizontal: horizontal, Data: mouseData));
        return true;
    }

    private bool OnRelease(Point point, MouseButtons button)
    {
        if (button == MouseButtons.Left && _left.Released())
        {
            Interlocked.And(ref _swallowedBits, ~(int)button); // The real window has the press now: the release is its own.
            _handedOverReleased = true;
            return false;
        }
        bool swallow = (Interlocked.And(ref _swallowedBits, ~(int)button) & (int)button) != 0;
        if (swallow) Enqueue(new(EventKind.Release, point, button));
        // It reaches the view, which knows it was handled (PipWindow.WndProc).
        if ((Interlocked.And(ref _reachedViewBits, ~(int)button) & (int)button) != 0) swallow = false;
        if (_swallowed == MouseButtons.None && !_outside && !InView(point) && !_inPopup)
        {
            nint root = RootAt(point);
            if (root != 0 && !IsSourcePopup(root) && root != _source) Leave();
        }
        return swallow;
    }

    private static MouseButtons ButtonOf(int message) => message switch
    {
        WM_LBUTTONDOWN => MouseButtons.Left,
        WM_RBUTTONDOWN => MouseButtons.Right,
        _ => MouseButtons.Middle,
    };

    // ---- Input worker: everything that talks to the other app, in the order it happened. ----

    private void Work()
    {
        var batch = new List<InputEvent>();
        while (true)
        {
            _work.WaitOne(_scrolling ? 16 : 100);
            lock (_queue)
            {
                batch.AddRange(_queue);
                _queue.Clear();
            }
            foreach (InputEvent input in batch)
            {
                try { Handle(input); }
                catch (Windows.WindowOperationException) { Leave(); }
            }
            batch.Clear();
            if (!EnhancedHover || _outside || _inPopup || _stopping || _appBusy) _messages.Hover.Dispose();
            else _messages.Hover.Renew();
            if (_scrolling && !_stopping)
            {
                try { ScrollTick(); }
                catch (Windows.WindowOperationException) { _scrolling = false; Leave(); }
            }
            if (_stopping)
            {
                lock (_queue) if (_queue.Count == 0) { _messages.Hover.Dispose(); return; }
            }
        }
    }

    private void Handle(InputEvent input)
    {
        switch (input.Kind)
        {
            case EventKind.Record: Record(input.Outcome, input.At, 0); break;
            case EventKind.Move: MoveArrived(input.At); break;
            case EventKind.Press: Trail(-1, (int)input.Button >> 20); PressArrived(input.At, input.Button, input.Alt, input.EndsScroll, input.OnLink, input.Press); break;
            case EventKind.Release: Trail(-2, (int)input.Button >> 20); ReleaseArrived(input.At, input.Button); break;
            case EventKind.Wheel:
                StopScroll();
                PipMapping map = Geometry();
                _messages.Wheel(input.At, input.Horizontal, input.Data, map);
                break;
            case EventKind.CancelScroll:
                StopScroll();
                break;
        }
    }

    private void MoveArrived(Point point)
    {
        if (_stopping && EnhancedHover) return;
        if (_dragging)
        {
            var moved = new Size(point.X - _dragFrom.X, point.Y - _dragFrom.Y);
            _dragFrom = point;
            ViewDragged?.Invoke(moved);
            return;
        }
        if (_pressHandedOver) return; // The real window takes the pointer from here.
        if (_leftWaitsForDrag)
        {
            if (_stopping || Math.Abs(point.X - _pressAt.X) <= SystemInformation.DragSize.Width / 2 &&
                Math.Abs(point.Y - _pressAt.Y) <= SystemInformation.DragSize.Height / 2) return;
            // Usually known long since; a slow answer doesn't hold the pointer for more than a moment.
            for (long until = Environment.TickCount64 + ItemAnswerWait; _pressOnItem < 0 && Environment.TickCount64 < until;) Thread.Sleep(5);
            if (_pressOnItem == 1)
            {
                // Let go already: the release waiting in the queue makes it a click.
                if (!_left.HandOver(_leftWaiting)) return;
                _leftWaitsForDrag = false;
                Trail(-3, TrailPoint(_pressAt, 0)); // Where it was pressed; the held moves above say where it went.
                _pressHandedOver = true;
                DragPickedUp?.Invoke(_pressAt);
                return;
            }
            _leftWaitsForDrag = false;
            // Not on an item (empty space selects what it crosses): the press goes as messages after all, and the move.
            Delivery late = _messages.Press(_pressAt, MouseButtons.Left, Geometry(), menuBar: false);
            if (late == Delivery.Posted) _heldDelivered |= MouseButtons.Left;
            if (late == Delivery.Deferred) _pendingClick |= MouseButtons.Left;
            else Record(Outcome(late), _pressAt, 1, _messages.PressTarget, late == Delivery.Posted, _messages.Why);
        }
        PipMapping map = Geometry();
        Publish(point);
        if (_scrolling) { _scrollPoint = point; return; }
        // Held presses keep going everywhere; otherwise the view's own sizing border isn't the app's.
        if (_heldDelivered == MouseButtons.None && (_pendingClick != MouseButtons.None || AtViewBorder(point))) return;
        if (_heldDelivered == MouseButtons.None && !map.View.Contains(point)) return;
        _messages.EnhancedHover = EnhancedHover && !_outside && !_inPopup && !_appBusy;
        _messages.Move(point, map);
        // Sharing input with a thread that has stopped reading it would hold up WinScreen's own input too. The
        // cursor question doubles as the check: no answer within 20 ms stops sharing until the app answers again.
        if (SharesInput || _appBusy)
        {
            bool answered = _messages.AskForCursor();
            if (!answered && !_appBusy) { _appBusy = true; Share(false); }
            else if (answered && _appBusy)
            {
                _appBusy = false;
                if (_heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None && Volatile.Read(ref _swallowedBits) == 0)
                    Share(true);
            }
        }
    }

    private void PressArrived(Point point, MouseButtons button, bool alt, bool endsScroll = false, bool onLink = false, long press = 0)
    {
        if (_scrolling || endsScroll)
        {
            _scrolling = false;
            if (!alt && button is MouseButtons.Left or MouseButtons.Middle)
            { Record(PressOutcome.ScrollMode, point, 1); return; }
        }
        // Not before Windows has told the app where the real pointer is (see MaxRecheckWait).
        long waited = Stopwatch.GetTimestamp();
        _rechecked.Wait(MaxRecheckWait);
        LastRecheckWait = Stopwatch.GetElapsedTime(waited).TotalMilliseconds;
        PipMapping map = Geometry();
        nint hit = _messages.Hit(point, map);
        if (!alt && PressRules.CaptionActionFor(hit, button) is { } action)
        {
            Record(PressOutcome.CaptionButton, point, hit);
            CaptionPressed?.Invoke(action);
            return;
        }
        if (!alt && PressRules.IsBorder(hit))
        {
            Record(PressOutcome.MovedView, point, hit); // Nothing happens: the view has its own border.
            return;
        }
        if (hit == 2 || alt)
        {
            _dragging = button == MouseButtons.Left;
            _dragFrom = point;
            Record(PressOutcome.MovedView, point, hit);
            return;
        }
        if (!PressRules.ReachesContent(hit))
        {
            Record(PressOutcome.Unconfirmed, point, hit, answers: false);
            return;
        }
        if (!alt && button == MouseButtons.Middle && MiddleClickScroll && !(AppScrollsWithHover && _messages.Hover.Installed) &&
            _heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None && _messages.CanScroll(point, map))
        {
            _scrollAnchor = _scrollPoint = point;
            _scrollX = _scrollY = 0;
            _scrollTick = Stopwatch.GetTimestamp();
            _scrolling = true;
            Record(PressOutcome.ScrollMode, point, hit);
            return;
        }
        // In a page the same press and move mostly selects text, which works through the view as it is: only a press on
        // a
        // link, known by the hand the page shows there, waits to see whether it is a drag. Nothing is asked of the
        // page.
        if (HandsOverDrags == Compatibility.DragHandOver.Links && onLink && button == MouseButtons.Left && !alt && hit == 1 &&
            _heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None)
        {
            Interlocked.Increment(ref _itemQuery);
            PressedItem = "";
            _pressOnItem = 1;
            _pressAt = point; // Click or drag: the next moves or the release say which.
            _leftWaitsForDrag = true;
            _leftWaiting = press;
            return;
        }
        if (HandsOverDrags == Compatibility.DragHandOver.Items && button == MouseButtons.Left && !alt && hit == 1 &&
            _heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None)
        {
            _pressAt = point; // Click or drag: the next moves or the release say which.
            _leftWaitsForDrag = true;
            _leftWaiting = press;
            _pressOnItem = -1;
            int query = Interlocked.Increment(ref _itemQuery);
            long asked = Environment.TickCount64;
            Point mapped = _messages.MappedScreen(point, map);
            if (!PipControlInput.Shared.Submit(_identity, _messages.TargetAt(mapped), mapped, PipControlInput.Request.IsItem,
                    result =>
                    {
                        if (query != Volatile.Read(ref _itemQuery)) return;
                        LastItemAnswer = $"{result.What} after {Environment.TickCount64 - asked} ms";
                        bool item = result.Done && result.What.StartsWith("item:", StringComparison.Ordinal);
                        PressedItem = item ? result.What["item:".Length..] : "";
                        _pressOnItem = item ? 1 : 0;
                    }))
                _pressOnItem = 0;
            return;
        }
        Delivery delivery = _messages.Press(point, button, map, menuBar: hit == 5);
        if (delivery == Delivery.Posted) _heldDelivered |= button;
        if (delivery == Delivery.Deferred) { _pendingClick |= button; return; } // Its outcome comes with the release.
        Record(Outcome(delivery), point, hit, _messages.PressTarget, delivery == Delivery.Posted, _messages.Why);
    }

    private void ReleaseArrived(Point point, MouseButtons button)
    {
        if (button == MouseButtons.Middle && _scrolling &&
            (Math.Abs(point.X - _scrollAnchor.X) > SystemInformation.DragSize.Width ||
             Math.Abs(point.Y - _scrollAnchor.Y) > SystemInformation.DragSize.Height)) _scrolling = false;
        if (button == MouseButtons.Left) _dragging = false;
        PipMapping map = Geometry();
        if (button == MouseButtons.Left && _leftWaitsForDrag)
        {
            // Let go without dragging: the click, where it was pressed and where it was let go.
            _leftWaitsForDrag = false;
            Delivery pressed = _messages.Press(_pressAt, button, map, menuBar: false);
            if (pressed == Delivery.Posted) _messages.Release(point, button, map, null, out _);
            else if (pressed == Delivery.Deferred)
            {
                nint target = _messages.PressTarget;
                _messages.Release(point, button, map, result =>
                    Record(result.Done ? PressOutcome.Delivered : result.Unsupported ? PressOutcome.Unsupported : PressOutcome.Unconfirmed,
                        point, 1, target, result.Done, result.What), out _);
            }
            if (pressed != Delivery.Deferred) Record(Outcome(pressed), _pressAt, 1, _messages.PressTarget, pressed == Delivery.Posted, _messages.Why);
        }
        else if ((_pendingClick & button) != 0)
        {
            _pendingClick &= ~button;
            nint target = _messages.PressTarget;
            Delivery delivery = _messages.Release(point, button, map, result =>
                Record(result.Done ? PressOutcome.Delivered : result.Unsupported ? PressOutcome.Unsupported : PressOutcome.Unconfirmed,
                    point, 1, target, result.Done, result.What), out string why);
            if (delivery != Delivery.Deferred) Record(Outcome(delivery), point, 1, target, false, why);
        }
        else if ((_heldDelivered & button) != 0)
        {
            _messages.Release(point, button, map, null, out _);
        }
        _heldDelivered &= ~button;
        // Not if the hook has already taken a newer press: it stopped sharing for that one.
        if (!_scrolling && _heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None && map.View.Contains(point) &&
            Volatile.Read(ref _swallowedBits) == 0) Share(true);
    }

    private void ScrollTick()
    {
        if (!MiddleClickScroll || _outside || _inPopup) { _scrolling = false; return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Min(.05, Stopwatch.GetElapsedTime(_scrollTick, now).TotalSeconds);
        _scrollTick = now;
        PipMapping map = Geometry();
        // In a turned view, a pull down the screen scrolls the way the content looks like it should go.
        Size pulled = map.DirectionToSource(new Size(_scrollPoint.X - _scrollAnchor.X, _scrollPoint.Y - _scrollAnchor.Y));
        ScrollAxis(pulled.Width, true, ref _scrollX);
        ScrollAxis(pulled.Height, false, ref _scrollY);
        void ScrollAxis(int displacement, bool horizontal, ref double remainder)
        {
            // Wheel units per second: a quiet centre, linear speed outside it, bounded after a stalled worker.
            double velocity = Math.Sign(displacement) * Math.Clamp(Math.Abs(displacement) - 12, 0, 120) * 12;
            remainder += velocity * elapsed;
            int delta = (int)remainder;
            if (delta == 0) return;
            remainder -= delta;
            if (!horizontal) delta = -delta; // A downward wheel displacement scrolls down.
            _messages.Wheel(_scrollAnchor, horizontal, unchecked((uint)(delta << 16)), map);
        }
    }

    private void StopScroll()
    {
        _scrolling = false;
        if (_heldDelivered == MouseButtons.None && _pendingClick == MouseButtons.None &&
            Volatile.Read(ref _swallowedBits) == 0 && !_outside) Share(true);
    }

    private static PressOutcome Outcome(Delivery delivery) => delivery switch
    {
        Delivery.Posted => PressOutcome.Delivered,
        Delivery.Unsupported => PressOutcome.Unsupported,
        _ => PressOutcome.Unconfirmed,
    };

    private void Record(PressOutcome outcome, Point at, nint hit, nint queuedTarget = 0, bool? queued = null, string effect = "unobserved",
        bool answers = true)
    {
        lock (_log)
        {
            _log.Enqueue((outcome, at, hit));
            if (_log.Count > Keep)
                _log.Dequeue();
        }

        // Presses that didn't arrive, so a report shows which apps and controls refuse them. Never moves or positions.
        if (outcome is PressOutcome.Unsupported or PressOutcome.Unconfirmed)
        {
            nint target = queuedTarget != 0 ? queuedTarget : _messages.PressTarget != 0 ? _messages.PressTarget : _source;
            Diagnostics.Log.Event("press", ("outcome", outcome), ("route", _messages.PressRoute), ("menuBar", _messages.PressRoute is null),
                ("program", Diagnostics.LogValue.ProgramOf(_source)), ("class", Diagnostics.LogValue.ClassOf(target)));
        }

        if (DiagnoseInput)
        {
            try
            {
                PipMapping map = Geometry();
                Point mapped = _messages.MappedScreen(at, map);
                nint target = queuedTarget != 0 ? queuedTarget : _messages.TargetAt(mapped), root = RootAt(at);
                User32.GetWindowRect(_source, out Rect sourceBounds);
                User32.GetWindowRect(target, out Rect targetBounds);
                User32.GetWindowRect(_viewWindow, out Rect viewBounds);
                string entry = $"time={Environment.TickCount64} viewBounds={viewBounds.Left},{viewBounds.Top},{viewBounds.Right},{viewBounds.Bottom} " +
                    $"physical={at} image={map.View} shown={map.Shown} turn={map.Turn} source={sourceBounds.Left},{sourceBounds.Top},{sourceBounds.Right},{sourceBounds.Bottom} " +
                    $"mapped={mapped} hit={hit}({HitName(hit)}) viewHwnd=0x{_viewWindow:X} sourceHwnd=0x{_source:X} target=0x{target:X} " +
                    $"class={Windows.WindowInfo.ClassName(target)} targetBounds={targetBounds.Left},{targetBounds.Top},{targetBounds.Right},{targetBounds.Bottom} " +
                    $"visible={User32.IsWindowVisible(target)} pid={ProcessOf(target)} physicalRoot=0x{root:X} physicalClass={Windows.WindowInfo.ClassName(root)} " +
                    $"foreground=0x{User32.GetForegroundWindow():X} sharing={SharesInput} sourceExStyle=0x{User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE):X} " +
                    $"border={AtViewBorder(at)} outcome={outcome} transport={_messages.PressTransport} queued={queued?.ToString() ?? "not-attempted"} effect={effect} " +
                    $"{Timing}";
                lock (_diagnostics)
                {
                    _diagnostics.Enqueue(entry);
                    if (_diagnostics.Count > Keep) _diagnostics.Dequeue();
                }
            }
            catch (Windows.WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
        }
        Pressed?.Invoke(outcome);
        if (answers) Answered?.Invoke(outcome);
    }

    internal static string HitName(nint hit) => (int)hit switch
    {
        -2 => "HTERROR", -1 => "HTTRANSPARENT", 0 => "HTNOWHERE", 1 => "HTCLIENT", 2 => "HTCAPTION",
        3 => "HTSYSMENU", 4 => "HTGROWBOX", 5 => "HTMENU", 6 => "HTHSCROLL", 7 => "HTVSCROLL",
        8 => "HTMINBUTTON", 9 => "HTMAXBUTTON", 10 => "HTLEFT", 11 => "HTRIGHT", 12 => "HTTOP",
        13 => "HTTOPLEFT", 14 => "HTTOPRIGHT", 15 => "HTBOTTOM", 16 => "HTBOTTOMLEFT", 17 => "HTBOTTOMRIGHT",
        18 => "HTBORDER", 19 => "HTOBJECT", 20 => "HTCLOSE", 21 => "HTHELP", _ => "unknown",
    };

    // Cheap enough for the hook: an owner walk of a few steps.
    private bool IsSourcePopup(nint root) =>
        root != 0 && root != _viewWindow && root != _source &&
        (ProcessOf(root) == _sourceProcess || Windows.WindowInfo.IsOwnedBy(root, _source) || _inputMethod?.Holds(root) == true);

    private InputMethodWindows? _inputMethod;

    // The layer Windows lays over the screen while a shell panel is open, to close it on a press elsewhere.
    internal const string LightDismissLayer = "Shell_LightDismissOverlay";

    // long.MinValue before the first press.
    private long _lastPress = long.MinValue;

    internal InputMethodWindows? InputMethod => _inputMethod;

    private static nint RootAt(Point point) =>
        User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);

    private static uint ProcessOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return process;
    }

    private IDisposable? _menuOpened;

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        _menuOpened?.Dispose();
        _inputMethod?.Dispose();
        _messages.Hover.Deactivate();
        Share(false);
        _rechecked.Set();
        _moved.Set();
        if (_hookThread is not null)
        {
            User32.PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread.Join(2000);
        }
        // The hook is gone: what it queued still goes out, releases included, then the worker ends.
        _work.Set();
        bool workerDone = _worker?.Join(2000) ?? true;
        _watcher?.Join(2000);
        if (workerDone)
        {
            PipMapping map = Geometry();
            // A press whose release never came must not leave the button held down in the app.
            MouseButtons cancelled = MouseButtons.None;
            try { cancelled = _messages.ReleaseAll(map); } catch (Windows.WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
            if ((cancelled | _pendingClick) != MouseButtons.None)
            {
                long packed = Interlocked.Read(ref _pointer);
                Record(PressOutcome.Unconfirmed, new Point((int)(packed >> 32), (int)packed), 1, 0, false, "the visit ended during the press", answers: false);
            }
        }
        if (_watcher is not null)
            Interlocked.Decrement(ref _active);
        if (DiagnoseInput && InputDiagnostics.Length > 0)
        {
            // One bounded file, written after the hook has stopped, never from its callback.
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinScreen");
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, "pip-input-last.log"), InputDiagnostics);
            }
            catch (IOException caught) { Diagnostics.Log.Swallowed(caught); }
            catch (UnauthorizedAccessException caught) { Diagnostics.Log.Swallowed(caught); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }

}
