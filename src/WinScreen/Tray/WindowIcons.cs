using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Tray;

// The icon a window shows on its title bar, or its program's.
internal static class WindowIcons
{
    private const uint WM_GETICON = 0x007F, AbortIfHung = 0x2, Milliseconds = 50;
    private const int Small = 0, Big = 1, SmallOrDrawn = 2, GCLP_HICON = -14, GCLP_HICONSM = -34;

    // The caller's to dispose. Null when nothing has one, as for a Store app's frame, whose program is Windows' own.
    internal static Bitmap? Of(nint window, int size)
    {
        Bitmap? best = null;
        try
        {
            foreach (nint handle in Handles(window))
            {
                if (Read(handle) is not { } icon) continue;
                if (best is not null && best.Width >= icon.Width) { icon.Dispose(); continue; }
                best?.Dispose();
                best = icon;
                if (best.Width >= size) break;
            }
            if (best is null && !string.Equals(WindowInfo.ProgramOf(window), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                WindowInfo.ExecutablePath(window) is { Length: > 0 } path && Icon.ExtractAssociatedIcon(path) is { } program)
                using (program) best = program.ToBitmap();
            return best is null ? null : Sized(best, size);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or
                                            ExternalException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Log.Swallowed(failure);
            return null;
        }
        finally { best?.Dispose(); }
    }

    private static IEnumerable<nint> Handles(nint window)
    {
        bool answers = true;
        nint Asked(int which)
        {
            if (!answers) return 0;
            answers = User32.SendMessageTimeout(window, WM_GETICON, which, 0, AbortIfHung, Milliseconds, out nint icon) != 0;
            return answers ? icon : 0;
        }
        yield return Asked(SmallOrDrawn);
        yield return Asked(Small);
        yield return User32.GetClassLongPtr(window, GCLP_HICONSM);
        yield return Asked(Big);
        yield return User32.GetClassLongPtr(window, GCLP_HICON);
    }

    // The icon is the app's: it isn't freed here, and the app may free it meanwhile.
    private static Bitmap? Read(nint handle)
    {
        if (handle == 0) return null;
        try
        {
            using Icon icon = Icon.FromHandle(handle);
            return icon.ToBitmap();
        }
        catch (Exception failure) when (failure is ArgumentException or ExternalException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Log.Swallowed(failure);
            return null;
        }
    }

    private static Bitmap Sized(Bitmap icon, int size)
    {
        var picture = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(picture);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var edges = new System.Drawing.Imaging.ImageAttributes();
        edges.SetWrapMode(WrapMode.TileFlipXY); // Or the edges fade into nothing.
        g.DrawImage(icon, new Rectangle(0, 0, size, size), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, edges);
        return picture;
    }
}
