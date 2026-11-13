using System.Runtime.InteropServices;
using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>What became of a press or release handed to the hidden window.</summary>
internal enum Delivery
{
    /// <summary>Nothing reached the window: it is gone, busy, or the surface can't take this input.</summary>
    Failed,
    /// <summary>Posted to a responsive window of the source's process at the mapped point.</summary>
    Posted,
    /// <summary>Waiting for the release: the outcome is reported when the control has been activated.</summary>
    Deferred,
    /// <summary>The window's way of taking input has no such operation (see <see cref="PipInputRoutes.Supports"/>).</summary>
    Unsupported,
}

/// <summary>
/// Mouse messages for a stationary parked source. Coordinates are always taken from the current view and source.
/// Used from one thread at a time: the visit's input worker, then its teardown.
/// </summary>
internal sealed class PipMessageInput(nint source, WindowIdentity identity)
{
    private MouseButtons _held;
    internal bool EnhancedHover;
    internal PipEnhancedHover Hover { get; } = new(identity);
    internal InputRoute MoveRoute { get; private set; }
    internal nint PressTarget { get; private set; }
    internal string PressTransport { get; private set; } = "messages";
    /// <summary>The window the last move went to, which is also where the pointer's shape comes from.</summary>
    internal nint MoveTarget { get; private set; }
    private nint _dragTarget;
    private Point _lastPointer;
    private Point? _controlPress;
    private bool _menuPress;
    private PipControlInput.Request _controlRequest;
    private int _wheelRemainder;
    /// <summary>Why the last press or release couldn't be delivered, for the press log.</summary>
    internal string Why { get; private set; } = "";
    // Windows' double-click bookkeeping: a second click within the time and rectangle is a double-click message.
    private readonly Dictionary<MouseButtons, (long Time, Point At, nint Target)> _clicks = [];
    // Each button's latest press and where it went in the app. Presses that follow it within the double-click time
    // and rectangle (a double or triple click) go to the same point, and so does a release that didn't really move.
    private readonly Dictionary<MouseButtons, (long Time, Point At, nint Target, Point Mapped)> _spot = [];
    private Point _pressAt, _pressMapped;

    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetMenu(nint window);
    [DllImport("user32.dll")] private static extern int MenuItemFromPoint(nint window, nint menu, NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(nint menu, uint item, System.Text.StringBuilder text, int length, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(nint window);

    private InputRoute RouteOf(nint target) => PipInputRoutes.RouteFor(target, source, identity.ProcessId);

    internal Point MappedScreen(Point pointer, Rectangle view, Rectangle shown)
    {
        // The quick check: this runs for every move. The visit checked the process fully when it began.
        if (!identity.StillThere()) throw new WindowOperationException(T("errors.identity.originalGone"));
        if (!User32.GetWindowRect(source, out Rect bounds)) throw new WindowOperationException(T("errors.pip.windowUnavailable"));
        Point spot = PipGeometry.PointToSource(pointer, view, shown);
        return new Point(bounds.Left + spot.X, bounds.Top + spot.Y);
    }

    internal nint Hit(Point pointer, Rectangle view, Rectangle shown)
    {
        Point mapped = MappedScreen(pointer, view, shown);
        if (User32.SendMessageTimeout(source, User32.WM_NCHITTEST, 0, Pack(mapped), 2, 30, out nint hit) != 0) return hit;
        // Busy for a moment, as Word is just after the first click of a double-click: ask once more and wait a little
        // longer. An app that still doesn't answer isn't responding, and its press isn't sent (it could act later).
        return User32.SendMessageTimeout(source, User32.WM_NCHITTEST, 0, Pack(mapped), 2, 250, out hit) != 0 ? hit : -2;
    }

    internal nint TargetAt(Point mapped)
    {
        nint target = source;
        for (int depth = 0; depth < 32; depth++)
        {
            nint child = InputChildAt(target, mapped);
            if (child == 0) return target;
            target = child;
        }
        return 0;
    }

    private static nint InputChildAt(nint parent, Point mapped)
    {
        // Rendering surfaces can be children of another process (Chromium's GPU window, for
        // example). Rectangle-only searches pick those even when they decline mouse input.
        // Walk siblings in Z order, respecting both click-through styles and hit testing.
        for (nint child = User32.GetWindow(parent, 5 /* GW_CHILD */); child != 0;
             child = User32.GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            if (!User32.IsWindowVisible(child) ||
                ((long)User32.GetWindowLongPtr(child, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) != 0 ||
                !User32.GetWindowRect(child, out Rect bounds) ||
                !Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom).Contains(mapped)) continue;
            if (User32.SendMessageTimeout(child, User32.WM_NCHITTEST, 0, Pack(mapped), 2, 20, out nint hit) == 0)
                continue;
            if (hit is -1 or 0 or -2) continue;
            return child;
        }
        return 0;
    }

    private static nint Pack(Point point) => (nint)((point.Y << 16) | (point.X & 0xffff));
    private static nint Keys(MouseButtons held)
    {
        int keys = 0;
        if ((held & MouseButtons.Left) != 0) keys |= 1;
        if ((held & MouseButtons.Right) != 0) keys |= 2;
        if ((held & MouseButtons.Middle) != 0) keys |= 0x10;
        if ((held & MouseButtons.XButton1) != 0) keys |= 0x20;
        if ((held & MouseButtons.XButton2) != 0) keys |= 0x40;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) keys |= 4;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) keys |= 8;
        return keys;
    }

