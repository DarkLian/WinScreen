using System.Runtime.InteropServices;

namespace WinScreen.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Size32
{
    public int Width, Height;
}

/// <summary>Matches DWM_THUMBNAIL_PROPERTIES, which dwmapi.h declares with one-byte packing.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DwmThumbnailProperties
{
    public uint Flags;
    public Rect Destination;
    public Rect Source;
    public byte Opacity;
    public int Visible;
    public int SourceClientAreaOnly;
}

internal static partial class Dwmapi
{
    public const uint DWM_TNP_RECTDESTINATION = 0x1;
    public const uint DWM_TNP_RECTSOURCE = 0x2;
    public const uint DWM_TNP_OPACITY = 0x4;
    public const uint DWM_TNP_VISIBLE = 0x8;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmRegisterThumbnail(nint hwndDestination, nint hwndSource, out nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmQueryThumbnailSourceSize(nint thumbnail, out Size32 size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
