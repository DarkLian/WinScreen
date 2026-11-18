using WinScreen.Windows;

namespace WinScreen.Glass;

internal sealed class ContentEffects : IDisposable
{
    private readonly WindowController windows;
    private readonly Dictionary<nint, ContentOverlay> _overlays = [];

    public ContentEffects(WindowController windows)
    {
        this.windows = windows;
        windows.OpacityChanged += (window, alpha) =>
        {
            if (_overlays.TryGetValue(window, out ContentOverlay? overlay) && !overlay.IsDisposed) overlay.SetOpacity(alpha);
        };
    }
    private ContentLevels _levels = new(0.15f, 1, 0.9f);

    public event Action<string>? Problem;

    public event Action<nint>? Created;

    public bool Has(nint window, ContentLook look) => _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.Look == look;

    public bool Has(nint window) => _overlays.ContainsKey(window);

    public int Count => _overlays.Count;
    internal IEnumerable<nint> Sources => _overlays.Keys;

    internal nint OverlayOf(nint window) =>
        _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.IsHandleCreated ? overlay.Handle : 0;

    public void SetLevels(ContentLevels levels)
    {
        _levels = levels;
        foreach (ContentOverlay overlay in _overlays.Values)
            overlay.SetLevels(levels);
    }

    public bool Toggle(nint window, ContentLook look)
    {
        if (_overlays.TryGetValue(window, out ContentOverlay? existing))
        {
            TurnOff(window);
            if (_overlays.ContainsKey(window) || existing.Look == look)
                return false;
        }

        var overlay = new ContentOverlay(window, look, _levels);
        try
        {
            byte opacity = windows.OpacityOf(window);
            windows.HideOwned(window, overlay, solid: true);
            overlay.Start();
            overlay.SetOpacity(opacity);
        }
        catch
        {
            if (windows.IsConcealedBy(window, overlay))
            {
                try { windows.ShowOwned(window, overlay); }
                catch (WindowOperationException)
                {
                    _overlays[window] = overlay;
                    throw;
                }
            }
            overlay.Dispose();
            throw;
        }

        _overlays[window] = overlay;
        overlay.Failed += message =>
        {
            TurnOff(window);
            Problem?.Invoke(message);
        };
        Created?.Invoke(overlay.Handle);
        return true;
    }

    public void TurnOff(nint window)
    {
        if (!_overlays.TryGetValue(window, out ContentOverlay? overlay))
            return;
        try
        {
            if (Native.User32.IsWindow(window))
                windows.ShowOwned(window, overlay);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
            return;
        }
        _overlays.Remove(window);
        overlay.Dispose();
    }

    public void TurnAllOff()
    {
        foreach (nint window in _overlays.Keys.ToArray())
            TurnOff(window);
    }

    public void DropClosed()
    {
        foreach (nint window in _overlays.Keys.Where(window => !Native.User32.IsWindow(window)).ToArray())
            if (_overlays.Remove(window, out ContentOverlay? overlay))
                overlay.Dispose();
    }

    public void Dispose() => TurnAllOff();
}
