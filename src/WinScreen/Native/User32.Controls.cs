using System.Runtime.InteropServices;

namespace WinScreen.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PaintStruct
{
    public nint Dc;
    public int Erase, Left, Top, Right, Bottom, Restore, IncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ComboInfo
{
    public int Size;
    public Rect Item, Button;
    public int ButtonState;
    public nint Combo, Edit, List;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScrollInfo
{
    public uint Size, Mask;
    public int Min, Max;
    public uint Page;
    public int Pos, TrackPos;
}

// DEVMODEW's display union is 16 bytes; the full layout must be retained for EnumDisplaySettings.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    public ushort Spec, Driver, Size, Extra;
    public uint Fields;
    public int X, Y;
    public uint Orientation, FixedOutput;
    public short Color, Duplex, YResolution, TTOption, Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
    public ushort LogPixels;
    public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanWidth, PanHeight;
}

// Painting, controls and displays.
internal static partial class User32
{
    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(nint window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetComboBoxInfo(nint combo, ref ComboInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetScrollInfo(nint window, int bar, ref ScrollInfo info);

    [DllImport("user32.dll")]
    public static extern nint BeginDeferWindowPos(int count);

    [DllImport("user32.dll")]
    public static extern nint DeferWindowPos(nint batch, nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndDeferWindowPos(nint batch);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags = 2);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode result);
}
