namespace WinScreen.Windows;

public enum WindowAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>The arithmetic of placing a window, kept apart from Windows so it can be tested directly.</summary>
public static class PlacementGeometry
{
    /// <summary>Where a frame of this size sits at this anchor of the work area. A frame too big for it starts at the corner.</summary>
    public static Point Align(Size frame, Rectangle area, WindowAnchor anchor)
    {
        int column = (int)anchor % 3, row = (int)anchor / 3;
        return new Point(
            area.Left + Math.Max(0, area.Width - frame.Width) * column / 2,
            area.Top + Math.Max(0, area.Height - frame.Height) * row / 2);
    }

    /// <summary>
    /// The rectangle to hand to Windows so the visible frame lands on <paramref name="target"/>. A window's
    /// real rectangle includes invisible resize margins, which is the gap between <paramref name="outer"/>
    /// and <paramref name="frame"/>.
    /// </summary>
    public static Rectangle OuterFor(Rectangle target, Rectangle frame, Rectangle outer) => Rectangle.FromLTRB(
        target.Left - (frame.Left - outer.Left), target.Top - (frame.Top - outer.Top),
        target.Right + (outer.Right - frame.Right), target.Bottom + (outer.Bottom - frame.Bottom));

    /// <summary>The same place on another screen, as shares of the free space, kept on that screen.</summary>
    public static Point SamePlaceOn(Rectangle frame, Rectangle from, Rectangle to)
    {
        double left = from.Width > frame.Width ? Math.Clamp((frame.Left - from.Left) / (double)(from.Width - frame.Width), 0, 1) : 0;
        double top = from.Height > frame.Height ? Math.Clamp((frame.Top - from.Top) / (double)(from.Height - frame.Height), 0, 1) : 0;
        return new Point(to.Left + (int)Math.Round(left * Math.Max(0, to.Width - frame.Width)),
            to.Top + (int)Math.Round(top * Math.Max(0, to.Height - frame.Height)));
    }

    /// <summary>Which edges a resize grabs for a press at this point: the third of the window it's in, each way.</summary>
    public static ResizeEdges EdgesAt(Point point, Rectangle window)
    {
        ResizeEdges edges = ResizeEdges.None;
        if (point.X < window.Left + window.Width / 3) edges |= ResizeEdges.Left;
        else if (point.X >= window.Right - window.Width / 3) edges |= ResizeEdges.Right;
        if (point.Y < window.Top + window.Height / 3) edges |= ResizeEdges.Top;
        else if (point.Y >= window.Bottom - window.Height / 3) edges |= ResizeEdges.Bottom;
        return edges == ResizeEdges.None ? ResizeEdges.Right | ResizeEdges.Bottom : edges;
    }

    /// <summary>The window after dragging these edges by this much, never smaller than <paramref name="smallest"/>.</summary>
    public static Rectangle Resized(Rectangle window, ResizeEdges edges, Size moved, Size smallest)
    {
        int left = window.Left, top = window.Top, right = window.Right, bottom = window.Bottom;
        if (edges.HasFlag(ResizeEdges.Left)) left = Math.Min(left + moved.Width, right - smallest.Width);
        if (edges.HasFlag(ResizeEdges.Right)) right = Math.Max(right + moved.Width, left + smallest.Width);
        if (edges.HasFlag(ResizeEdges.Top)) top = Math.Min(top + moved.Height, bottom - smallest.Height);
        if (edges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Max(bottom + moved.Height, top + smallest.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>The display after <paramref name="current"/>, wrapping round, or -1 when there's only one.</summary>
    public static int NextDisplay(int current, int count) => count < 2 ? -1 : (Math.Max(0, current) + 1) % count;
}