    /// <param name="evenIfBusy">
    /// For a release that pairs a press already sent: the app must not be left with a button held down when it
    /// catches up. Anything else isn't sent to a window that isn't responding, or it would act much later.
    /// </param>
    private bool Post(nint target, int message, nint keys, Point mapped, bool screenCoordinates = false, bool evenIfBusy = false)
    {
        if (target == 0 || !identity.StillThere() || !User32.IsWindow(target)) return false;
        User32.GetWindowThreadProcessId(target, out uint process);
        if (process != identity.ProcessId) return false;
        if (!evenIfBusy && IsHungAppWindow(target)) return false;
        var point = new NativePoint { X = mapped.X, Y = mapped.Y };
        if (screenCoordinates) return User32.PostMessage(target, message, keys, Pack(mapped));
        if (!ScreenToClient(target, ref point)) return false;
        return PostClient(target, message, keys, new Point(point.X, point.Y));
    }

    /// <summary>
    /// Posts a client point (in pixels) to the window in its own coordinates. For an app that isn't per-monitor aware,
    /// Windows rescales a point posted from WinScreen's context on delivery, and gets it wrong for a window parked off
    /// every display beside a display at another scale (about 250 points too high at 150%). Posted from a thread in
    /// the app's own context, the point arrives as sent, so it is scaled here by the window's own two sizes.
    /// </summary>
    private static bool PostClient(nint target, int message, nint keys, Point client)
    {
        nint context = GetWindowDpiAwarenessContext(target);
        if (context == 0 || GetAwarenessFromDpiAwarenessContext(context) is not (0 or 1)) // Unaware or system aware.
            return User32.PostMessage(target, message, keys, Pack(client));
        // The top-level window gives the most exact scale, when it shares the target's context.
        nint root = User32.GetAncestor(target, 2 /* GA_ROOT */);
        nint measured = root != 0 && AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(root), context) ? root : target;
        if (!User32.GetWindowRect(measured, out Rect pixels)) return false;
        nint previous = SetThreadDpiAwarenessContext(context);
        if (previous == 0) return User32.PostMessage(target, message, keys, Pack(client));
        try
        {
            if (!User32.GetWindowRect(measured, out Rect own)) return false;
            static double Ratio(int ownSize, int pixelSize) => ownSize > 0 && pixelSize > 0 ? ownSize / (double)pixelSize : 1;
            double x = Ratio(own.Right - own.Left, pixels.Right - pixels.Left), y = Ratio(own.Bottom - own.Top, pixels.Bottom - pixels.Top);
            return User32.PostMessage(target, message, keys,
                Pack(new Point((int)Math.Round(client.X * x), (int)Math.Round(client.Y * y))));
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    /// <summary>The access key of the menu bar item at a point: its marked letter, or else its first, as Windows matches.</summary>
    private nint? MenuKeyAt(Point mapped)
    {
        nint bar = GetMenu(source);
        if (bar == 0) return null;
        int item = MenuItemFromPoint(source, bar, new NativePoint { X = mapped.X, Y = mapped.Y });
        if (item < 0) return null;
        var text = new System.Text.StringBuilder(256);
        if (GetMenuString(bar, (uint)item, text, text.Capacity, 0x400 /* MF_BYPOSITION */) <= 0) return null;
        string name = text.ToString();
        int marked = name.IndexOf('&');
        while (marked >= 0 && marked + 1 < name.Length && name[marked + 1] == '&') marked = name.IndexOf('&', marked + 2);
        char key = marked >= 0 && marked + 1 < name.Length ? name[marked + 1] : name.TrimStart().FirstOrDefault();
        return key == '\0' ? null : char.ToLowerInvariant(key);
    }

    internal bool Move(Point pointer, Rectangle view, Rectangle shown)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        if (_controlPress is not null) return true;
        // Within the double-click time of a left press, a move that stays inside the double-click rectangle isn't a
        // move, as Windows treats it. In a view shown smaller, one pixel of hand jitter is several in the app, which
        // would take it as the pointer moving away and count the next press as a new single click.
        if (AtLastPress(MouseButtons.Left, pointer, 0, out _)) return true;
        nint target = _held != MouseButtons.None ? _dragTarget : TargetAt(mapped);
        MoveTarget = target;
        MoveRoute = RouteOf(target);
        Hover.Update(EnhancedHover && MoveRoute == InputRoute.Messages ? target : 0);
        // Only posted messages carry a pointer; other routes have nothing to move.
        return MoveRoute == InputRoute.Messages && Post(target, 0x200, Keys(_held), mapped);
    }

