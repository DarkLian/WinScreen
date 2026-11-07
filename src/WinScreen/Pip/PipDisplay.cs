using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace WinScreen.Pip;

internal static partial class PipDisplay
{
    private static readonly ConcurrentDictionary<string, Task<int>> RefreshQueries = new();
    internal static int CachedRefresh(nint hwnd)
    {
        return CachedRefresh(Screen.FromHandle(hwnd).DeviceName);
    }

    internal static int CachedRefresh(string device)
    {
        // Display-driver queries can block for seconds. Never issue them from a WinForms timer.
        var query = RefreshQueries.GetOrAdd(device, name => Task.Run(() => RefreshDevice(name)));
        return query.IsCompletedSuccessfully ? query.Result : 60;
    }

    internal static void InvalidateRefresh() => RefreshQueries.Clear();
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags = 2);

    // DEVMODEW's display union is 16 bytes; the full layout must be retained for EnumDisplaySettings.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
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
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode result);

    private static int RefreshDevice(string device)
    {
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), Device = "", Form = "" };
        return EnumDisplaySettings(device, -1, ref mode) && mode.Frequency is >= 15 and <= 480
            ? (int)mode.Frequency : 60;
    }
}
