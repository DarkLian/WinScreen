using WinScreen.Native;

namespace WinScreen.Anchors;

/// <summary>The anchors on screen, stacked down the right edge until someone moves them.</summary>
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
            Location = FreePosition(area),
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

    private Point FreePosition(Rectangle area)
    {
        for (int left = area.Right - AnchorButton.ButtonSize - Margin; left >= area.Left; left -= AnchorButton.ButtonSize + Gap)
            for (int top = area.Top + Margin; top + AnchorButton.ButtonSize <= area.Bottom; top += AnchorButton.ButtonSize + Gap)
            {
                var candidate = new Rectangle(left, top, AnchorButton.ButtonSize, AnchorButton.ButtonSize);
                if (!_anchors.Values.Any(anchor => anchor.Bounds.IntersectsWith(candidate))) return candidate.Location;
            }
        return new Point(Math.Max(area.Left, area.Right - AnchorButton.ButtonSize), Math.Max(area.Top, area.Bottom - AnchorButton.ButtonSize));
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