    /// <summary>The pointer is still where the button was last pressed, within the double-click time (and, given a target, on it).</summary>
    private bool AtLastPress(MouseButtons button, Point pointer, nint target, out Point mapped)
    {
        mapped = default;
        if (!_spot.TryGetValue(button, out var last) || (target != 0 && last.Target != target) ||
            Environment.TickCount64 - last.Time > GetDoubleClickTime() || !WithinDoubleClick(pointer, last.At)) return false;
        mapped = last.Mapped;
        return true;
    }

    private static bool WithinDoubleClick(Point a, Point b) =>
        Math.Abs(a.X - b.X) <= SystemInformation.DoubleClickSize.Width / 2 && Math.Abs(a.Y - b.Y) <= SystemInformation.DoubleClickSize.Height / 2;

    /// <summary>Asks the window under the pointer to set its cursor, as Windows would if the pointer were really there.</summary>
    /// <returns>False when it didn't answer within 20 ms: it is busy, or not responding.</returns>
    internal bool AskForCursor()
    {
        nint target = MoveTarget;
        if (target == 0 || !identity.StillThere() || !User32.IsWindow(target)) return true;
        return User32.SendMessageTimeout(target, 0x0020 /* WM_SETCURSOR */, target, (nint)(1 | (0x0200 << 16)), 0x2, 20, out _) != 0;
    }

