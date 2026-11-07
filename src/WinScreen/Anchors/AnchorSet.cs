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

        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var anchor = new AnchorButton(window)
        {
            Location = new Point(area.Right - AnchorButton.ButtonSize - Margin, FreeTop()),
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

    private int FreeTop()
    {
        int top = FirstTop;
        while (_anchors.Values.Any(a => Math.Abs(a.Top - top) < AnchorButton.ButtonSize))
            top += AnchorButton.ButtonSize + Gap;
        return top;
    }

    private void DropClosedWindows()
    {
        foreach ((nint window, AnchorButton anchor) in _anchors.ToArray())
        {
            if (!User32.IsWindow(window))
            {
                _anchors.Remove(window);
                anchor.Dispose();
            }
        }

        _watch.Enabled = _anchors.Count > 0;
    }
}
