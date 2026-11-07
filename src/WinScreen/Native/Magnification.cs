using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>The Magnification API, used to show part of the screen through a colour filter.</summary>
internal static unsafe partial class Magnification
{
    public const string ControlClass = "Magnifier";
    public const uint MW_FILTERMODE_EXCLUDE = 0;

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MagInitialize();

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MagSetWindowSource(nint hwnd, Rect source);

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MagSetColorEffect(nint hwnd, float* effect);

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MagSetWindowFilterList(nint hwnd, uint filterMode, int count, nint* windows);
}
