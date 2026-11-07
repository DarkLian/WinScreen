using WinScreen.Native;

namespace WinScreen.Anchors;

/// <summary>
/// The anchors on screen, stacked in columns from the right edge until someone moves them. They start
/// below the top of the screen, which is where a maximized window keeps its close button.
/// </summary>
public sealed class AnchorSet : IDisposable
{
    private const int Margin = 16;
    private const int FirstTop = 120;
    private const int Gap = 8;
    private const int AutomaticLimit = 8;

    private readonly Dictionary<nint, AnchorButton> _anchors = [];
    private readonly HashSet<nint> _dismissed = [];
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 500 };

    public AnchorSet()
    {
        _watch.Tick += (_, _) => DropClosedWindows();
    }

    /// <summary>True for an Anchor someone added themselves, which is what commands report and toggle.</summary>
    public bool Has(nint window) => _anchors.TryGetValue(window, out AnchorButton? anchor) && !anchor.Automatic;

    internal IReadOnlyCollection<nint> Automatic => _anchors.Where(pair => pair.Value.Automatic).Select(pair => pair.Key).ToArray();

    public event Action<nint>? Created;
    public event Action<nint>? ShowRequested;

    /// <summary>Adds an anchor for a window, or removes the one it has. Returns the new state.</summary>
    public bool Toggle(nint window)
    {
        if (_anchors.TryGetValue(window, out AnchorButton? existing))
        {
            if (existing.Automatic)
            {
                existing.Automatic = false; // Asking for one keeps it, so uncovering won't remove it.
                return true;
            }

            _anchors.Remove(window);
            existing.Dispose();
            _watch.Enabled = _anchors.Count > 0;
            return false;
        }

        Add(window, automatic: false);
        return true;
    }

    /// <summary>
    /// Makes the automatic Anchors match the covered windows, topmost first. One the person removed
    /// stays away until its window has been uncovered.
    /// </summary>
    internal void SyncAutomatic(IReadOnlyList<nint> covered)
    {
        _dismissed.IntersectWith(covered);
        var wanted = covered.Where(window => !_dismissed.Contains(window) && !HasManualOrDead(window))
            .Take(AutomaticLimit).ToHashSet();
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
            catch (Windows.WindowOperationException) { } // It closed during the pass.
        }

        _watch.Enabled = _anchors.Count > 0;
    }

    private bool HasManualOrDead(nint window) =>
        _anchors.TryGetValue(window, out AnchorButton? anchor) && (!anchor.Automatic || anchor.IsDisposed);

    private void Add(nint window, bool automatic)
    {
        Rectangle area = Screen.FromHandle(window).WorkingArea;
        var anchor = new AnchorButton(window)
        {
            Location = FreePosition(area, Dpi.Of(window)),
            ShowTarget = ShowRequested is null ? null : target => ShowRequested?.Invoke(target),
            Automatic = automatic,
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
        };
        Created?.Invoke(anchor.Handle);
        anchor.Show();
        _watch.Enabled = true;
    }

    public void RemoveAll()
    {
        foreach (AnchorButton anchor in _anchors.Values.ToArray())
            anchor.Dispose();
        _anchors.Clear();
        _watch.Enabled = false;
    }

    public void Dispose()
    {
        RemoveAll();
        _watch.Dispose();
    }

    private Point FreePosition(Rectangle area, int dpi)
    {
        int size = Dpi.Scale(AnchorButton.ButtonSize, dpi);
        int step = size + Dpi.Scale(Gap, dpi);
        for (int left = area.Right - size - Dpi.Scale(Margin, dpi); left >= area.Left; left -= step)
        for (int top = area.Top + Dpi.Scale(FirstTop, dpi); top + size <= area.Bottom; top += step)
        {
            var candidate = new Rectangle(left, top, size, size);
            if (!_anchors.Values.Any(anchor => anchor.Bounds.IntersectsWith(candidate))) return candidate.Location;
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
    }
}
