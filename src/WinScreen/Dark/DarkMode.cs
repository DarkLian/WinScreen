namespace WinScreen.Dark;

/// <summary>Keeps one overlay per darkened window, and keeps them over their windows.</summary>
public sealed class DarkMode : IDisposable
{
    private readonly Dictionary<nint, DarkOverlay> _overlays = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly HashSet<nint> _ignored = [];

    public DarkMode()
    {
        _timer.Tick += (_, _) => FollowAll();
    }

    public bool IsDark(nint window) => _overlays.ContainsKey(window);

    /// <summary>Turns dark mode on or off for a window. Returns the new state.</summary>
    public bool Toggle(nint window)
    {
        if (_overlays.Remove(window, out DarkOverlay? existing))
        {
            existing.Dispose();
            UpdateFilters();
            _timer.Enabled = _overlays.Count > 0;
            return false;
        }

        Add(window).Follow();
        _timer.Enabled = true;
        return true;
    }

    private DarkOverlay Add(nint window)
    {
        var overlay = new DarkOverlay(window, DarkOverlay.IsPinned(window));
        _overlays[window] = overlay;
        _ = overlay.Handle;
        UpdateFilters();
        return overlay;
    }

    /// <summary>Keeps a WinScreen window such as the toolbar out of the filtered picture.</summary>
    public void Ignore(nint window)
    {
        _ignored.Add(window);
        UpdateFilters();
    }

    public void TurnAllOff()
    {
        foreach (DarkOverlay overlay in _overlays.Values)
            overlay.Dispose();
        _overlays.Clear();
        _timer.Enabled = false;
    }

    public void Dispose()
    {
        TurnAllOff();
        _timer.Dispose();
    }

    private void FollowAll()
    {
        foreach ((nint window, DarkOverlay current) in _overlays.ToArray())
        {
            DarkOverlay overlay = current;
            if (!overlay.TargetAlive)
            {
                _overlays.Remove(window);
                overlay.Dispose();
                UpdateFilters();
                continue;
            }

            if (Native.User32.IsWindow(window) && !overlay.MatchesTarget)
            {
                _overlays.Remove(window);
                overlay.Dispose();
                try
                {
                    overlay = Add(window);
                }
                catch (Windows.WindowOperationException)
                {
                    UpdateFilters(); // The window went away mid-swap, so there is nothing left to cover.
                    continue;
                }
            }

            if (!overlay.Follow())
            {
                _overlays.Remove(window);
                overlay.Dispose();
                UpdateFilters();
            }
        }

        _timer.Enabled = _overlays.Count > 0;
    }

    private void UpdateFilters()
    {
        _ignored.RemoveWhere(window =>
        {
            Native.User32.GetWindowThreadProcessId(window, out uint pid);
            return !Native.User32.IsWindow(window) || pid != Environment.ProcessId;
        });
        nint[] windows = [.. _overlays.Values.Select(o => o.Handle), .. _ignored];
        foreach (DarkOverlay overlay in _overlays.Values)
            overlay.SetIgnoredWindows(windows);
    }
}
