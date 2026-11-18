namespace WinScreen.Native;

internal static class WindowsBuild
{
    // Windows 10 version 2004: older builds lack what hiding views from screen capture needs.
    public const int Minimum = 19041;
}
