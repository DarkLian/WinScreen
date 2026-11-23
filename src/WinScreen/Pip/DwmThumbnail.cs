using System.Drawing;
using WinScreen.Localization;
using WinScreen.Native;

namespace WinScreen.Pip;

internal sealed class DwmThumbnail : IDisposable
{
    private nint _handle;
    private Rectangle _destination = new(0, 0, 1, 1), _source = new(0, 0, 1, 1);

    private DwmThumbnail(nint handle) => _handle = handle;

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

    public void Show(Rectangle destination, Rectangle source, byte opacity = 255, bool visible = true)
    {
        var properties = new DwmThumbnailProperties
        {
            Flags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY |
                    Dwmapi.DWM_TNP_VISIBLE,
            Destination = ToRect(destination),
            Source = ToRect(source),
            Opacity = opacity,
            Visible = visible ? 1 : 0,
        };
        if (destination.Width <= 0 || destination.Height <= 0 || source.Width <= 0 || source.Height <= 0 ||
            Dwmapi.DwmUpdateThumbnailProperties(_handle, ref properties) != 0)
            throw new Windows.WindowOperationException(
                UiText.T("errors.pip.renderFailed"));
        (_destination, _source) = (destination, source);
    }

    // Best effort: a thumbnail that can't be hidden is one whose window has gone. Hidden as Show hides one, with every
    // property given.
    public void Hide()
    {
        var properties = new DwmThumbnailProperties
        {
            Flags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_RECTSOURCE | Dwmapi.DWM_TNP_OPACITY | Dwmapi.DWM_TNP_VISIBLE,
            Destination = ToRect(_destination), Source = ToRect(_source), Opacity = 0, Visible = 0,
        };
        Dwmapi.DwmUpdateThumbnailProperties(_handle, ref properties);
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
