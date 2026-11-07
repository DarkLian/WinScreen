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

    public static int Scale(int value, int dpi) => (int)Math.Round(value * (double)dpi / Default);
}
