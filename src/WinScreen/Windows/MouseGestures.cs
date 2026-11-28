using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Windows;

internal enum MenuGestureOutcome { Claimed, TurnedAway }

// Only while the feature could act, so a game, a full-screen app, an excluded program or a locked PC never has it
// running behind them.
internal static class ModifierHookPolicy
{
    public static bool ShouldRun(bool moveOn, bool menuOn, FullScreenState state, bool fills,
        bool moveExcluded, bool menuExcluded, bool away) =>
        ShouldRun((moveOn && !moveExcluded) || (menuOn && !menuExcluded), state, fills, false, away);

    public static bool ShouldRun(bool turnedOn, FullScreenState fullScreen, bool foregroundFillsMonitor,
        bool foregroundExcluded, bool away) =>
        turnedOn && fullScreen == FullScreenState.None && !foregroundFillsMonitor && !foregroundExcluded && !away;

    // Coming back from being away (locked, or the sign-in screen, where no hook hears anything) moves the pointer
    // before the hook hears it again, so that isn't a drop. Nor is silence over an administrator window an ordinary
    // copy can't hear.
    public static bool LooksDropped(bool active, bool pointerMoved, TimeSpan sinceCallback, TimeSpan sinceAway, bool pointerOverReachable,
        TimeSpan silence) =>
        active && pointerMoved && sinceCallback > silence && sinceAway > silence && pointerOverReachable;
}

