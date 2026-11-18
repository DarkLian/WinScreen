namespace WinScreen.Native;

internal static class Dpi
{
    public const int Default = 96;

    public static int Of(nint window)
    {
        int dpi = (int)User32.GetDpiForWindow(window);
        return dpi > 0 ? dpi : Default;
    }

    public static int ScaleOf(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? (int)Math.Round(x * 100.0 / Default) : 100;

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    public static int Scale(int value, int dpi) => (int)Math.Round(value * (double)dpi / Default);
}
