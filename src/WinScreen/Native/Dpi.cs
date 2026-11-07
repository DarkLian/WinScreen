namespace WinScreen.Native;

/// <summary>Turns sizes designed at 100% display scaling into pixels for a window's monitor.</summary>
internal static class Dpi
{
    public const int Default = 96;

    public static int Of(nint window)
    {
        int dpi = (int)User32.GetDpiForWindow(window);
        return dpi > 0 ? dpi : Default;
    }

    /// <summary>A monitor's scaling in percent, or 100 when Windows won't say.</summary>
    public static int ScaleOf(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? (int)Math.Round(x * 100.0 / Default) : 100;

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    public static int Scale(int value, int dpi) => (int)Math.Round(value * (double)dpi / Default);
}
