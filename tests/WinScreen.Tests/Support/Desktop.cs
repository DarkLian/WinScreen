using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Tests.Support;

/// <summary>What is really on the desktop, plus real mouse and keyboard input.</summary>
internal static partial class Desktop
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyUp = 0x2;
    private const uint MouseLeftDown = 0x2, MouseLeftUp = 0x4, MouseRightDown = 0x8, MouseRightUp = 0x10;
    private const uint MouseMiddleDown = 0x20, MouseMiddleUp = 0x40, MouseWheel = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public int Data;
        public uint Flags, Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public NativePoint Point;
    }

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out ThreadMessage message, nint window, uint first, uint last, uint remove);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size, Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInput info);

    public static uint LastInputTick()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? title);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    private delegate bool WindowVisitor(nint window, nint state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowVisitor visitor, nint state);

    public static nint TopWindowAt(Point point) => WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });

    public static Rectangle BoundsOf(nint window)
    {
        GetWindowRect(window, out NativeRect r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static long ExStyle(nint window) => User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);

    public static byte? Alpha(nint window)
    {
        return (ExStyle(window) & User32.WS_EX_LAYERED) != 0
               && User32.GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags)
               && (flags & User32.LWA_ALPHA) != 0
            ? alpha
            : null;
    }

    /// <summary>
    /// Earns the right to bring windows forward the way WinScreen does, by receiving a global hotkey
    /// on the calling thread. The key is one no keyboard has, so nothing else reacts to it.
    /// </summary>
    public static void GainForegroundRights()
    {
        WaitUntilNobodyIsTyping();
        const int id = 0x5753;
        if (!User32.RegisterHotKey(0, id, User32.MOD_CONTROL | User32.MOD_ALT, (uint)Keys.F22))
            throw new InvalidOperationException("Couldn't claim the test hotkey.");
        try
        {
            Press(Keys.F22, Keys.ControlKey, Keys.Menu);
            bool received = false;
            WaitFor(() => received, 2000, () =>
            {
                while (PeekMessage(out ThreadMessage message, 0, 0, 0, 1))
                    received |= message.Message == User32.WM_HOTKEY;
            });
            if (!received)
                throw new InvalidOperationException("The test hotkey never arrived.");
        }
        finally
        {
            User32.UnregisterHotKey(0, id);
        }
    }

    /// <summary>
    /// Someone using the PC takes the active window back and their keys would land in the test, so
    /// tests wait for a quiet moment before they touch either.
    /// </summary>
    private static void WaitUntilNobodyIsTyping()
    {
        const int quietMilliseconds = 1200;
        const int patienceMilliseconds = 120000;

        if (!WaitFor(() => MillisecondsSinceInput() >= quietMilliseconds, patienceMilliseconds))
            throw new InvalidOperationException("Someone is using this PC, so these tests can't run safely right now.");
    }

    private static uint MillisecondsSinceInput()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        GetLastInputInfo(ref info);
        return (uint)Environment.TickCount - info.Time;
    }

    /// <summary>Makes a test window the active one. Needs <see cref="GainForegroundRights"/> first.</summary>
    public static void Activate(nint window)
    {
        User32.SetForegroundWindow(window);
        if (!WaitFor(() => User32.GetForegroundWindow() == window, 2000))
            throw new InvalidOperationException("Test window didn't become active.");
    }

    /// <summary>Stops a test before it clicks anything but its own window.</summary>
    public static void RequireWindowAt(Point point, nint window)
    {
        WaitUntilNobodyIsTyping();
        if (!WaitFor(() => User32.GetAncestor(TopWindowAt(point), User32.GA_ROOT) == window, 2000))
        {
            nint found = User32.GetAncestor(TopWindowAt(point), User32.GA_ROOT);
            throw new InvalidOperationException(
                $"Refusing to use the mouse: \"{Windows.WindowInfo.Title(found)}\" ({Windows.WindowInfo.ClassName(found)}) is at {point}, with the mouse at {CursorPosition()}.");
        }
    }

    /// <summary>The average colour actually on screen, as composited by DWM.</summary>
    public static Color ColorOnScreen(Rectangle area)
    {
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);

        long red = 0, green = 0, blue = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
            }
        }

        int count = bitmap.Width * bitmap.Height;
        return Color.FromArgb((int)(red / count), (int)(green / count), (int)(blue / count));
    }

    public static bool IsNear(Color actual, Color expected, int tolerance = 24)
    {
        return Math.Abs(actual.R - expected.R) <= tolerance
               && Math.Abs(actual.G - expected.G) <= tolerance
               && Math.Abs(actual.B - expected.B) <= tolerance;
    }

    public static int CountProcessWindows(int processId, Func<string, bool> titleMatches)
    {
        int count = 0;
        EnumWindows((window, _) =>
        {
            User32.GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId && IsWindowVisible(window) && titleMatches(Windows.WindowInfo.Title(window)))
                count++;
            return true;
        }, 0);
        return count;
    }

    public static nint FindProcessWindow(int processId, Func<string, bool> titleMatches)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            User32.GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId && IsWindowVisible(window) && titleMatches(Windows.WindowInfo.Title(window)))
            {
                found = window;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    public static void Key(Keys key, bool down)
    {
        var input = new Input { Type = InputKeyboard };
        input.Data.Keyboard.Key = (ushort)key;
        input.Data.Keyboard.Flags = down ? 0 : KeyUp;
        Send(input);
    }

    /// <summary>Presses a key while holding the given modifier keys.</summary>
    public static void Press(Keys key, params Keys[] modifiers)
    {
        foreach (Keys modifier in modifiers)
            Key(modifier, true);
        Key(key, true);
        Key(key, false);
        foreach (Keys modifier in modifiers.Reverse())
            Key(modifier, false);
    }

    public static void MoveMouse(Point point) => SetCursorPos(point.X, point.Y);

    /// <summary>Moves the pointer as input, so low-level hooks see it. Setting its position doesn't reach them.</summary>
    public static void SlideMouse(Point point)
    {
        Rectangle screen = SystemInformation.VirtualScreen;
        mouse_event(0x8001 | 0x4000, (uint)((point.X - screen.Left + 0.5) * 65536 / screen.Width),
            (uint)((point.Y - screen.Top + 0.5) * 65536 / screen.Height), 0, 0);
    }

    /// <summary>Moves relative to now, with the system's pointer acceleration, as a real mouse does.</summary>
    public static void NudgeMouse(int x, int y) => mouse_event(0x0001, unchecked((uint)x), unchecked((uint)y), 0, 0);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    public static void MouseButton(bool down) => SendMouse(down ? MouseLeftDown : MouseLeftUp, 0);

    private static void Click(Point point)
    {
        MoveMouse(point);
        Thread.Sleep(50);
        MouseButton(true);
        MouseButton(false);
    }

    public static void ClickOn(Point point, nint window)
    {
        RequireWindowAt(point, window);
        Click(point);
    }

    public static void RightClickOn(Point point, nint window)
    {
        RequireWindowAt(point, window);
        MoveMouse(point);
        Thread.Sleep(50);
        SendMouse(MouseRightDown, 0);
        SendMouse(MouseRightUp, 0);
    }

    public static void MiddleClickOn(Point point, nint window)
    {
        RequireWindowAt(point, window);
        MoveMouse(point);
        Thread.Sleep(50);
        SendMouse(MouseMiddleDown, 0);
        SendMouse(MouseMiddleUp, 0);
    }

    public static void DoubleClick(Point point, nint window)
    {
        RequireWindowAt(point, window);
        Click(point);
        Thread.Sleep(30);
        Click(point);
    }

    public static void Wheel(Point point, int notches, nint window)
    {
        RequireWindowAt(point, window);
        MoveMouse(point);
        Thread.Sleep(50);
        SendMouse(MouseWheel, notches * 120);
    }

    /// <summary>Presses the left button and moves in small steps, like a hand would. The button stays down.</summary>
    public static void PressAndDrag(Point from, Point to, nint window)
    {
        RequireWindowAt(from, window);
        MoveMouse(from);
        Thread.Sleep(50);
        MouseButton(true);
        for (int i = 1; i <= 8; i++)
        {
            MoveMouse(new Point(from.X + (to.X - from.X) * i / 8, from.Y + (to.Y - from.Y) * i / 8));
            Thread.Sleep(15);
        }
    }

    public static void Drag(Point from, Point to, nint window)
    {
        PressAndDrag(from, to, window);
        // Setting the position reaches the window only once its queue is otherwise empty, but the release is
        // real input and can overtake the last moves on a slow machine. Give them time to land first.
        Thread.Sleep(150);
        MouseButton(false);
    }

    public static Point CursorPosition()
    {
        GetCursorPos(out NativePoint point);
        return new Point(point.X, point.Y);
    }

    /// <summary>Waits for a condition to come true, giving Windows time to act.</summary>
    public static bool WaitFor(Func<bool> condition, int milliseconds = 3000, Action? whileWaiting = null)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition())
                return true;
            whileWaiting?.Invoke();
            Thread.Sleep(20);
        }

        return condition();
    }

    private static void SendMouse(uint flags, int data)
    {
        var input = new Input { Type = InputMouse };
        input.Data.Mouse.Flags = flags;
        input.Data.Mouse.Data = data;
        Send(input);
    }

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Windows refused the simulated input.");
    }
}
