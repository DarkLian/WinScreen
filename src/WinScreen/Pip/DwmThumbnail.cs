using System.Drawing;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// A live view of one window drawn into another by the Desktop Window Manager. DWM composites it
/// on the GPU from the source window's own surface, so WinScreen copies no pixels and the view keeps
/// updating while the source is covered by other windows.
/// </summary>
internal sealed class DwmThumbnail : IDisposable
{
    private nint _handle;

    private DwmThumbnail(nint handle) => _handle = handle;

    /// <summary>Returns null when Windows refuses, for example for a window that was just closed.</summary>
    public static DwmThumbnail? TryCreate(nint destination, nint source)
    {
        return Dwmapi.DwmRegisterThumbnail(destination, source, out nint handle) == 0 ? new DwmThumbnail(handle) : null;
    }

    public Size SourceSize
    {
        get
        {
            return Dwmapi.DwmQueryThumbnailSourceSize(_handle, out Size32 size) == 0
                ? new Size(size.Width, size.Height)
                : Size.Empty;
        }
    }

    public void Show(Rectangle destination, Rectangle source, byte opacity = 255)
    {
        var properties = new DwmThumbnailProperties
        {
            Flags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY |
                    Dwmapi.DWM_TNP_VISIBLE,
            Destination = ToRect(destination),
            Source = ToRect(source),
            Opacity = opacity,
            Visible = 1,
        };
        if (destination.Width <= 0 || destination.Height <= 0 || source.Width <= 0 || source.Height <= 0 ||
            Dwmapi.DwmUpdateThumbnailProperties(_handle, ref properties) != 0)
            throw new Windows.WindowOperationException(
                Settings.UiText.T("Windows could not render the live preview. The original will remain available."));
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Dwmapi.DwmUnregisterThumbnail(_handle);
            _handle = 0;
        }
    }

    private static Rect ToRect(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
