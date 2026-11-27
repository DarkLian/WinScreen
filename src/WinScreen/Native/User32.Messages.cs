using System.Runtime.InteropServices;

namespace WinScreen.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Window;
    public int Message;
    public nint WParam, LParam;
    public uint Time;
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GuiThreadInfo
{
    public int Size, Flags;
    public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
    public Rect CaretBounds;

    public static GuiThreadInfo Empty => new() { Size = Marshal.SizeOf<GuiThreadInfo>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int X, Y;
    public uint Data, Flags, Time;
    public nuint Extra;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public int Type;
    public MouseInput Mouse;
}

// Message loops, hooks and input. The hook callbacks are delegates, which LibraryImport can't take, so these stay
// DllImport; whoever installs a hook keeps its delegate in a field for as long as Windows holds it.
internal static partial class User32
{
    public delegate nint HookProc(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int kind, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out Msg message, nint window, uint first, uint last);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out Msg message, nint window, uint first, uint last, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(in Msg message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern nint DispatchMessage(in Msg message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint thread, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint count, nint handles, [MarshalAs(UnmanagedType.Bool)] bool all,
        uint milliseconds, uint wake);

    [DllImport("user32.dll")]
    public static extern int GetMessageTime();

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool on);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(nint window);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    [DllImport("user32.dll")]
    public static extern int MenuItemFromPoint(nint window, nint menu, NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMenuString(nint menu, uint item, System.Text.StringBuilder text, int length, uint flags);

    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    public static extern nint RemoveProp(nint window, string name);

    [DllImport("user32.dll")]
    public static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    public static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);
}

internal static class TopWindows
{
    private delegate bool EnumProc(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc callback, nint data);

    // Top-level windows front to back, until the visitor says stop.
    internal static void Each(Func<nint, bool> visit) => EnumWindows((window, _) => visit(window), 0);
}
