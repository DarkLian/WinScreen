using System.Runtime.InteropServices;
using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Mouse messages for a stationary parked source. Coordinates are always taken from the current view and source.</summary>
internal sealed class PipMessageInput(nint source, WindowIdentity identity)
{
    private MouseButtons _held;
    internal nint PressTarget { get; private set; }
    internal string PressTransport { get; private set; } = "messages";
    private nint _dragTarget;
    private Point _lastPointer;
    private PipControlInput? _controls;
    private Point? _controlPress;
    private readonly Dictionary<MouseButtons, (long Time, Point At, nint Target)> _clicks = [];

    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal Point MappedScreen(Point pointer, Rectangle view, Rectangle shown)
    {
        identity.Require();
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
    private nint Keys(MouseButtons held)
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

    private bool Post(nint target, int message, nint keys, Point mapped, bool screenCoordinates = false)
    {
        if (target == 0 || !identity.StillThere() || !User32.IsWindow(target)) return false;
        User32.GetWindowThreadProcessId(target, out uint process);
        if (process != identity.ProcessId) return false;
        var point = new NativePoint { X = mapped.X, Y = mapped.Y };
        if (!screenCoordinates && !ScreenToClient(target, ref point)) return false;
        return User32.PostMessage(target, message, keys, Pack(new Point(point.X, point.Y)));
    }

    internal bool Move(Point pointer, Rectangle view, Rectangle shown)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        if (_controlPress is not null) return true;
        return Post(_held != MouseButtons.None ? _dragTarget : TargetAt(mapped), 0x200, Keys(_held), mapped);
    }

    internal bool Press(Point pointer, MouseButtons button, Rectangle view, Rectangle shown)
    {
        _lastPointer = pointer;
        Point mapped = MappedScreen(pointer, view, shown);
        nint target = _held != MouseButtons.None ? _dragTarget : TargetAt(mapped);
        PressTarget = target;
        PressTransport = "messages";
        int down = DownMessage(button);
        long now = Environment.TickCount64;
        bool doubleClick = _clicks.TryGetValue(button, out var previous) && previous.Target == target &&
            now - previous.Time <= GetDoubleClickTime() && Math.Abs(pointer.X - previous.At.X) <= SystemInformation.DoubleClickSize.Width / 2 &&
            Math.Abs(pointer.Y - previous.At.Y) <= SystemInformation.DoubleClickSize.Height / 2 &&
            ((long)GetClassLongPtr(target, -26) & 8) != 0;
        MouseButtons held = _held | button;
        if (button == MouseButtons.Left && _held == MouseButtons.None &&
            WindowInfo.ClassName(target) == "Microsoft.UI.Content.DesktopChildSiteBridge")
        {
            PressTransport = "automation";
            _controlPress = mapped;
            _held = held;
            _dragTarget = target;
            return true;
        }
        nint keys = ButtonKeys(button, held);
        if (!Post(target, down + (doubleClick ? 2 : 0), keys, mapped)) return false;
        if (doubleClick) _clicks.Remove(button);
        else _clicks[button] = (now, pointer, target);
        _held = held;
        _dragTarget = target;
        return true;
    }

    internal bool Release(Point pointer, MouseButtons button, Rectangle view, Rectangle shown)
    {
        if ((_held & button) == 0) return false;
        _held &= ~button;
        if (button == MouseButtons.Left && _controlPress is { } press)
        {
            _controlPress = null;
            Point release = MappedScreen(pointer, view, shown);
            if (Math.Abs(release.X - press.X) > SystemInformation.DragSize.Width ||
                Math.Abs(release.Y - press.Y) > SystemInformation.DragSize.Height) return false;
            return (_controls ??= new PipControlInput(identity)).Click(_dragTarget, press);
        }
        return Post(_dragTarget, DownMessage(button) + 1, ButtonKeys(button, _held), MappedScreen(pointer, view, shown));
    }

    internal bool Wheel(Point pointer, bool horizontal, uint data, Rectangle view, Rectangle shown)
    {
        Point mapped = MappedScreen(pointer, view, shown);
        return Post(TargetAt(mapped), horizontal ? 0x20e : 0x20a,
            (nint)((long)(data & 0xffff0000u) | (long)Keys(_held)), mapped, true);
    }

    internal void ReleaseAll(Rectangle view, Rectangle shown)
    {
        if (_controlPress is not null) { _controlPress = null; _held &= ~MouseButtons.Left; }
        _controls?.Dispose();
        foreach (MouseButtons button in new[] { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle, MouseButtons.XButton1, MouseButtons.XButton2 })
            if ((_held & button) != 0 && identity.StillThere()) Release(_lastPointer, button, view, shown);
    }

    private nint ButtonKeys(MouseButtons button, MouseButtons held) =>
        (nint)((long)Keys(held) | (button is MouseButtons.XButton1 or MouseButtons.XButton2 ? (button == MouseButtons.XButton1 ? 1L : 2L) << 16 : 0));
    private static int DownMessage(MouseButtons button) => button switch
    {
        MouseButtons.Left => 0x201, MouseButtons.Right => 0x204, MouseButtons.Middle => 0x207, _ => 0x20b,
    };
}
