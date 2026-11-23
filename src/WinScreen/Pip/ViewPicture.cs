using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using Color = System.Drawing.Color;
using Rect = WinScreen.Native.Rect;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinScreen.Native;
using static Vortice.Direct3D11.D3D11;

namespace WinScreen.Pip;

// A view's picture as a bitmap: what it shows, covers blacked out, turned as it is. The pixels come from the view's
// capture, or straight from the window when Windows' thumbnail is what the view shows: never from the screen, where
// the view's opacity, anything over it, or its being left out of screen capture would change them.
internal static class ViewPicture
{
    // whole's top-left pixel is at origin in the source's pixels; shown and covers are in those pixels too.
    internal static Bitmap? Compose(Bitmap whole, Point origin, Rectangle shown, IReadOnlyList<Rectangle> covers, ViewTurn turn)
    {
        Rectangle part = Rectangle.Intersect(shown with { X = shown.X - origin.X, Y = shown.Y - origin.Y },
            new Rectangle(Point.Empty, whole.Size));
        if (part.Width <= 0 || part.Height <= 0) return null;
        Bitmap picture = whole.Clone(part, PixelFormat.Format32bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(picture))
                foreach (Rectangle cover in covers)
                    graphics.FillRectangle(Brushes.Black, cover.X - origin.X - part.X, cover.Y - origin.Y - part.Y, cover.Width, cover.Height);
            Turn(picture, turn);
            return picture;
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }

    // Clockwise quarter turns, then mirrored left to right: ViewTurn's order.
    internal static void Turn(Bitmap picture, ViewTurn turn)
    {
        picture.RotateFlip(turn.Turns switch
        {
            1 => RotateFlipType.Rotate90FlipNone,
            2 => RotateFlipType.Rotate180FlipNone,
            3 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        });
        if (turn.Mirrored) picture.RotateFlip(RotateFlipType.RotateNoneFlipX);
    }

    // part is in the view's client pixels, inside picture area: the same part of the picture.
    internal static Rectangle InPicture(Rectangle part, Rectangle area, Size picture)
    {
        if (area.Width <= 0 || area.Height <= 0) return Rectangle.Empty;
        double across = picture.Width / (double)area.Width, down = picture.Height / (double)area.Height;
        Rectangle inside = Rectangle.Intersect(part, area);
        var found = Rectangle.FromLTRB((int)Math.Floor((inside.Left - area.Left) * across), (int)Math.Floor((inside.Top - area.Top) * down),
            (int)Math.Ceiling((inside.Right - area.Left) * across), (int)Math.Ceiling((inside.Bottom - area.Top) * down));
        return Rectangle.Intersect(found, new Rectangle(Point.Empty, picture));
    }

    // The window drawn by itself, in its surface's pixels (the corner of its frame at 0,0). Null when Windows can't
    // draw it this way, as it can't some apps: their picture comes back blank.
    internal static Bitmap? Print(nint window, Size surface)
    {
        if (!User32.GetWindowRect(window, out Rect bounds)) return null;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 67_108_864) return null;
        var picture = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            bool drawn;
            using (Graphics graphics = Graphics.FromImage(picture))
            {
                nint context = graphics.GetHdc();
                try { drawn = User32.PrintWindow(window, context, PrintFullContent); }
                finally { graphics.ReleaseHdc(context); }
            }
            if (!drawn || Blank(picture))
            {
                picture.Dispose();
                return null;
            }
            if (surface.Width > 0 && surface.Height > 0 && surface != picture.Size)
            {
                var scaled = new Bitmap(picture, surface);
                picture.Dispose();
                return scaled;
            }
            return picture;
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }

    private const uint PrintFullContent = 2;

    internal static bool Blank(Bitmap picture)
    {
        Color first = picture.GetPixel(0, 0);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                if (picture.GetPixel(x * (picture.Width - 1) / 15, y * (picture.Height - 1) / 15).ToArgb() != first.ToArgb())
                    return false;
        return true;
    }

    // One frame from a capture of its own, for a window Windows can't draw by itself. Its pixels start at the origin
    // returned, in the source's pixels.
    internal static async Task<(Bitmap Picture, Point Origin)?> CaptureOnce(PipSource source, Size surface)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? winrt = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return null;
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out device, out _, out context).CheckError();
            winrt = WgcInterop.Device(device!);
            GraphicsCaptureItem item = source.CreateItem();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            CaptureBorder.TryHide(session, wait: 0);
            var arrived = new TaskCompletionSource<Direct3D11CaptureFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (sender, _) => { if (sender.TryGetNextFrame() is { } next && !arrived.TrySetResult(next)) next.Dispose(); };
            session.StartCapture();
            frame = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (frame is null) return null;
            using ID3D11Texture2D texture = WgcInterop.Texture(frame.Surface);
            int width = Math.Min(frame.ContentSize.Width, (int)texture.Description.Width);
            int height = Math.Min(frame.ContentSize.Height, (int)texture.Description.Height);
            if (width <= 0 || height <= 0) return null;
            using ID3D11Texture2D staging = device!.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            context!.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, width, height, 1));
            MappedSubresource map = context.Map(staging, 0, MapMode.Read);
            try
            {
                var picture = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                BitmapData bits = picture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    unsafe
                    {
                        for (int y = 0; y < height; y++)
                            Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, (byte*)bits.Scan0 + (long)y * bits.Stride,
                                bits.Stride, width * 4L);
                    }
                }
                finally { picture.UnlockBits(bits); }
                return (picture, source.CaptureOrigin(source.Whole(surface)));
            }
            finally { context.Unmap(staging, 0); }
        }
        catch (Exception failure) when (failure is TimeoutException or System.Runtime.InteropServices.COMException or
            SharpGen.Runtime.SharpGenException or InvalidOperationException or UnauthorizedAccessException or Windows.WindowOperationException)
        {
            Diagnostics.Log.Swallowed(failure);
            return null;
        }
        finally
        {
            frame?.Dispose();
            session?.Dispose();
            pool?.Dispose();
            winrt?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }
}