// Raw input can see a click but can't stop it reaching the app, so this needs a low-level mouse hook, which exists only
// while Active. No keyboard hook: the modifier is read only when a button goes down.
internal sealed class MouseGestures : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012, WM_HOTKEY = 0x0312, WM_APP = 0x8000;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int HC_ACTION = 0;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_ESCAPE = 0x1B;
    private const int MaskKey = 0xE8; // Unassigned: tapping it stops a held Alt or Win opening a menu or Start.
    private const int ClaimEscape = WM_APP + 1, ReleaseEscape = WM_APP + 2, DropHook = WM_APP + 3;
    private const int EscapeHotkey = 0x5751;
    private static readonly uint[] EscapeModifiers = [0, 1, 8]; // None, Alt, Win.
    private const uint KeyUp = 2;
    private const int ThreadExit = 2000;
    private const int SettleLimitMilliseconds = 50;
    private const int SM_CXDRAG = 68, SM_CYDRAG = 69, SPI_GETWINARRANGING = 0x0082;
    private const long WS_THICKFRAME = 0x40000, WS_MAXIMIZEBOX = 0x10000;
    private const int SW_SHOWMAXIMIZED = 3;
    private static readonly Size Smallest = new(120, 60);

    // Only the mover thread touches it.
    private sealed class Drag(Gesture gesture)
    {
        public Gesture Gesture { get; } = gesture;
        public Rectangle Original = gesture.Original; // Where the motion is measured from.
        public Point Start = gesture.Start;
        public WindowPlacement Before;
        public bool Maximized, Restored, CanSnap, CanMaximize;
        public Size? SnappedFrom; // The size before a snap, for a snapped window being dragged out.
        public SnapZone Zone;
        public Point Pointer;
        public Rectangle? Sent;
    }

    private sealed record Gesture(nint Window, bool Resizing, Point Start, Rectangle Original, ResizeEdges Edges, int ReleaseMessage);

    private readonly Func<nint, bool> _eligible;
    private readonly Action<nint> _began;
    private readonly User32.HookProc _proc; // Held here so the collector can't take it while Windows has it.
    private readonly AutoResetEvent _moved = new(false);
    private readonly ConcurrentQueue<(Gesture Gesture, bool Cancelled)> _endings = new();
    private readonly Dictionary<nint, (Rectangle Snapped, Size Before)> _snapped = []; // Mover thread only.
    private Thread? _hookThread, _mover;
    private uint _hookThreadId;
    private volatile Gesture? _gesture;
    private volatile bool _swallowRelease;
    private bool _dragClaimed;
    private int _pendingRelease;
    private long _point;
    private volatile bool _stopping;
    private volatile bool _moveStopping;
    private volatile bool _accepting;
    private volatile bool _moveEnabled = true, _menuEnabled, _menuVisible;
    private volatile WindowMenuGesture _menuGesture;

    public bool MoveEnabled { get => _moveEnabled; set => _moveEnabled = value; }
    public bool MenuEnabled { get => _menuEnabled; set => _menuEnabled = value; }
    public WindowMenuGesture MenuGesture { get => _menuGesture; set => _menuGesture = value; }
    public Func<nint, bool>? MenuEligible { get; set; }
    public Action<nint, Point>? MenuRequested { get; set; }
    // The gesture was made over a window that can't have the menu. Told off the hook, for the log.
    public Action<nint>? MenuTurnedAway { get; set; }
    private long _menuDecisionTicks;
    // How long the hook took to decide on the last menu press: Windows drops a hook that answers slowly.
    public TimeSpan LastMenuDecision => TimeSpan.FromTicks(Interlocked.Read(ref _menuDecisionTicks));
    public Action<Point>? OutsidePressed { get; set; }
    public bool OpacityWheelEnabled { get; set; }
    public Func<nint, bool>? OpacityEligible { get; set; }
    public Action<nint, int>? OpacityRequested { get; set; }
    private nint _wheelWindow;
    private int _wheelRemainder;
    public bool MenuVisible { get => _menuVisible; set => _menuVisible = value; }

    // Windows' own Snap switch has to be on too.
    public bool SnapEnabled { get; set; } = true;

    // Told off the UI thread.
    public Action<Rectangle?>? SnapPreview { get; set; }
    private long _lastCallback;
    private long _longestCallbackTicks;

    // eligible is called inside the hook, so it must be quick. began is told once per gesture, off the hook.
    public MouseGestures(Func<nint, bool> eligible, Action<nint> began)
    {
        _eligible = eligible;
        _began = began;
        _proc = Callback;
    }

    // Read each time a button goes down, so a change applies straight away.
    public ModifierKey Key { get; set; }

    public bool Active { get; private set; }

    private const int SilenceCheckMilliseconds = 150;

    // Told from the mover's thread; the owner calls Reinstall on its own thread.
    public Action? HookSilent { get; set; }

    public long LastCallback => Interlocked.Read(ref _lastCallback);

    // Must stay far under Windows' limit.
    public TimeSpan LongestCallback => TimeSpan.FromTicks(Interlocked.Read(ref _longestCallbackTicks));

    public void SetActive(bool on)
    {
        _accepting = on;
        if (!on && _swallowRelease) return; // Finish a claimed pair before removing the hook.
        if (on == Active)
        {
            if (on) FollowMover();
            return;
        }
        if (on) Start();
        else Stop();
    }

    // Windows removes a hook it finds slow without saying so. A drag or menu press in hand will never see its release,
    // so it ends here, as if let go; otherwise the claimed press would keep the dead hook for good.
    public void Reinstall()
    {
        if (!Active) return;
        Diagnostics.Log.Event("hook.gestures.lost", ("dragEnded", _gesture is not null));
        Diagnostics.Suspicion.HookLostRepeatedly.Note(("front", Diagnostics.LogValue.ProgramOf(User32.GetForegroundWindow())));
        if (_gesture is { } gesture)
        {
            _gesture = null;
            _endings.Enqueue((gesture, false));
            _moved.Set();
        }
        _swallowRelease = false;
        _dragClaimed = false;
        Stop();
        if (_accepting) Start();
    }

    internal void LoseHookForTest()
    {
        if (Active) User32.PostThreadMessage(_hookThreadId, DropHook, 0, 0);
    }

    public void Dispose()
    {
        Stop();
        _moved.Dispose();
    }

    private void Start()
    {
        _stopping = false;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = Kernel32.GetCurrentThreadId();
            nint hook = User32.SetWindowsHookEx(WH_MOUSE_LL, _proc, Kernel32.GetModuleHandle(0), 0);
            if (hook == 0)
                failure = new Win32Exception(Marshal.GetLastWin32Error());
            User32.PeekMessage(out _, 0, 0, 0, 0); // Make the thread-message queue before publishing readiness.
            ready.Set();
            if (hook == 0)
                return;
            try
            {
                while (User32.GetMessage(out Msg message, 0, 0, 0) > 0)
                {
                    // Esc on its own, or with the held key: people keep holding it while they drag.
                    if (message.Message == ClaimEscape)
                        for (int i = 0; i < EscapeModifiers.Length; i++)
                            User32.RegisterHotKey(0, EscapeHotkey + i, User32.MOD_NOREPEAT | EscapeModifiers[i], VK_ESCAPE);
                    else if (message.Message == ReleaseEscape)
                        ReleaseEscapeKeys();
                    else if (message.Message == DropHook && User32.UnhookWindowsHookEx(hook))
                        hook = 0; // For tests: what Windows does to a hook it finds too slow, without a word.
                    else if (message.Message == WM_HOTKEY && message.WParam - EscapeHotkey is >= 0 and < 3)
                        Cancel();
                }
            }
            finally
            {
                ReleaseEscapeKeys();
                if (hook != 0) User32.UnhookWindowsHookEx(hook);
            }
        }) { IsBackground = true, Name = "WinScreen modifier hook", Priority = ThreadPriority.AboveNormal };
        _hookThread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _hookThread.Join(ThreadExit);
            _hookThread = null;
            Diagnostics.Log.Event("hook.gestures.failed", ("code", Diagnostics.LogValue.Code(((Win32Exception)failure).NativeErrorCode)));
            throw failure;
        }

        Diagnostics.Log.Event("hook.gestures.installed");
        FollowMover();
        Interlocked.Exchange(ref _lastCallback, Stopwatch.GetTimestamp());
        Active = true;
    }

    private void Stop()
    {
        if (!Active)
            return;
        Active = false;
        _stopping = true;
        _gesture = null;
        _swallowRelease = false;
        if (_mover is not null) _moved.Set();
        User32.PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(ThreadExit);
        _mover?.Join(ThreadExit);
        _hookThread = _mover = null;
        Diagnostics.Log.Event("hook.gestures.removed");
    }

    private static void ReleaseEscapeKeys()
    {
        for (int i = 0; i < EscapeModifiers.Length; i++) User32.UnregisterHotKey(0, EscapeHotkey + i);
    }

    private bool ModifierHeld() => Key == ModifierKey.Win
        ? User32.GetAsyncKeyState(VK_LWIN) < 0 || User32.GetAsyncKeyState(VK_RWIN) < 0
        : User32.GetAsyncKeyState(VK_MENU) < 0;

    private void FollowMover()
    {
        if (MoveEnabled && _mover is null)
        {
            _moveStopping = false;
            _mover = new Thread(MoveWindows) { IsBackground = true, Name = "WinScreen modifier mover" };
            _mover.Start();
        }
        else if (!MoveEnabled && _mover is not null)
        {
            _gesture = null;
            _moveStopping = true;
            _moved.Set();
            _mover.Join(ThreadExit);
            _mover = null;
        }
    }

    internal static bool MatchesMenu(WindowMenuGesture gesture, bool control, bool shift, bool alt, bool win, bool middle) =>
        control && !alt && !win && shift == (gesture == WindowMenuGesture.CtrlShiftRightClick) &&
        middle == (gesture == WindowMenuGesture.CtrlMiddleClick);

    private bool BeginMenu(Point point, bool middle)
    {
        if (!_accepting || !MenuEnabled || MenuVisible || !MatchesMenu(MenuGesture, User32.GetAsyncKeyState(0x11) < 0,
                User32.GetAsyncKeyState(0x10) < 0, User32.GetAsyncKeyState(VK_MENU) < 0,
                User32.GetAsyncKeyState(VK_LWIN) < 0 || User32.GetAsyncKeyState(VK_RWIN) < 0, middle)) return false;
        long started = Stopwatch.GetTimestamp();
        nint window = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);
        bool eligible = window != 0 && MenuEligible?.Invoke(window) == true;
        Interlocked.Exchange(ref _menuDecisionTicks, Stopwatch.GetElapsedTime(started).Ticks);
        if (!eligible)
        {
            MenuTurnedAway?.Invoke(window);
            return false;
        }
        _pendingRelease = middle ? WM_MBUTTONUP : WM_RBUTTONUP;
        _dragClaimed = false;
        _swallowRelease = true;
        MenuRequested?.Invoke(window, point); // Posts only; it must never wait for the UI.
        return true;
    }

    private nint Callback(int code, nint message, nint data)
    {
        long started = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastCallback, started);
        if (code != HC_ACTION)
            return User32.CallNextHookEx(0, code, message, data);

        MouseHookInfo info;
        unsafe { info = *(MouseHookInfo*)data; } // Read in place: PtrToStructure boxes, on every move in Windows.
        bool swallow = false;
        if (MenuVisible && (int)message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            OutsidePressed?.Invoke(new Point(info.X, info.Y));
        switch ((int)message)
        {
            case WM_MOUSEWHEEL:
                swallow = ChangeOpacity(new Point(info.X, info.Y), (short)(info.MouseData >> 16));
                break;
            case WM_MOUSEMOVE:
                if (_gesture is not null)
                {
                    Interlocked.Exchange(ref _point, Pack(info.X, info.Y));
                    _moved.Set();
                }

                break;
            case WM_LBUTTONDOWN:
                swallow = _gesture is null && !_swallowRelease && Begin(new Point(info.X, info.Y), false);
                break;
            case WM_RBUTTONDOWN:
                swallow = _gesture is null && !_swallowRelease &&
                          (BeginMenu(new Point(info.X, info.Y), false) || Begin(new Point(info.X, info.Y), true));
                break;
            case WM_MBUTTONDOWN:
                swallow = _gesture is null && !_swallowRelease && BeginMenu(new Point(info.X, info.Y), true);
                break;
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
            case WM_MBUTTONUP:
                swallow = End((int)message);
                break;
        }

        long spent = Stopwatch.GetElapsedTime(started).Ticks;
        if (spent > Interlocked.Read(ref _longestCallbackTicks))
            Interlocked.Exchange(ref _longestCallbackTicks, spent);
        return swallow ? 1 : User32.CallNextHookEx(0, code, message, data);
    }

    private bool Begin(Point point, bool resizing)
    {
        if (!_accepting || !MoveEnabled || !ModifierHeld())
            return false;
        nint window = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);
        if (window == 0 || !_eligible(window) || !User32.GetWindowRect(window, out Rect r))
            return false;

        var original = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        Interlocked.Exchange(ref _point, Pack(point.X, point.Y));
        _pendingRelease = resizing ? WM_RBUTTONUP : WM_LBUTTONUP;
        _dragClaimed = true;
        _gesture = new Gesture(window, resizing, point, original,
            resizing ? PlacementGeometry.EdgesAt(point, original) : ResizeEdges.None, _pendingRelease);
        _swallowRelease = true;
        User32.PostThreadMessage(_hookThreadId, ClaimEscape, 0, 0);
        _moved.Set();
        return true;
    }

    private bool ChangeOpacity(Point point, int delta)
    {
        if (!_accepting || !OpacityWheelEnabled || _gesture is not null || _swallowRelease ||
            User32.GetAsyncKeyState(0x11) >= 0 || User32.GetAsyncKeyState(VK_MENU) >= 0 || User32.GetAsyncKeyState(0x10) < 0 ||
            User32.GetAsyncKeyState(VK_LWIN) < 0 || User32.GetAsyncKeyState(VK_RWIN) < 0)
        {
            _wheelWindow = 0;
            _wheelRemainder = 0;
            return false;
        }
        nint window = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);
        if (window == 0 || OpacityEligible?.Invoke(window) != true) return false;
        if (_wheelWindow != window) { _wheelWindow = window; _wheelRemainder = 0; }
        int steps = TakeWheelSteps(ref _wheelRemainder, delta);
        if (steps != 0) OpacityRequested?.Invoke(window, steps);
        return true;
    }

    internal static int TakeWheelSteps(ref int remainder, int delta)
    {
        remainder += delta;
        int steps = remainder / 120;
        remainder %= 120;
        return steps;
    }

    // The press went to WinScreen, so its release must too, or the app sees a release it never pressed.
    private bool End(int message)
    {
        if (!_swallowRelease || message != _pendingRelease)
            return false;
        _swallowRelease = false;
        bool dragged = _dragClaimed;
        _dragClaimed = false;
        if (_gesture is { } gesture)
        {
            _gesture = null;
            _endings.Enqueue((gesture, false));
            _moved.Set();
        }
        User32.PostThreadMessage(_hookThreadId, ReleaseEscape, 0, 0);
        if (dragged) MaskModifier();
        return true;
    }

    // The button is still down, so its release is still WinScreen's to swallow. The mover puts the window back: this
    // thread must never wait on another app.
    private void Cancel()
    {
        Gesture? gesture = _gesture;
        if (gesture is null)
            return;
        _gesture = null;
        _endings.Enqueue((gesture, true));
        _moved.Set();
    }

    private void MaskModifier()
    {
        if (!ModifierHeld())
            return;
        User32.keybd_event(MaskKey, 0, 0, 0);
        User32.keybd_event(MaskKey, 0, KeyUp, 0);
    }

    // Each move is asynchronous, so a hung app can't stall the mouse, and the next waits until the app caught up: every
    // position sent would pile up in a slow app's queue and play back long after the pointer stopped.
    private void MoveWindows()
    {
        Drag? drag = null;
        bool silenceReported = false;
        while (!_stopping && !_moveStopping)
        {
            // While a drag is in hand, wake now and then to notice a hook Windows has removed: the real pointer moved
            // but the hook heard nothing.
            bool woken = _moved.WaitOne(_gesture is null ? Timeout.Infinite : SilenceCheckMilliseconds);
            if (_moveStopping) break;
            if (!woken && _gesture is not null && !silenceReported && Cursor.Position != Pointer() &&
                Stopwatch.GetElapsedTime(LastCallback).TotalMilliseconds > SilenceCheckMilliseconds * 2)
            {
                silenceReported = true;
                HookSilent?.Invoke();
            }
            if (_gesture is null) silenceReported = false;
            drag = FinishEndings(drag);

            if (_stopping || _gesture is not { } gesture)
            {
                if (drag is not null) ShowZone(drag, SnapZone.None);
                drag = null;
                continue;
            }

            if (drag is null || !ReferenceEquals(drag.Gesture, gesture))
                drag = Started(gesture);
            Follow(drag, Pointer());
        }

        // Reinstall queues the drag in hand and stops at once: its ending must still be carried out.
        if (!_moveStopping) drag = FinishEndings(drag);
        if (drag is not null) ShowZone(drag, SnapZone.None);
    }

    private Drag? FinishEndings(Drag? drag)
    {
        while (_endings.TryDequeue(out var ending))
        {
            if (drag is null || !ReferenceEquals(drag.Gesture, ending.Gesture))
                drag = Started(ending.Gesture); // Released before the mover got to it: it still goes where it was dropped.
            if (!ending.Cancelled) Follow(drag, Pointer());
            Finish(drag, ending.Cancelled);
            drag = null;
        }
        return drag;
    }

    private Point Pointer()
    {
        long packed = Interlocked.Read(ref _point);
        return new Point((int)(packed >> 32), (int)packed);
    }

    private Drag Started(Gesture gesture)
    {
        _began(gesture.Window);
        var drag = new Drag(gesture) { Before = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() } };
        User32.GetWindowPlacement(gesture.Window, ref drag.Before);
        drag.Maximized = User32.IsZoomed(gesture.Window);
        long style = (long)User32.GetWindowLongPtr(gesture.Window, User32.GWL_STYLE);
        drag.CanSnap = (style & WS_THICKFRAME) != 0;
        drag.CanMaximize = (style & WS_MAXIMIZEBOX) != 0;
        foreach (nint gone in _snapped.Keys.Where(window => !User32.IsWindow(window)).ToArray())
            _snapped.Remove(gone);
        if (!drag.Maximized && !gesture.Resizing && _snapped.TryGetValue(gesture.Window, out var snapped) &&
            snapped.Snapped == gesture.Original)
            drag.SnappedFrom = snapped.Before;
        return drag;
    }

    private void Follow(Drag drag, Point pointer)
    {
        Gesture gesture = drag.Gesture;
        drag.Pointer = pointer;
        if ((drag.Maximized || drag.SnappedFrom is not null) && !drag.Restored)
        {
            // A click with the key held, or a little wobble, shouldn't take a window out of maximized.
            if (Math.Abs(pointer.X - gesture.Start.X) < User32.GetSystemMetrics(SM_CXDRAG) &&
                Math.Abs(pointer.Y - gesture.Start.Y) < User32.GetSystemMetrics(SM_CYDRAG))
                return;
            if (!Restore(drag, pointer)) return;
        }

        var moved = new Size(pointer.X - drag.Start.X, pointer.Y - drag.Start.Y);
        Rectangle r = gesture.Resizing
            ? PlacementGeometry.Resized(drag.Original, gesture.Edges, moved, Smallest)
            : new Rectangle(drag.Original.Location + moved, drag.Original.Size);
        if (!gesture.Resizing) ShowZone(drag, ZoneAt(drag, pointer));
        Send(gesture.Window, r, gesture.Resizing ? 0 : User32.SWP_NOSIZE, drag);
    }

    private bool Restore(Drag drag, Point pointer)
    {
        nint window = drag.Gesture.Window;
        if (User32.IsHungAppWindow(window)) return false;
        drag.Restored = true;
        if (drag.SnappedFrom is { } size)
        {
            _snapped.Remove(window);
            Rectangle under = PlacementGeometry.RestoredUnder(pointer, drag.Gesture.Start, drag.Original, size);
            Send(window, under, 0, drag);
            drag.Original = under;
            drag.Start = pointer;
            return true;
        }

        Screen screen = Screen.FromHandle(window);
        Rectangle area = screen.WorkingArea, all = screen.Bounds;
        Rect normal = drag.Before.NormalPosition;
        var restored = new Size(normal.Right - normal.Left, normal.Bottom - normal.Top);
        Rectangle target = drag.Gesture.Resizing
            ? area // Resizing starts from what's on screen, so the window doesn't jump.
            : PlacementGeometry.RestoredUnder(pointer, drag.Gesture.Start, area, restored);
        WindowPlacement placement = drag.Before;
        placement.ShowCommand = User32.SW_SHOWNOACTIVATE;
        placement.NormalPosition = new Rect(target.Left - (area.Left - all.Left), target.Top - (area.Top - all.Top),
            target.Right - (area.Left - all.Left), target.Bottom - (area.Top - all.Top));
        User32.SetWindowPlacement(window, ref placement);
        if (drag.Gesture.Resizing && WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out Rectangle outer))
        {
            Rectangle wanted = PlacementGeometry.OuterFor(area, frame, outer);
            User32.SetWindowPos(window, 0, wanted.X, wanted.Y, wanted.Width, wanted.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        }

        if (!User32.GetWindowRect(window, out Rect now)) return false;
        drag.Original = Rectangle.FromLTRB(now.Left, now.Top, now.Right, now.Bottom);
        drag.Sent = drag.Original;
        if (!drag.Gesture.Resizing) drag.Start = pointer; // A resize keeps measuring from the press.
        return true;
    }

    // Without waiting on the app.
    private static void Send(nint window, Rectangle r, uint extra, Drag drag)
    {
        if (drag.Sent == r) return;
        drag.Sent = r;
        User32.SetWindowPos(window, 0, r.X, r.Y, r.Width, r.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS | extra);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < SettleLimitMilliseconds)
        {
            if (!User32.GetWindowRect(window, out Rect now)) return;
            if (now.Left == r.Left && now.Top == r.Top &&
                ((extra & User32.SWP_NOSIZE) != 0 || (now.Right == r.Right && now.Bottom == r.Bottom)))
                return;
            Thread.Sleep(1);
        }
    }

    private SnapZone ZoneAt(Drag drag, Point pointer)
    {
        if (!SnapEnabled || !drag.CanSnap || !WindowsSnaps()) return SnapZone.None;
        Rectangle display = Screen.FromPoint(pointer).Bounds;
        Screen[] screens = Screen.AllScreens;
        bool Open(int x, int y) => !screens.Any(screen => screen.Bounds.Contains(x, y));
        SnapZone zone = PlacementGeometry.SnapZoneAt(pointer, display, Open(display.Left - 1, pointer.Y),
            Open(pointer.X, display.Top - 1), Open(display.Right, pointer.Y));
        return zone == SnapZone.Maximize && !drag.CanMaximize ? SnapZone.None : zone;
    }

    private void ShowZone(Drag drag, SnapZone zone)
    {
        if (drag.Zone == zone) return;
        drag.Zone = zone;
        SnapPreview?.Invoke(zone == SnapZone.None
            ? null
            : PlacementGeometry.SnapFrame(zone, Screen.FromPoint(drag.Pointer).WorkingArea));
    }

    private void Finish(Drag drag, bool cancelled)
    {
        SnapZone zone = drag.Zone;
        ShowZone(drag, SnapZone.None);
        nint window = drag.Gesture.Window;
        if (cancelled)
        {
            if (drag.Maximized)
            {
                if (drag.Restored && !User32.IsHungAppWindow(window)) User32.SetWindowPlacement(window, ref drag.Before);
                return;
            }
            Rectangle r = drag.Gesture.Original;
            User32.SetWindowPos(window, 0, r.X, r.Y, r.Width, r.Height,
                User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS);
            if (drag.SnappedFrom is { } before) _snapped[window] = (r, before);
            return;
        }

        if (zone != SnapZone.None && !User32.IsHungAppWindow(window)) Snap(drag, zone);
    }

    private void Snap(Drag drag, SnapZone zone)
    {
        nint window = drag.Gesture.Window;
        if (!User32.GetWindowRect(window, out Rect now)) return;
        var dropped = Rectangle.FromLTRB(now.Left, now.Top, now.Right, now.Bottom);
        Screen screen = Screen.FromPoint(drag.Pointer);
        Rectangle area = screen.WorkingArea, all = screen.Bounds;
        if (zone == SnapZone.Maximize)
        {
            // It comes back out of maximized at the size it had, where it was dropped.
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!User32.GetWindowPlacement(window, ref placement)) return;
            placement.ShowCommand = SW_SHOWMAXIMIZED;
            placement.NormalPosition = new Rect(dropped.Left - (area.Left - all.Left), dropped.Top - (area.Top - all.Top),
                dropped.Right - (area.Left - all.Left), dropped.Bottom - (area.Top - all.Top));
            User32.SetWindowPlacement(window, ref placement);
            return;
        }

        Rectangle target = PlacementGeometry.SnapFrame(zone, area);
        for (int attempt = 0; attempt < 2; attempt++) // A second go if a change of scaling changed the invisible margins.
        {
            if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out Rectangle outer) || frame == target) break;
            Send(window, PlacementGeometry.OuterFor(target, frame, outer), 0, drag);
        }
        if (User32.GetWindowRect(window, out now))
            _snapped[window] = (Rectangle.FromLTRB(now.Left, now.Top, now.Right, now.Bottom), dropped.Size);
    }

    private static bool WindowsSnaps() => !User32.SystemParametersInfo(SPI_GETWINARRANGING, 0, out int on, 0) || on != 0;

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }

}
