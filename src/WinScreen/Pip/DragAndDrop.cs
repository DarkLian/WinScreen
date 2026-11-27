using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

// Drag and drop as Windows runs it, and where a handed-over original goes. No state.
internal static class DragAndDrop
{
    internal static bool RunningInProcess(uint process)
    {
        if (process == 0) return false;
        try
        {
            using var running = System.Diagnostics.Process.GetProcessById((int)process);
            foreach (System.Diagnostics.ProcessThread thread in running.Threads)
                using (thread)
                    if (Running((uint)thread.Id)) return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { Diagnostics.Log.Swallowed(e); }
        return false;
    }

    internal static bool Running(uint thread)
    {
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && User32.GetGUIThreadInfo(thread, ref info) && info.Capture != 0 && WindowInfo.ClassName(info.Capture) == "CLIPBRDWNDCLASS";
    }

    private static readonly Size SmallestPickup = new(420, 300);

    // At most three quarters of its screen each way, so there is room left to drag to.
    internal static Rectangle PickupPlace(Size normal, Point pointer, Rectangle area)
    {
        int width = Math.Min(Math.Max(Math.Min(normal.Width, area.Width * 3 / 4), SmallestPickup.Width), area.Width);
        int height = Math.Min(Math.Max(Math.Min(normal.Height, area.Height * 3 / 4), SmallestPickup.Height), area.Height);
        int x = Math.Clamp(pointer.X - width / 2, area.Left, area.Right - width);
        int y = Math.Clamp(pointer.Y - height / 2, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    internal static Rectangle DropPlace(Point pointer, Point spot, Size size, Rectangle area)
    {
        int x = pointer.X - spot.X, y = pointer.Y - spot.Y;
        x = size.Width >= area.Width ? area.Left : Math.Clamp(x, area.Left, area.Right - size.Width);
        y = size.Height >= area.Height ? area.Top : Math.Clamp(y, area.Top, area.Bottom - size.Height);
        return new Rectangle(new Point(x, y), size);
    }

    internal static void PressThenBack(Point press, Point hand)
    {
        const uint Move = 0x0001, LeftDown = 0x0002, Absolute = 0x8000, VirtualDesk = 0x4000;
        Rectangle screen = SystemInformation.VirtualScreen;
        MouseInput At(Point p, uint flags) => new()
        {
            X = (int)((p.X - screen.Left + 0.5) * 65536 / screen.Width),
            Y = (int)((p.Y - screen.Top + 0.5) * 65536 / screen.Height),
            Flags = flags | Move | Absolute | VirtualDesk,
        };
        Input[] inputs = press == hand ? [new() { Type = 0, Mouse = At(press, LeftDown) }]
            : [new() { Type = 0, Mouse = At(press, LeftDown) }, new() { Type = 0, Mouse = At(hand, 0) }];
        User32.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }
}
