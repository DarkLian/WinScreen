using WinScreen.Windows;
using static WinScreen.Settings.UiText;

namespace WinScreen.Pip;

/// <summary>Independent compositor path. No D3D device, frame timer or custom scaling.</summary>
internal sealed class DwmThumbnailBackend : IPipRenderBackend
{
    private readonly nint _source;
    private readonly nint _destination;
    private readonly DwmThumbnail _thumbnail;
    private DwmThumbnail? _selection;
    private Rectangle _viewport, _crop;
    private byte _opacity = 255;
    private bool _visible = true, _selecting, _disposed;

    internal DwmThumbnailBackend(nint destination, nint source)
    {
        _source = source;
        _destination = destination;
        _thumbnail = DwmThumbnail.TryCreate(destination, source) ??
            throw new WindowOperationException(T("This window can't be shown in Picture-in-Picture."));
    }

    public PipBackendKind Kind => PipBackendKind.DwmThumbnail;
    public PipSessionState State => _disposed ? PipSessionState.Stopped : PipSessionState.Compositor;
    public Size SourceSize => _disposed ? Size.Empty : _thumbnail.SourceSize;
    public void SetViewport(Rectangle destination, Rectangle source)
    {
        (_viewport, _crop) = (destination, source);
        Update();
    }
    public void SetOpacity(byte opacity) { _opacity = opacity; Update(); }
    public void SetVisible(bool visible) { _visible = visible; Update(); }
    private void Update()
    {
        if (_disposed || _viewport.Width <= 0 || _viewport.Height <= 0) return;
        _thumbnail.Show(_viewport, _crop, _selecting ? (byte)(_opacity * 90 / 255) : _opacity, _visible);
    }
    public void SetSelection(Rectangle? selection)
    {
        if (_disposed || (!_selecting && !selection.HasValue)) return;
        _selecting = selection.HasValue;
        if (!_selecting)
        {
            _selection?.Dispose();
            _selection = null;
        }
        else if (selection is { Width: > 0, Height: > 0 } rectangle)
        {
            _selection ??= DwmThumbnail.TryCreate(_destination, _source);
            _selection?.Show(rectangle, PipGeometry.SelectionToSource(rectangle, _viewport, _crop), _opacity);
        }
        Update();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection?.Dispose();
        _thumbnail.Dispose();
    }
}
