namespace WinScreen.Windows;

public enum WindowAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public enum SnapZone { None, Maximize, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>The arithmetic of placing a window, kept apart from Windows so it can be tested directly.</summary>
public static class PlacementGeometry
{
    internal static Rectangle FrameOnDisplay(Rectangle original, Rectangle from, Rectangle to, Size destinationSize)
    {
        var size = new Size(Math.Min(destinationSize.Width, to.Width), Math.Min(destinationSize.Height, to.Height));
        double left = from.Width > original.Width ? Math.Clamp((original.Left - from.Left) / (double)(from.Width - original.Width), 0, 1) : 0;
        double top = from.Height > original.Height ? Math.Clamp((original.Top - from.Top) / (double)(from.Height - original.Height), 0, 1) : 0;
        return new Rectangle(to.Left + (int)Math.Round(left * (to.Width - size.Width)),
            to.Top + (int)Math.Round(top * (to.Height - size.Height)), size.Width, size.Height);
    }

    /// <summary>
    /// A frame given a new size: its top-left corner stays, unless that would put part of it off the work area, in
    /// which case it moves back in; a size bigger than the work area is cut to it.
    /// </summary>
    public static Rectangle Resized(Rectangle frame, Size size, Rectangle area)
    {
        var fitted = new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
        int left = Math.Clamp(frame.Left, area.Left, area.Right - fitted.Width);
        int top = Math.Clamp(frame.Top, area.Top, area.Bottom - fitted.Height);
        return new Rectangle(left, top, fitted.Width, fitted.Height);
    }

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

    /// <summary>
    /// Which snap a dragged window gets with the pointer here. Only an edge with no other display past it
    /// counts, so the pointer can still cross to a neighbouring display. Near a side edge's ends it's a corner;
    /// the bottom edge doesn't snap, as in Windows.
    /// </summary>
    public static SnapZone SnapZoneAt(Point pointer, Rectangle display, bool leftOpen, bool topOpen, bool rightOpen)
    {
        const int Reach = 1;
        int corner = Math.Max(40, display.Height / 8);
        bool left = leftOpen && pointer.X <= display.Left + Reach;
        bool right = rightOpen && pointer.X >= display.Right - 1 - Reach;
        bool top = topOpen && pointer.Y <= display.Top + Reach;
        bool upper = pointer.Y < display.Top + corner, lower = pointer.Y >= display.Bottom - corner;
        if (left) return upper ? SnapZone.TopLeft : lower ? SnapZone.BottomLeft : SnapZone.Left;
        if (right) return upper ? SnapZone.TopRight : lower ? SnapZone.BottomRight : SnapZone.Right;
        if (top) return SnapZone.Maximize;
        return SnapZone.None;
    }

    /// <summary>The visible frame a snap gives, inside a display's work area. Maximize fills it.</summary>
    public static Rectangle SnapFrame(SnapZone zone, Rectangle area)
    {
        int halfWidth = area.Width / 2, halfHeight = area.Height / 2;
        return zone switch
        {
            SnapZone.Left => new Rectangle(area.Left, area.Top, halfWidth, area.Height),
            SnapZone.Right => Rectangle.FromLTRB(area.Left + halfWidth, area.Top, area.Right, area.Bottom),
            SnapZone.TopLeft => new Rectangle(area.Left, area.Top, halfWidth, halfHeight),
            SnapZone.TopRight => Rectangle.FromLTRB(area.Left + halfWidth, area.Top, area.Right, area.Top + halfHeight),
            SnapZone.BottomLeft => Rectangle.FromLTRB(area.Left, area.Top + halfHeight, area.Left + halfWidth, area.Bottom),
            SnapZone.BottomRight => Rectangle.FromLTRB(area.Left + halfWidth, area.Top + halfHeight, area.Right, area.Bottom),
            _ => area,
        };
    }

    /// <summary>
    /// Where a window that's coming out of maximized or snapped goes, so the point that was grabbed stays
    /// under the pointer: the same share across and down the smaller window as it was of the bigger one.
    /// </summary>
    public static Rectangle RestoredUnder(Point pointer, Point grabbed, Rectangle before, Size restored)
    {
        double across = before.Width > 0 ? Math.Clamp((grabbed.X - before.Left) / (double)before.Width, 0, 1) : 0.5;
        double down = before.Height > 0 ? Math.Clamp((grabbed.Y - before.Top) / (double)before.Height, 0, 1) : 0;
        return new Rectangle(pointer.X - (int)Math.Round(across * restored.Width),
            pointer.Y - (int)Math.Round(down * restored.Height), restored.Width, restored.Height);
    }

    /// <summary>The display after <paramref name="current"/>, wrapping round, or -1 when there's only one.</summary>
    public static int NextDisplay(int current, int count) => count < 2 ? -1 : (Math.Max(0, current) + 1) % count;
}
