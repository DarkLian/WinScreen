using WinScreen.Windows;

namespace WinScreen.Glass;

/// <summary>
/// The processed effects on windows: layered Glass and picture-safe Dark. Each keeps its window invisible
/// but clickable under an overlay, and puts it back exactly when turned off, on exit, or after a crash
/// through the same recovery as hidden windows.
/// </summary>
internal sealed class ContentEffects(WindowController windows) : IDisposable
{
    private readonly Dictionary<nint, ContentOverlay> _overlays = [];
    private ContentLevels _levels = new(0.15f, 1, 0.9f);

    /// <summary>Raised when an effect had to stop, with a message for the person.</summary>
    public event Action<string>? Problem;

    /// <summary>Raised with each new overlay, so other effects can leave it alone.</summary>
    public event Action<nint>? Created;

    public bool Has(nint window, ContentLook look) => _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.Look == look;

    public bool Has(nint window) => _overlays.ContainsKey(window);

    public int Count => _overlays.Count;

    public void SetLevels(ContentLevels levels)
    {
        _levels = levels;
        foreach (ContentOverlay overlay in _overlays.Values)
            overlay.SetLevels(levels);
    }

    /// <summary>Turns this look on for a window, or off when it already has it. Returns the new state.</summary>
    public bool Toggle(nint window, ContentLook look)
    {
        if (_overlays.TryGetValue(window, out ContentOverlay? existing))
        {
            TurnOff(window);
            if (existing.Look == look)
                return false;
        }

        var overlay = new ContentOverlay(window, look, _levels);
        windows.ConcealSolid(window);
        try
        {
            overlay.Start();
        }
        catch
        {
            overlay.Dispose();
            windows.Show(window);
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
        if (!_overlays.Remove(window, out ContentOverlay? overlay))
            return;
        overlay.Dispose();
        try
        {
            if (Native.User32.IsWindow(window))
                windows.Show(window);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    public void TurnAllOff()
    {
        foreach (nint window in _overlays.Keys.ToArray())
            TurnOff(window);
    }

    /// <summary>Drops effects whose window has closed.</summary>
    public void DropClosed()
    {
        foreach (nint window in _overlays.Keys.Where(window => !Native.User32.IsWindow(window)).ToArray())
            if (_overlays.Remove(window, out ContentOverlay? overlay))
                overlay.Dispose();
    }

    public void Dispose() => TurnAllOff();
}
