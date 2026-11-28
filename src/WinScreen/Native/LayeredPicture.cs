using System.Runtime.InteropServices;

namespace WinScreen.Native;

// The window must have the layered style and must not have been given a single opacity: Windows takes one or the other.
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

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindowAt(nint window, nint screen, ref NativePoint place, ref NativeSize size,
        nint source, ref NativePoint from, int key, ref Blend blend, int flags);

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

    internal static bool Show(nint window, Bitmap picture, byte opacity) => Show(window, picture, opacity, null);

    internal static bool Show(nint window, Bitmap picture, byte opacity, Point? at)
    {
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0);
            memory = CreateCompatibleDC(screen);
            bitmap = picture.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, bitmap);
            var size = new NativeSize { Width = picture.Width, Height = picture.Height };
            var from = new NativePoint();
            var blend = new Blend { Opacity = opacity, Format = AC_SRC_ALPHA };
            if (at is not { } place) return UpdateLayeredWindow(window, screen, 0, ref size, memory, ref from, 0, ref blend, ULW_ALPHA);
            var to = new NativePoint { X = place.X, Y = place.Y };
            return UpdateLayeredWindowAt(window, screen, ref to, ref size, memory, ref from, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}
