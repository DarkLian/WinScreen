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
    private readonly SourcePopups _popups;
    private readonly nint _viewWindow;
    private readonly bool _replay;
    private readonly object _reach = new(), _applying = new();
    private readonly List<string> _misses = [];

    /// <summary>Replayed presses that found something other than the original under the pointer, for tests.</summary>
    internal IReadOnlyList<string> ReplayMisses { get { lock (_misses) return _misses.ToArray(); } }
    private bool _reachable;
    private int _reachGeneration;
    private int _replayedButtons; // MouseButtons whose press was replayed and whose release must be too.
    private readonly System.Collections.Concurrent.BlockingCollection<(uint Flags, uint Data, Point? At)> _replays = new();
    private volatile bool _holdPlace; // A press is on its way: the original stays where it was aligned for it.
    private Thread? _replayer;
    private const nuint ReplayMark = 0x57534E52; // Marks WinScreen's own replays so the hook lets them through.
    private const int SettleMilliseconds = 40;

    /// <param name="viewWindow">The view itself: under the pointer when the original can't take input.</param>
    /// <param name="replay">
    /// True: the original stays fully invisible, and each press is replayed to it after it is briefly made able
    /// to take it. False: it takes input directly wherever the pointer is on content, hover included, and
    /// shows faintly while it does.
    /// </param>
    public PipInteraction(nint source, Rectangle view, Rectangle shown, nint viewWindow = 0, bool replay = false,
        SourcePopups? popups = null)
    {
        _source = source;
        _identity = Windows.WindowIdentity.Capture(source);
        _view = view;
        _shown = shown;
        _hook = Callback;
        _viewWindow = viewWindow;
        _replay = replay;
        _popups = popups ?? new SourcePopups(source);
        _reachable = !replay;
        User32.GetWindowThreadProcessId(source, out _sourceProcess);
    }

    /// <summary>Raised on the hook thread for a press on the original's caption buttons or icon.</summary>
    public event Action<CaptionAction>? CaptionPressed;

    /// <summary>Raised on the mover thread when the original asks something: a dialog, or it was disabled.</summary>
    public event Action? QuestionShown;

    /// <summary>Whether the original can take input at the pointer right now, for tests.</summary>
    internal bool Reachable { get { lock (_reach) return _reachable; } }

    /// <summary>
    /// Makes the original able to take input, or not, at once: faint (alpha 1) and the view passing input
    /// through, or invisible (alpha 0) and the view solid, so the pointer and hover stay on the view.
    /// </summary>
    private void SetReachable(bool on)
    {
        lock (_reach)
        {
            if (on) _reachGeneration++;
            if (_reachable == on) return;
            _reachable = on;
        }
        Apply();
    }

    /// <summary>
    /// Makes the original match the latest wish. It sits just above the view, so its opacity alone decides:
    /// at 0 the pointer passes through it to the view, at 1 the original takes it. Nothing waits on another
    /// thread, which matters here inside the mouse hook.
    /// </summary>
    private void Apply()
    {
        lock (_applying)
        {
            bool on;
            lock (_reach) on = _reachable;
            if (_stopping && on) return;
            User32.SetLayeredWindowAttributes(_source, 0, on ? (byte)1 : (byte)0, User32.LWA_ALPHA);
        }
    }

    /// <summary>Keeps the original just above the view, under the view's own title bar and buttons.</summary>
    private void StayJustAboveView()
    {
        if (_viewWindow == 0) return;
        nint window = User32.GetWindow(_source, GW_HWNDNEXT);
        for (int step = 0; window != 0 && step < 16; step++, window = User32.GetWindow(window, GW_HWNDNEXT))
            if (window == _viewWindow) return;
        nint above = User32.GetWindow(_viewWindow, GW_HWNDPREV);
        User32.SetWindowPos(_source, above == 0 || above == _source ? User32.HWND_TOPMOST : above, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;

    /// <summary>
    /// Sends replayed input at once, so it lands where the pointer pressed, then hides the original again once
    /// nothing needs it: as soon as it holds the mouse after a press (capture routes a drag without
    /// hit-testing), or a moment after the last release or wheel turn.
    /// </summary>
    private void Replay()
    {
        long settleAt = 0, captureUntil = 0;
        int since = -1;
        uint thread = User32.GetWindowThreadProcessId(_source, out _);
        while (!_replays.IsCompleted)
        {
            long now = Environment.TickCount64;
            int wait = captureUntil != 0 ? 2 : settleAt != 0 ? (int)Math.Max(0, settleAt - now) : 250;
            if (_replays.TryTake(out var input, wait))
            {
                // Here, not in the hook: putting it back above the view waits on the app, and a hook that
                // waits too long has its press passed on by Windows as well as replayed.
                StayJustAboveView();
                if (input.At is { } at)
                {
                    nint under = RootAt(at);
                    if (under != _source)
                        lock (_misses) _misses.Add($"{at.X},{at.Y} onto 0x{under:X} ({Windows.WindowInfo.ClassName(under)})");
                }
                SendMouse(input.Flags, input.Data, input.At);
                if (input.At is not null) _holdPlace = false;
                lock (_reach) since = _reachGeneration;
                bool press = (input.Flags & (0x2 | 0x8 | 0x20 | 0x80)) != 0;
                captureUntil = press ? Environment.TickCount64 + 30 : 0;
                settleAt = press ? 0 : Environment.TickCount64 + SettleMilliseconds;
                continue;
            }
            now = Environment.TickCount64;
            if (captureUntil != 0)
            {
                var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                bool held = GetGUIThreadInfo(thread, ref info) && info.Capture != 0;
                if (held || now >= captureUntil)
                {
                    captureUntil = 0;
                    if (held) HideIfUnchanged(since, evenWhileHeld: true); // The drag goes on by capture alone.
                    else settleAt = now + SettleMilliseconds;
                }
                continue;
            }
            if (settleAt != 0 && now >= settleAt)
            {
                settleAt = 0;
                HideIfUnchanged(since, evenWhileHeld: false);
            }
        }
    }

    private void HideIfUnchanged(int since, bool evenWhileHeld)
    {
        lock (_reach)
        {
            if (since != _reachGeneration || !_reachable) return;
            if (!evenWhileHeld && Volatile.Read(ref _replayedButtons) != 0) return;
            _reachable = false;
        }
        Apply();
    }

    /// <summary>
    /// Sends input marked as WinScreen's. A press goes exactly where it was made: if the pointer has moved on
    /// since, it is put back there with it, so the press can't land somewhere it wasn't meant for.
    /// </summary>
    private static void SendMouse(uint flags, uint data, Point? at = null)
    {
        int x = 0, y = 0;
        if (at is { } point)
        {
            const uint MoveAbsolute = 0x1 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
            Rectangle all = SystemInformation.VirtualScreen;
            x = (int)Math.Round((point.X - all.Left) * 65535.0 / Math.Max(1, all.Width - 1));
            y = (int)Math.Round((point.Y - all.Top) * 65535.0 / Math.Max(1, all.Height - 1));
            flags |= MoveAbsolute;
        }
        var input = new Input { Type = 0, Mouse = new MouseData { X = x, Y = y, Data = data, Flags = flags, Extra = ReplayMark } };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private void QueueReplay(uint flags, uint data = 0, Point? at = null)
    {
        if (at is not null) _holdPlace = true;
        try { _replays.Add((flags, data, at)); }
        catch (InvalidOperationException) { _holdPlace = false; } // Stopping: the release is sent by Dispose.
    }

    private static (uint Down, uint Up, uint Data) FlagsFor(MouseButtons button) => button switch
    {
        MouseButtons.Left => (0x2, 0x4, 0),
        MouseButtons.Right => (0x8, 0x10, 0),
        MouseButtons.Middle => (0x20, 0x40, 0),
        MouseButtons.XButton1 => (0x80, 0x100, 1),
        _ => (0x80, 0x100, 2),
    };

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
        if (_replay)
        {
            _replayer = new Thread(Replay) { IsBackground = true, Name = "WinScreen interaction replay", Priority = ThreadPriority.AboveNormal };
            _replayer.Start();
        }
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
                Rectangle view;
                lock (_moved) view = _view;
                if (_popups.Sweep(view))
                {
                    QuestionShown?.Invoke();
                    return;
                }
                if (_inPopup || _holdPlace) continue;
                StayJustAboveView();
                clock.Restart();
                long packed = Interlocked.Read(ref _pointer);
                var pointer = new Point((int)(packed >> 32), (int)packed);
                Point place = PlaceFor(pointer);
                User32.SetWindowPos(_source, 0, place.X, place.Y, 0, 0, Move | User32.SWP_ASYNCWINDOWPOS);
                if (!_replay && _heldDelivered == MouseButtons.None)
                    FollowContent(pointer);
            }
        }
        finally
        {
            _popups.ShowAll();
        }
    }

    /// <summary>
    /// With hover effects, the original takes input only where the pointer is on its content. Over its title bar,
    /// frame or caption buttons it stays invisible, and the view has the pointer: no hover there, so Windows'
    /// snap layouts never open over a window nobody can see.
    /// </summary>
    private void FollowContent(Point pointer)
    {
        nint position = (nint)((pointer.Y << 16) | (pointer.X & 0xFFFF));
        if (User32.SendMessageTimeout(_source, User32.WM_NCHITTEST, 0, position, AbortIfHung, 15, out nint hit) != 0)
            SetReachable(!AtViewBorder(pointer) && PressRules.ReachesContent(hit));
    }

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
        if (info.Extra == ReplayMark)
            return CallNextHookEx(0, code, message, data); // WinScreen's own replay of a press it already decided on.
        var point = new Point(info.X, info.Y);
        Interlocked.Exchange(ref _observedPointer, Pack(point));
        bool swallow = (int)message switch
        {
            WM_MOUSEMOVE => OnMove(point),
            WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN => OnPress(point, ButtonOf((int)message)),
            WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP => OnRelease(point, ButtonOf((int)message - 1)),
            WM_XBUTTONDOWN => OnPress(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
            WM_XBUTTONUP => OnRelease(point, (info.MouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2),
            WM_MOUSEWHEEL or WM_MOUSEHWHEEL => OnWheel(point, (int)message == WM_MOUSEHWHEEL, info.MouseData),
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

    private bool AtViewBorder(Point point)
    {
        if (_viewWindow == 0 || !User32.GetWindowRect(_viewWindow, out Rect bounds)) return false;
        return PipGeometry.SizingHit(point, Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            Dpi.Scale(6, Dpi.Of(_viewWindow))) != 0;
    }

    private bool OnPress(Point point, MouseButtons button)
    {
        if (AtViewBorder(point))
        {
            SetReachable(false);
            Record(PressOutcome.ViewControl, point, 0);
            return false;
        }
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
        bool underView = root == _viewWindow && _viewWindow != 0; // The original is there, unable to take it yet.
        if (!underView && ProcessOf(root) == (uint)Environment.ProcessId)
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

        if (!_identity.StillThere() || (root != _source && !underView) || !Align(point))
        {
            Record(PressOutcome.Unconfirmed, point, 0);
            _heldSwallowed |= button;
            return true;
        }

        nint position = (nint)((point.Y << 16) | (point.X & 0xFFFF));
        nint hit = -2;
        bool answered = User32.SendMessageTimeout(_source, User32.WM_NCHITTEST, 0, position, AbortIfHung, HitTestMilliseconds, out hit) != 0;
        nint now = answered ? RootAt(point) : 0;
        if (!answered || !_identity.StillThere() || (now != _source && now != _viewWindow))
        {
            Record(PressOutcome.Unconfirmed, point, 0);
            _heldSwallowed |= button;
            return true;
        }

        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        if (!PressRules.ReachesContent(hit) && !alt && PressRules.CaptionActionFor(hit, button) is { } action)
        {
            Record(PressOutcome.CaptionButton, point, hit);
            _heldSwallowed |= button;
            CaptionPressed?.Invoke(action);
            return true;
        }

        if (!PressRules.ReachesContent(hit) && !alt && PressRules.IsBorder(hit))
        {
            Record(PressOutcome.MovedView, point, hit); // Held, and nothing happens: the view has its own border.
            _heldSwallowed |= button;
            return true;
        }

        if (!PressRules.ReachesContent(hit) || alt)
        {
            Record(PressOutcome.MovedView, point, hit);
            _heldSwallowed |= button;
            _dragging = button == MouseButtons.Left;
            _dragFrom = point;
            return true;
        }

        Record(PressOutcome.Delivered, point, hit);
        _heldDelivered |= button;
        if (_replay)
        {
            // Swallowed and sent again, once the original can take it: the same press, a moment later. Always,
            // even when it already can, since it may be about to stop.
            SetReachable(true);
            Interlocked.Or(ref _replayedButtons, (int)button);
            var (down, _, data) = FlagsFor(button);
            QueueReplay(down, data, point);
            return true;
        }
        return false;
    }

    private bool OnWheel(Point point, bool horizontal, uint mouseData)
    {
        if (_outside || !_identity.StillThere())
            return true;
        nint root = RootAt(point);
        bool underView = root == _viewWindow && _viewWindow != 0;
        if (!underView)
            return ProcessOf(root) != _sourceProcess;
        if (!_replay)
            return true; // Over the original's frame: the wheel isn't for anything there.
        SetReachable(true);
        QueueReplay(horizontal ? 0x1000u : 0x800u, unchecked((uint)(int)(short)(mouseData >> 16)));
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
        bool swallow = (_heldSwallowed & button) != 0 ||
            (_heldDelivered & button) != 0 && !_identity.StillThere();
        if ((Volatile.Read(ref _replayedButtons) & (int)button) != 0)
        {
            Interlocked.And(ref _replayedButtons, ~(int)button);
            if (!swallow)
            {
                SetReachable(true); // In case a capture let it hide during the drag.
                var (_, up, data) = FlagsFor(button);
                QueueReplay(up, data);
                swallow = true;
            }
        }
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
        _replays.CompleteAdding();
        _replayer?.Join(1000);
        // A replayed press whose release never came must not leave the button held down in the app.
        int held = Interlocked.Exchange(ref _replayedButtons, 0);
        foreach (MouseButtons button in new[] { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle, MouseButtons.XButton1, MouseButtons.XButton2 })
            if ((held & (int)button) != 0)
            {
                var (_, up, data) = FlagsFor(button);
                SendMouse(up, data);
            }
        _moved.Dispose();
        _replays.Dispose();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
