namespace WinScreen.Native;

/// <summary>The oldest Windows WinScreen runs on.</summary>
internal static class WindowsBuild
{
    /// <summary>Windows 10 version 2004. Older builds lack what hiding views from screen capture needs.</summary>
    public const int Minimum = 19041;
}
