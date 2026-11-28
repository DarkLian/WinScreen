using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;

namespace WinScreen.Anchors;

// Columns start below the top of the screen, where a maximized window keeps its close button.
public sealed class AnchorSet : IDisposable
{
    private const int Margin = 16;
    private const int FirstTop = 120;
    private const int Gap = 8;
    private const int PreviewDelay = 600;

    private readonly Dictionary<nint, AnchorButton> _anchors = [];
    private readonly HashSet<nint> _dismissed = [];
    private readonly HashSet<nint> _offTaskbar = [];
    private readonly HashSet<nint> _keepsTaskbar = [];  // Refused to give up their taskbar button once; not asked again.
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 500 };
    private AnchorPreferences _preferences = new();

    public AnchorSet()
    {
        _watch.Tick += (_, _) =>
        {
            DropClosedWindows();
            Restyle();
            FollowDesktops();
        };
    }

    public bool Has(nint window) => _anchors.TryGetValue(window, out AnchorButton? anchor) && !anchor.Automatic;

    internal IReadOnlyCollection<nint> Automatic => _anchors.Where(pair => pair.Value.Automatic).Select(pair => pair.Key).ToArray();

    internal Func<nint, bool>? HasView { get; set; }

    public event Action<nint>? Created;
    public event Action<nint>? ShowRequested;

    public bool Hidden
    {
        get => _hidden;
        set
        {
            if (_hidden == value) return;
            _hidden = value;
            foreach ((nint window, AnchorButton anchor) in _anchors.Where(pair => !pair.Value.IsDisposed))
                anchor.Visible = ShowsNow(window);
            SyncTaskbar();  // No taskbar button and no Anchor in sight would leave the window hard to find.
        }
    }

    internal Action<nint, bool>? SetOffTaskbar { get; set; }

    // Asked each time: a view closing, for one, may have put the button back.
    internal Func<nint, bool>? IsOffTaskbar { get; set; }

    private void SyncTaskbar()
    {
        if (SetOffTaskbar is not { } set) return;
        HashSet<nint> wanted = _preferences.OffTaskbar && !_hidden
            ? [.. _anchors.Where(pair => !pair.Value.Automatic && !pair.Value.IsDisposed && pair.Value.TargetAlive).Select(pair => pair.Key)]
            : [];
        foreach (nint window in _offTaskbar.Except(wanted).ToArray())
        {
            _offTaskbar.Remove(window);
            try { set(window, false); }
            catch (Windows.WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
        }
        if (IsOffTaskbar is { } isOff) _offTaskbar.RemoveWhere(window => wanted.Contains(window) && !isOff(window));
        _keepsTaskbar.IntersectWith(wanted);
        foreach (nint window in wanted.Except(_offTaskbar).Except(_keepsTaskbar).ToArray())
        {
            try
            {
                set(window, true);
                _offTaskbar.Add(window);
            }
            catch (Windows.WindowOperationException) { _keepsTaskbar.Add(window); }
        }
    }

    private bool _hidden;

    internal void Configure(AnchorPreferences preferences)
    {
        _preferences = preferences.Validated();
        Restyle();
        SyncTaskbar();
    }

    public bool Toggle(nint window)
    {
        if (_anchors.TryGetValue(window, out AnchorButton? existing))
        {
            if (existing.Automatic)
            {
                existing.Automatic = false;
                Restyle();
                SyncTaskbar();
                return true;
            }

            _anchors.Remove(window);
            existing.Dispose();
            _watch.Enabled = _anchors.Count > 0;
            Restyle();
            SyncTaskbar();
            return false;
        }

        Add(window, automatic: false);
        Restyle();
        SyncTaskbar();
        return true;
    }

    internal void SyncAutomatic(IReadOnlyList<nint> covered)
    {
        _dismissed.IntersectWith(covered);
        List<nint> wanted = covered.Where(window => !_dismissed.Contains(window) && !HasManualOrDead(window))
            .Take(_preferences.MaxAutomatic).ToList();
        foreach ((nint window, AnchorButton anchor) in _anchors.ToArray())
        {
            if (anchor.Automatic && !wanted.Contains(window))
            {
                _anchors.Remove(window);
                anchor.Dispose();
            }
        }

        foreach (nint window in wanted.Where(window => !_anchors.ContainsKey(window)))
        {
            try { Add(window, automatic: true); }
            catch (Windows.WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
        }

        _recency = wanted;
        Restyle();
        _watch.Enabled = _anchors.Count > 0;
    }

    private List<nint> _recency = [];

    private bool HasManualOrDead(nint window) =>
        _anchors.TryGetValue(window, out AnchorButton? anchor) && (!anchor.Automatic || anchor.IsDisposed);

    private void Add(nint window, bool automatic)
    {
        Rectangle area = Screen.FromHandle(window).WorkingArea;
        var anchor = new AnchorButton(window)
        {
            Location = StartPosition(window, area, Dpi.Of(window)),
            ShowTarget = ShowRequested is null ? null : target => ShowRequested?.Invoke(target),
            Automatic = automatic,
            PreviewDelay = _preferences.HoverPreview ? PreviewDelay : 0,
        };
        _anchors[window] = anchor;
        anchor.FormClosed += (_, _) =>
        {
            if (_anchors.TryGetValue(window, out var current) && ReferenceEquals(current, anchor))
            {
                _anchors.Remove(window);
                if (anchor.Automatic) _dismissed.Add(window);
            }

            _watch.Enabled = _anchors.Count > 0;
            if (!_disposing) Restyle();
            SyncTaskbar();
        };
        Created?.Invoke(anchor.Handle);
        Style(anchor);
        if (ShowsNow(anchor.Target)) anchor.Show();
        _watch.Enabled = true;
    }

    private bool ShowsNow(nint window) =>
        !_hidden && (_preferences.EveryDesktop || Native.VirtualDesktops.OnCurrentDesktop(window));

    private void FollowDesktops()
    {
        if (_hidden || _preferences.EveryDesktop) return;
        foreach ((nint window, AnchorButton anchor) in _anchors.ToArray())
        {
            if (anchor.IsDisposed) continue;
            bool show = ShowsNow(window);
            if (anchor.Visible != show) anchor.Visible = show;
        }
    }

    private void Restyle()
    {
        foreach (AnchorButton anchor in _anchors.Values)
            Style(anchor);
    }

    private string? TitleOf(AnchorButton anchor)
    {
        if (anchor.Program is not { } program || !_anchors.Values.Any(other => !ReferenceEquals(other, anchor) &&
                !other.IsDisposed && string.Equals(other.Program, program, StringComparison.OrdinalIgnoreCase)))
            return null;
        string title = Windows.WindowInfo.Title(anchor.Target).Trim();
        return title.Length == 0 ? null : AnchorTitles.Short(title, Path.GetFileNameWithoutExtension(program), Describe(program));
    }

    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    private string Describe(string program)
    {
        if (_descriptions.TryGetValue(program, out string? known)) return known;
        string name = "";
        try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(program).FileDescription ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(e); }
        return _descriptions[program] = name;
    }

    private void Style(AnchorButton anchor)
    {
        anchor.PreviewDelay = _preferences.HoverPreview ? PreviewDelay : 0;
        int rank = anchor.Automatic ? _recency.IndexOf(anchor.Target) : -1;
        (double size, double opacity) = rank >= 0 && _preferences.RecencyEmphasis
            ? AnchorEmphasis.For(rank, _recency.Count)
            : (1.0, 1.0);
        // The anchor's own screen: it can be moved to another one than its window's.
        int full = Dpi.Scale(_preferences.Size, Dpi.Of(anchor.Handle));
        anchor.Apply((int)Math.Round(full * size), opacity, _preferences.Style,
            HasView?.Invoke(anchor.Target) == true, _preferences.AvoidContent, TitleOf(anchor));
    }

    // Anchors are left out of screen capture while this is on, so the check sees what's under them.
    internal void AvoidContent(Func<Rectangle, bool[,]> busyCells)
    {
        foreach (AnchorButton anchor in _anchors.Values.Where(anchor => anchor.Automatic && !anchor.Placed).ToArray())
        {
            Rectangle area = Screen.FromRectangle(anchor.Bounds).WorkingArea;
            if (PipGeometry.CalmerSpot(busyCells(area), area, anchor.Bounds) is not { } spot)
                continue;
            var moved = new Rectangle(spot, anchor.Size);
            if (!_anchors.Values.Any(other => !ReferenceEquals(other, anchor) && other.Bounds.IntersectsWith(moved)))
                anchor.Location = spot;
        }
    }

    public void RemoveAll()
    {
        foreach (AnchorButton anchor in _anchors.Values.ToArray())
            anchor.Dispose();
        _anchors.Clear();
        _watch.Enabled = false;
        SyncTaskbar();
    }

    private bool _disposing;

    public void Dispose()
    {
        _disposing = true;
        RemoveAll();
        _watch.Dispose();
    }

    private Point StartPosition(nint window, Rectangle area, int dpi)
    {
        if (_preferences.Side == AnchorSide.NearWindow && NearWindow(window, area, dpi) is { } near)
            return near;
        return FreePosition(area, dpi);
    }

    private Point? NearWindow(nint window, Rectangle area, int dpi)
    {
        if (!User32.GetWindowRect(window, out Rect bounds))
            return null;
        int size = Dpi.Scale(_preferences.Size, dpi), gap = Dpi.Scale(Gap, dpi);
        return AnchorGeometry.NearWindow(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), size, gap, area,
            candidate => !_anchors.Values.Any(anchor => anchor.Bounds.IntersectsWith(candidate)));
    }

    private Point FreePosition(Rectangle area, int dpi)
    {
        int size = Dpi.Scale(_preferences.Size, dpi);
        int step = size + Dpi.Scale(Gap, dpi);
        int margin = Dpi.Scale(Margin, dpi);
        bool leftSide = _preferences.Side == AnchorSide.Left;
        int columns = Math.Max(1, (area.Width - margin) / step);
        for (int column = 0; column < columns; column++)
        {
            int left = leftSide ? area.Left + margin + column * step : area.Right - size - margin - column * step;
            for (int top = area.Top + Dpi.Scale(FirstTop, dpi); top + size <= area.Bottom; top += step)
            {
                var candidate = new Rectangle(left, top, size, size);
                if (!_anchors.Values.Any(anchor => anchor.Bounds.IntersectsWith(candidate))) return candidate.Location;
            }
        }

        return new Point(Math.Max(area.Left, area.Right - size), Math.Max(area.Top, area.Bottom - size));
    }

    private void DropClosedWindows()
    {
        foreach ((nint window, AnchorButton anchor) in _anchors.ToArray())
        {
            if (anchor.IsDisposed || !anchor.TargetAlive)
            {
                _anchors.Remove(window);
                anchor.Dispose();
            }
        }

        _watch.Enabled = _anchors.Count > 0;
        _offTaskbar.RemoveWhere(window => !_anchors.ContainsKey(window) && !User32.IsWindow(window));
        SyncTaskbar();
    }
}

