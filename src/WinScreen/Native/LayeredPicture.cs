using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// Shows a picture with see-through parts as a whole window, for a window that has nothing behind what it draws. The
/// window must have the layered style and must not have been given a single opacity: Windows takes one or the other.
/// </summary>
internal static partial class LayeredPicture
{
    private const int ULW_ALPHA = 2;
    private const byte AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend { public byte Op, Flags, Opacity, Format; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint window, nint screen, nint place, ref NativeSize size, nint source,
        ref NativePoint from, int key, ref Blend blend, int flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    /// <summary>Puts the picture on the window where it is, at <paramref name="opacity"/> overall. False if Windows refused.</summary>
    internal static bool Show(nint window, Bitmap picture, byte opacity)
    {
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = picture.GetHbitmap(Color.FromArgb(0));
        nint previous = SelectObject(memory, bitmap);
        try
        {
            var size = new NativeSize { Width = picture.Width, Height = picture.Height };
            var from = new NativePoint();
            var blend = new Blend { Opacity = opacity, Format = AC_SRC_ALPHA };
            return UpdateLayeredWindow(window, screen, 0, ref size, memory, ref from, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
}
