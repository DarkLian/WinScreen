namespace WinScreen.Dark;

public sealed class DarkMode : IDisposable
{
    private readonly Dictionary<nint, DarkOverlay> _overlays = [];
    private const int FollowInterval = 33;
    private const int SlowestFollowInterval = 300;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = FollowInterval };
    private readonly HashSet<nint> _ignored = [];
    public bool DetectAlreadyDark { get; set; }
    private Func<nint, Glass.ContentLevels> _levelsOf = _ => new(0.15f, 1);

    // Called again after levels change, for every window with Dark.
    internal void SetLevels(Func<nint, Glass.ContentLevels> levelsOf)
    {
        _levelsOf = levelsOf;
        foreach ((nint window, DarkOverlay overlay) in _overlays) Level(window, overlay);
    }

    private void Level(nint window, DarkOverlay overlay)
    {
        Glass.ContentLevels levels = _levelsOf(window);
        overlay.SetLevels(levels.DarkPage, levels.DarkText);
    }
    public event Action<string>? Notice;

    public DarkMode()
    {
        _timer.Tick += (_, _) => FollowAll();
    }

    public bool IsDark(nint window) => _overlays.ContainsKey(window);

    internal IEnumerable<nint> Targets => _overlays.Keys;

    internal nint OverlayOf(nint window) =>
        _overlays.TryGetValue(window, out DarkOverlay? overlay) && overlay.IsHandleCreated ? overlay.Handle : 0;

    public bool Toggle(nint window)
    {
        if (_overlays.Remove(window, out DarkOverlay? existing))
        {
            existing.Dispose();
            UpdateFilters();
            _timer.Enabled = _overlays.Count > 0;
            return false;
        }

        if (RefusesAsAlreadyDark(window))
            return false;
        Add(window).Follow();
        _timer.Enabled = true;
        return true;
    }

    public bool RefusesAsAlreadyDark(nint window)
    {
        if (!LooksDark(window))
            return false;
        Notice?.Invoke(Localization.UiText.T("dark.alreadyDark"));
        return true;
    }

    public bool LooksDark(nint window) =>
        DetectAlreadyDark && Windows.WindowInfo.TryGetVisibleBounds(window, out Rectangle bounds) &&
        Windows.ScreenColors.Brightness(Windows.ScreenColors.MostCommon(bounds)) < .35;

    private DarkOverlay Add(nint window)
    {
        var overlay = new DarkOverlay(window, DarkOverlay.IsPinned(window));
        _overlays[window] = overlay;
        Level(window, overlay);
        _ = overlay.Handle;
        UpdateFilters();
        return overlay;
    }

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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
                    UpdateFilters();
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

        // Where redrawing is slow (no graphics card, for example), an update can take longer than the
        // interval, and the next one would always be waiting. Leave room for the redraw and everything else.
        double took = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _timer.Interval = Math.Clamp((int)(took * 3), FollowInterval, SlowestFollowInterval);
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
