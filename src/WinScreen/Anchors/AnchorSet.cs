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

    private readonly Dictionary<nint, AnchorButton> _anchors = [];
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 500 };

    public AnchorSet()
    {
        _watch.Tick += (_, _) => DropClosedWindows();
    }

    public bool Has(nint window) => _anchors.ContainsKey(window);

    /// <summary>Adds an anchor for a window, or removes the one it has. Returns the new state.</summary>
    public bool Toggle(nint window)
    {
        if (_anchors.Remove(window, out AnchorButton? existing))
        {
            existing.Dispose();
            _watch.Enabled = _anchors.Count > 0;
            return false;
        }

        Rectangle area = Screen.FromHandle(window).WorkingArea;
        var anchor = new AnchorButton(window)
        {
            Location = FreePosition(area, Dpi.Of(window)),
        };
        _anchors[window] = anchor;
        anchor.FormClosed += (_, _) =>
        {
            if (_anchors.TryGetValue(window, out var current) && ReferenceEquals(current, anchor))
                _anchors.Remove(window);
            _watch.Enabled = _anchors.Count > 0;
        };
        anchor.Show();
        _watch.Enabled = true;
        return true;
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