    internal Delivery Press(Point pointer, MouseButtons button, Rectangle view, Rectangle shown, bool menuBar = false)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        // A standard menu bar only follows a press while the button is really down over it, which it isn't. Its menus
        // also open from the keyboard, so the menu under the pointer is opened by its access key instead. A bar the
        // app draws itself takes the press as any other.
        if (menuBar && button == MouseButtons.Left && _held == MouseButtons.None && MenuKeyAt(mapped) is { } key)
        {
            PressTarget = source;
            PressTransport = "menu bar";
            Why = "";
            if (!identity.StillThere() || IsHungAppWindow(source) ||
                !User32.PostMessage(source, 0x112 /* WM_SYSCOMMAND */, 0xF100 /* SC_KEYMENU */, key)) return Delivery.Failed;
            _clicks.Remove(button);
            _spot.Remove(button);
            _menuPress = true;
            _held = button;
            _dragTarget = source;
            return Delivery.Posted;
        }
        nint target = _held != MouseButtons.None ? _dragTarget : TargetAt(mapped);
        PressTarget = target;
        Why = "";
        InputRoute route = RouteOf(target);
        PressTransport = route.ToString().ToLowerInvariant();
        long now = Environment.TickCount64;
        bool secondClick = _clicks.TryGetValue(button, out var previous) && previous.Target == target &&
            now - previous.Time <= GetDoubleClickTime() && WithinDoubleClick(pointer, previous.At);
        InputOperation operation = button switch
        {
            MouseButtons.Left => secondClick ? InputOperation.DoubleClick : InputOperation.Click,
            MouseButtons.Right => InputOperation.ContextClick,
            _ => InputOperation.OtherButton,
        };
        if (_held != MouseButtons.None && route != InputRoute.Messages) operation = InputOperation.Drag; // A chord.
        if (!PipInputRoutes.Supports(route, operation))
        {
            Why = route == InputRoute.Refused ? "another program's window" : $"{operation} on a surface that takes {route} input";
            return Delivery.Unsupported;
        }
        if (route == InputRoute.Automation)
        {
            // Acted on at the release, which also tells a click from a drag.
            _controlPress = mapped;
            _controlRequest = operation switch
            {
                InputOperation.ContextClick => PipControlInput.Request.ContextMenu,
                InputOperation.DoubleClick => PipControlInput.Request.DoubleClick,
                _ => PipControlInput.Request.Click,
            };
            if (operation == InputOperation.DoubleClick) _clicks.Remove(button);
            else _clicks[button] = (now, pointer, target);
            _held |= button;
            _dragTarget = target;
            return Delivery.Deferred;
        }
        int down = DownMessage(button);
        bool doubleClick = secondClick && ((long)GetClassLongPtr(target, -26) & 8) != 0;
        MouseButtons held = _held | button;
        // The second press of a double-click (or third of a triple) lands where the first did, as moves above.
        Point first = pointer;
        if (AtLastPress(button, pointer, target, out Point same)) { mapped = same; first = _spot[button].At; }
        if (!Post(target, down + (doubleClick ? 2 : 0), ButtonKeys(button, held), mapped)) return Delivery.Failed;
        if (doubleClick) _clicks.Remove(button);
        else _clicks[button] = (now, pointer, target);
        _spot[button] = (now, first, target, mapped);
        _pressAt = pointer;
        _pressMapped = mapped;
        _held = held;
        _dragTarget = target;
        return Delivery.Posted;
    }

    /// <param name="activated">For a deferred press: called, from another thread, with what activation did.</param>
    /// <returns>
    /// Posted for a release that pairs a posted press; Deferred once a control activation is queued; Failed when
    /// a deferred press can't become a click (it was a drag, or the queue was full), with <paramref name="why"/>.
    /// </returns>
    internal Delivery Release(Point pointer, MouseButtons button, Rectangle view, Rectangle shown,
        Action<PipControlInput.Result>? activated, out string why)
    {
        why = "";
        if ((_held & button) == 0) return Delivery.Failed;
        _held &= ~button;
        if (_menuPress)
        {
            // The menu that opened tracks the pointer by itself from here; a release would close it again.
            _menuPress = false;
            return Delivery.Posted;
        }
        if (_controlPress is { } press)
        {
            _controlPress = null;
            Point release = MappedScreen(pointer, view, shown);
            if (Math.Abs(release.X - press.X) > SystemInformation.DragSize.Width ||
                Math.Abs(release.Y - press.Y) > SystemInformation.DragSize.Height)
            {
                why = "a drag on a surface that takes Automation input";
                return Delivery.Unsupported;
            }
            if (activated is null || !PipControlInput.Shared.Submit(identity, _dragTarget, press, _controlRequest, activated))
            {
                why = "control input is busy";
                return Delivery.Failed;
            }
            return Delivery.Deferred;
        }
        Point at = MappedScreen(pointer, view, shown);
        if (WithinDoubleClick(pointer, _pressAt)) at = _pressMapped; // It didn't really move.
        return Post(_dragTarget, DownMessage(button) + 1, ButtonKeys(button, _held), at, evenIfBusy: true)
            ? Delivery.Posted : Delivery.Failed;
    }

    internal bool Wheel(Point pointer, bool horizontal, uint data, Rectangle view, Rectangle shown)
    {
        Point mapped = MappedScreen(pointer, view, shown);
        nint target = TargetAt(mapped);
        InputRoute route = RouteOf(target);
        if (!PipInputRoutes.Supports(route, InputOperation.Wheel)) return false;
        if (route == InputRoute.Automation)
        {
            // Lines, as the wheel would scroll them; a touchpad's small steps add up until they make one.
            _wheelRemainder += (short)(data >> 16) * SystemInformation.MouseWheelScrollLines;
            int lines = _wheelRemainder / 120;
            if (lines == 0) return true;
            _wheelRemainder -= lines * 120;
            // A positive turn scrolls up, or right for a sideways one.
            return PipControlInput.Shared.Scroll(identity, target, mapped, horizontal ? lines : -lines, horizontal, _ => { });
        }
        return Post(target, horizontal ? 0x20e : 0x20a,
            (nint)((long)(data & 0xffff0000u) | (long)Keys(_held)), mapped, true);
    }

    internal bool CanScroll(Point pointer, Rectangle view, Rectangle shown) =>
        PipInputRoutes.Supports(RouteOf(TargetAt(MappedScreen(pointer, view, shown))), InputOperation.Wheel);

    /// <summary>Releases every button still held, so none stays down in the app. Returns the buttons whose press was a pending click.</summary>
    internal MouseButtons ReleaseAll(Rectangle view, Rectangle shown)
    {
        MouseButtons cancelled = MouseButtons.None;
        if (_controlPress is not null) { _controlPress = null; cancelled = _held; _held = MouseButtons.None; }
        foreach (MouseButtons button in new[] { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle, MouseButtons.XButton1, MouseButtons.XButton2 })
            if ((_held & button) != 0 && identity.StillThere())
                try { Release(_lastPointer, button, view, shown, null, out _); } catch (WindowOperationException) { }
        _held = MouseButtons.None;
        return cancelled;
    }

    private static nint ButtonKeys(MouseButtons button, MouseButtons held) =>
        (nint)((long)Keys(held) | (button is MouseButtons.XButton1 or MouseButtons.XButton2 ? (button == MouseButtons.XButton1 ? 1L : 2L) << 16 : 0));
    private static int DownMessage(MouseButtons button) => button switch
    {
        MouseButtons.Left => 0x201, MouseButtons.Right => 0x204, MouseButtons.Middle => 0x207, _ => 0x20b,
    };
}
