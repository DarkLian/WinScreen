using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

internal sealed class DwmThumbnailBackend : IPipRenderBackend
{
    private readonly nint _source;
    private readonly nint _destination;
    private readonly DwmPieces _thumbnail;
    private DwmPieces? _selection;
    private Rectangle[] _covers = [];
    private Rectangle _viewport, _crop;
    private byte _opacity = 255;
    private bool _visible = true, _selecting, _disposed;

    internal DwmThumbnailBackend(nint destination, nint source)
    {
        _source = source;
        _destination = destination;
        _thumbnail = DwmPieces.TryCreate(destination, source) ??
            throw new WindowOperationException(T("errors.pip.windowUnavailable"));
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
        _thumbnail.Show(_viewport, _crop, _covers, _selecting ? (byte)(_opacity * 90 / 255) : _opacity, _visible);
    }

    // In the window's surface pixels, as the crop is.
    public void SetCovers(Rectangle[] covers)
    {
        _covers = covers;
        Update();
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
            _selection ??= DwmPieces.TryCreate(_destination, _source);
            _selection?.Show(rectangle, PipGeometry.SelectionToSource(rectangle, _viewport, _crop), _covers, _opacity);
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
