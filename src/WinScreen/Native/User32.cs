using System.Runtime.InteropServices;
using WinScreen.Localization;

namespace WinScreen.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left, Top, Right, Bottom;

    public Rect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int ShowCommand;
    public NativePoint MinimizedPosition;
    public NativePoint MaximizedPosition;
    public Rect NormalPosition;
}

/// <summary>What WM_COPYDATA carries: a number saying what the data is, and the data.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CopyData
{
    public nint Kind;
    public int Size;
    public nint Data;
}

/// <summary>The user32.dll calls WinScreen needs. Window handles are plain <see cref="nint"/>.</summary>
internal static partial class User32
{
    public const int WM_COPYDATA = 0x004A;

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const long WS_CAPTION = 0xC00000;
    public const long WS_THICKFRAME = 0x40000;
    public const long WS_EX_APPWINDOW = 0x40000;

    public const long WS_EX_TOPMOST = 0x8;
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_LAYERED = 0x80000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x80;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial nint GetMenu(nint window);

    public const uint LWA_COLORKEY = 0x1;
    public const uint LWA_ALPHA = 0x2;

    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOMOVE = 0x2;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const uint SWP_FRAMECHANGED = 0x20;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;
    public const int SW_MAXIMIZE = 3;

    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;

    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint WDA_NONE = 0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const int WM_CLOSE = 0x0010;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTCAPTION = 2;
    public const int HTMINBUTTON = 8;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint window, nint deviceContext, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint context, in BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(NativePoint point, uint flags);

    public delegate void WinEventProc(nint hook, uint kind, nint window, int item, int child, uint thread, uint time);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint first, uint last, nint module, nint callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(nint callback, nint state);
    public const int WM_HOTKEY = 0x0312;

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public static readonly nint HWND_BOTTOM = 1;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? title);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetProp(nint window, string name);

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProp(nint window, string name, nint value);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags,
        uint timeout, out nint result);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLayeredWindowAttributes(nint hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static unsafe partial int GetWindowText(nint hWnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static unsafe partial int GetClassName(nint hWnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint after, string? className, string? title);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hWnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(nint hWnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint command);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, int style, int x,
        int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hWnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    /// <summary>Marks a window as painted, so Windows stops asking it to paint; with no rectangle, all of it.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(nint hWnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HideCaret(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowCaret(nint window);

    /// <summary>The bounding box of a window's region in window coordinates; 0 when it has no region.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowRgnBox(nint hWnd, out Rect box);

    /// <summary>Windows owns the region after a successful call. Passing 0 removes the window's region.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowRgn(nint hWnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hWnd, out uint affinity);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    public const int SB_HORZ = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowScrollBar(nint hWnd, int bar, [MarshalAs(UnmanagedType.Bool)] bool show);

    public const uint CB_SETCUEBANNER = 0x1703;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendTextMessage(nint window, uint message, nint wParam, string text);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();
}
