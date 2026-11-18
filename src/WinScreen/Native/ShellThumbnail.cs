using System.Runtime.InteropServices;

namespace WinScreen.Native;

// The picture File Explorer shows for a file, which covers videos too.
internal static unsafe class ShellThumbnail
{
    private static readonly Guid ImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private const int GetImage = 3; // IShellItemImageFactory, after IUnknown.
    private const int ThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Extent
    {
        public int Width, Height;
    }

    // Call on a single-threaded apartment thread.
    internal static Bitmap? Of(string file, int largest)
    {
        if (SHCreateItemFromParsingName(file, 0, ImageFactory, out nint factory) < 0 || factory == 0) return null;
        try
        {
            nint bitmap = 0;
            var size = new Extent { Width = largest, Height = largest };
            nint* table = *(nint**)factory;
            if (((delegate* unmanaged[Stdcall]<nint, Extent, int, nint*, int>)table[GetImage])(factory, size, ThumbnailOnly, &bitmap) < 0 || bitmap == 0)
                return null;
            try
            {
                return Image.FromHbitmap(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid, out nint item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
