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
    internal nint PressTarget { get; private set; }
    internal string PressTransport { get; private set; } = "messages";
    /// <summary>The window the last move went to, which is also where the pointer's shape comes from.</summary>
    internal nint MoveTarget { get; private set; }
    private nint _dragTarget;
    private Point _lastPointer;
    private Point? _controlPress;
    private PipControlInput.Request _controlRequest;
    private int _wheelRemainder;
    /// <summary>Why the last press or release couldn't be delivered, for the press log.</summary>
    internal string Why { get; private set; } = "";
    private readonly Dictionary<MouseButtons, (long Time, Point At, nint Target)> _clicks = [];

    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
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
        return User32.SendMessageTimeout(source, User32.WM_NCHITTEST, 0, Pack(mapped), 2, 30, out nint hit) != 0 ? hit : -2;
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
        if (!screenCoordinates && !ScreenToClient(target, ref point)) return false;
        return User32.PostMessage(target, message, keys, Pack(new Point(point.X, point.Y)));
    }

    internal bool Move(Point pointer, Rectangle view, Rectangle shown)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        if (_controlPress is not null) return true;
        nint target = _held != MouseButtons.None ? _dragTarget : TargetAt(mapped);
        MoveTarget = target;
        // Only posted messages carry a pointer; other routes have nothing to move.
        return RouteOf(target) == InputRoute.Messages && Post(target, 0x200, Keys(_held), mapped);
    }

    /// <summary>Asks the window under the pointer to set its cursor, as Windows would if the pointer were really there.</summary>
    internal void AskForCursor()
    {
        nint target = MoveTarget;
        if (target != 0 && identity.StillThere() && User32.IsWindow(target))
            User32.SendMessageTimeout(target, 0x0020 /* WM_SETCURSOR */, target, (nint)(1 | (0x0200 << 16)), 0x2, 20, out _);
    }

    internal Delivery Press(Point pointer, MouseButtons button, Rectangle view, Rectangle shown)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        nint target = _held != MouseButtons.None ? _dragTarget : TargetAt(mapped);
        PressTarget = target;
        Why = "";
        InputRoute route = RouteOf(target);
        PressTransport = route.ToString().ToLowerInvariant();
        long now = Environment.TickCount64;
        bool secondClick = _clicks.TryGetValue(button, out var previous) && previous.Target == target &&
            now - previous.Time <= GetDoubleClickTime() && Math.Abs(pointer.X - previous.At.X) <= SystemInformation.DoubleClickSize.Width / 2 &&
            Math.Abs(pointer.Y - previous.At.Y) <= SystemInformation.DoubleClickSize.Height / 2;
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
        if (!Post(target, down + (doubleClick ? 2 : 0), ButtonKeys(button, held), mapped)) return Delivery.Failed;
        if (doubleClick) _clicks.Remove(button);
        else _clicks[button] = (now, pointer, target);
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
        return Post(_dragTarget, DownMessage(button) + 1, ButtonKeys(button, _held), MappedScreen(pointer, view, shown), evenIfBusy: true)
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