internal static class AnchorTitles
{
    public static string Short(string title, params string[] program)
    {
        foreach (string separator in new[] { " - ", " \u2014 ", " \u2013 ", " | " })
        {
            int at = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && LooksLikeProgram(title[(at + separator.Length)..], program))
                return title[..at].Trim();
        }
        return title;
    }

    private static bool LooksLikeProgram(string tail, string[] names)
    {
        static string Plain(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        string a = Plain(tail);
        return a.Length > 0 && names.Select(Plain).Any(b => b.Length > 0 && (a.Contains(b) || b.Contains(a)));
    }
}

internal static class AnchorGeometry
{
    public static Point? NearWindow(Rectangle window, int size, int gap, Rectangle area, Func<Rectangle, bool> isFree)
    {
        int left = window.Right + gap;
        if (left + size > area.Right)
            left = Math.Max(area.Left, window.Right - size - gap);
        for (int top = Math.Max(window.Top, area.Top); top + size <= area.Bottom; top += size + gap)
        {
            var candidate = new Rectangle(left, top, size, size);
            if (isFree(candidate))
                return candidate.Location;
        }

        return null;
    }
}

internal static class AnchorEmphasis
{
    private const double SmallestSize = 0.7;
    private const double FaintestOpacity = 0.6;

    public static (double Size, double Opacity) For(int rank, int count)
    {
        if (count <= 1) return (1, 1);
        double age = (double)Math.Clamp(rank, 0, count - 1) / (count - 1);
        return (1 - age * (1 - SmallestSize), 1 - age * (1 - FaintestOpacity));
    }
}
