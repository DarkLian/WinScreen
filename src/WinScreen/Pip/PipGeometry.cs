using System.Drawing;

namespace WinScreen.Pip;

/// <summary>The arithmetic behind a PiP window, kept apart from the window so it can be tested directly.</summary>
public static class PipGeometry
{
    internal static bool NeedsWholePreview(int pictureWidth, int shownWidth) =>
        pictureWidth > 0 && shownWidth > 0 && pictureWidth / (double)shownWidth < 0.5;

    internal static int SizingHit(Point point, Rectangle bounds, int band)
    {
        if (!bounds.Contains(point)) return 0;
        bool left = point.X < bounds.Left + band, right = point.X >= bounds.Right - band;
        bool top = point.Y < bounds.Top + band, bottom = point.Y >= bounds.Bottom - band;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : 0;
    }

    internal static Rectangle FitPicture(Rectangle area, Rectangle shown)
    {
        if (shown.Width <= 0 || shown.Height <= 0 || area.Width <= 0 || area.Height <= 0) return area;
        double scale = Math.Min(area.Width / (double)shown.Width, area.Height / (double)shown.Height);
        int width = Math.Max(1, (int)Math.Round(shown.Width * scale));
        int height = Math.Max(1, (int)Math.Round(shown.Height * scale));
        return new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    internal static Rectangle SizeWithAspect(Rectangle proposed, int edge, Rectangle shown)
    {
        if (shown.Width <= 0 || shown.Height <= 0) return proposed;
        int width = Math.Max(MinimumWidth, proposed.Width);
        int height = Math.Max(MinimumHeight, proposed.Height);
        if (edge is 3 or 6)
            width = Math.Max(MinimumWidth, (int)Math.Round((height - 2 * Border) * (double)shown.Width / shown.Height) + 2 * Border);
        else
            height = Math.Max(MinimumHeight, (int)Math.Round((width - 2 * Border) * (double)shown.Height / shown.Width) + 2 * Border);
        // A minimum in either direction must preserve the picture's ratio too.
        width = Math.Max(width, (int)Math.Round((height - 2 * Border) * (double)shown.Width / shown.Height) + 2 * Border);
        height = (int)Math.Round((width - 2 * Border) * (double)shown.Height / shown.Width) + 2 * Border;
        int left = edge is 1 or 4 or 7 ? proposed.Right - width : proposed.Left;
        int top = edge is 3 or 4 or 5 ? proposed.Bottom - height : proposed.Top;
        return new Rectangle(left, top, width, height);
    }

    public const int Border = 0;
    public const int MinimumWidth = 160;
    public const int MinimumHeight = 60;

    /// <summary>The size in pixels of the squares the screen is judged in.</summary>
    public const int ContentCell = 16;

    private const double MaximumZoom = 8;

    private const double BusyShare = 0.12;
    private const double ClearGain = 0.08;
    private const double DistanceCost = 0.0001;

    /// <summary>The window height that shows <paramref name="source"/> undistorted at this window width.</summary>
    public static int FitHeight(int windowWidth, Rectangle source)
    {
        int inner = windowWidth - 2 * Border;
        int height = (int)Math.Round(inner * (double)source.Height / source.Width);
        return Math.Max(MinimumHeight, height + 2 * Border);
    }

    /// <summary>The part of the window the view is drawn in, inside the border.</summary>
    public static Rectangle ViewArea(Size clientSize)
    {
        return new Rectangle(Border, Border, clientSize.Width - 2 * Border, clientSize.Height - 2 * Border);
    }

    /// <summary>
    /// Turns a rectangle dragged in the window into the matching part of the source window,
    /// given the part of the source currently shown. The result never leaves that part.
    /// </summary>
    public static Rectangle SelectionToSource(Rectangle selection, Rectangle viewArea, Rectangle shown)
    {
        double scaleX = (double)shown.Width / viewArea.Width;
        double scaleY = (double)shown.Height / viewArea.Height;

        var mapped = Rectangle.FromLTRB(
            shown.Left + (int)Math.Round((selection.Left - viewArea.Left) * scaleX),
            shown.Top + (int)Math.Round((selection.Top - viewArea.Top) * scaleY),
            shown.Left + (int)Math.Round((selection.Right - viewArea.Left) * scaleX),
            shown.Top + (int)Math.Round((selection.Bottom - viewArea.Top) * scaleY));

        return Rectangle.Intersect(mapped, shown);
    }

    /// <summary>The spot in the source window that a point in the view is showing.</summary>
    public static Point PointToSource(Point point, Rectangle viewArea, Rectangle shown)
    {
        if (viewArea.Width <= 0 || viewArea.Height <= 0 || shown.Width <= 0 || shown.Height <= 0)
            throw new ArgumentException("Preview bounds must be nonempty.");
        return new Point(
            Math.Clamp(shown.Left + (int)Math.Round((point.X - viewArea.Left) * (double)shown.Width / viewArea.Width),
                shown.Left, shown.Right - 1),
            Math.Clamp(shown.Top + (int)Math.Round((point.Y - viewArea.Top) * (double)shown.Height / viewArea.Height),
                shown.Top, shown.Bottom - 1));
    }

    public static Size FitWithin(int preferredWidth, Rectangle source, Size available)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return Size.Empty;
        int width = Math.Max(1, Math.Min(preferredWidth - 2 * Border, available.Width - 2 * Border));
        int height = Math.Max(1, available.Height - 2 * Border);
        double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        return new Size(Math.Max(1, (int)Math.Floor(source.Width * scale)) + 2 * Border,
            Math.Max(1, (int)Math.Floor(source.Height * scale)) + 2 * Border);
    }

    /// <summary>
    /// The window size that shows <paramref name="source"/> undistorted with its longer side at
    /// <paramref name="longSide"/>, so tall and wide windows both come out a sensible size. The result
    /// depends only on its inputs, so fitting again never makes the view creep smaller.
    /// </summary>
    public static Size FitLongSide(int longSide, Rectangle source, Size available)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return Size.Empty;
        double scale = Math.Max(1, longSide - 2 * Border) / (double)Math.Max(source.Width, source.Height);
        scale = Math.Min(scale, Math.Min(
            Math.Max(1, available.Width - 2 * Border) / (double)source.Width,
            Math.Max(1, available.Height - 2 * Border) / (double)source.Height));
        return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)) + 2 * Border,
            Math.Max(1, (int)Math.Round(source.Height * scale)) + 2 * Border);
    }

    public static Point KeepInside(Rectangle bounds, Rectangle area) => new(
        Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - bounds.Width)),
        Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height)));

    /// <summary>
    /// Where a resized view goes so the corner nearest the edge of the screen stays put, keeping a view
    /// parked in a corner there instead of drifting away from it.
    /// </summary>
    public static Point KeepCorner(Rectangle old, Size size, Rectangle area)
    {
        bool right = old.Left + old.Width / 2 > area.Left + area.Width / 2;
        bool bottom = old.Top + old.Height / 2 > area.Top + area.Height / 2;
        return new Point(right ? old.Right - size.Width : old.Left, bottom ? old.Bottom - size.Height : old.Top);
    }

    /// <summary>Where a resized view goes so that the same spot of it stays under the pointer.</summary>
    public static Point KeepUnderPointer(Rectangle old, Size size, Point pointer) => new(
        pointer.X - (int)Math.Round((pointer.X - old.Left) * (double)size.Width / old.Width),
        pointer.Y - (int)Math.Round((pointer.Y - old.Top) * (double)size.Height / old.Height));

    /// <summary>
    /// The first spot clear of <paramref name="taken"/>, going up the screen from
    /// <paramref name="wanted"/> and then a column to the left. A full screen falls back to the first.
    /// </summary>
    public static Point FreeSpot(Rectangle wanted, IEnumerable<Rectangle> taken, Rectangle area, int gap)
    {
        Rectangle[] others = taken.ToArray();
        for (int x = wanted.X; x >= area.Left; x -= wanted.Width + gap)
        {
            for (int y = wanted.Y; y >= area.Top; y -= wanted.Height + gap)
            {
                var spot = new Rectangle(new Point(x, y), wanted.Size);
                if (!others.Any(spot.IntersectsWith))
                    return spot.Location;
            }
        }

        return wanted.Location;
    }

    /// <summary>The largest size of this shape that fits inside a limit, never empty.</summary>
    public static Size FitInside(Size shape, Size limit)
    {
        if (shape.Width <= 0 || shape.Height <= 0 || limit.Width <= 0 || limit.Height <= 0)
            return new Size(Math.Max(1, limit.Width), Math.Max(1, limit.Height));
        double scale = Math.Min((double)limit.Width / shape.Width, (double)limit.Height / shape.Height);
        return new Size(Math.Clamp((int)Math.Round(shape.Width * scale), 1, limit.Width),
            Math.Clamp((int)Math.Round(shape.Height * scale), 1, limit.Height));
    }

    /// <summary>
    /// Where a view should go to get off the text and images under it, or null when it's fine where it
    /// is or nowhere is clearly better. <paramref name="busy"/> marks the cells of
    /// <paramref name="area"/> holding content, by row then column. Of equally calm spots the nearest
    /// wins, so the view doesn't cross the screen for nothing.
    /// </summary>
    public static Point? CalmerSpot(bool[,] busy, Rectangle area, Rectangle view)
    {
        int rows = busy.GetLength(0), columns = busy.GetLength(1);
        if (rows == 0 || columns == 0 || view.Width > area.Width || view.Height > area.Height)
            return null;
        int high = Math.Min(rows, (int)Math.Ceiling(view.Height / (double)ContentCell));
        int wide = Math.Min(columns, (int)Math.Ceiling(view.Width / (double)ContentCell));

        var sums = new int[rows + 1, columns + 1];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
                sums[row + 1, column + 1] = (busy[row, column] ? 1 : 0) + sums[row, column + 1] +
                    sums[row + 1, column] - sums[row, column];
        }

        double Share(int row, int column)
        {
            int count = sums[row + high, column + wide] - sums[row, column + wide] - sums[row + high, column] +
                        sums[row, column];
            return count / (double)(high * wide);
        }

        int currentRow = Math.Clamp((view.Top - area.Top) / ContentCell, 0, rows - high);
        int currentColumn = Math.Clamp((view.Left - area.Left) / ContentCell, 0, columns - wide);
        double current = Share(currentRow, currentColumn);
        if (current < BusyShare)
            return null;

        (int Row, int Column) best = (currentRow, currentColumn);
        double bestScore = double.MaxValue, bestShare = current;
        for (int row = 0; row <= rows - high; row++)
        {
            for (int column = 0; column <= columns - wide; column++)
            {
                double share = Share(row, column);
                double distance = Math.Abs(row - currentRow) + Math.Abs(column - currentColumn);
                double score = share + distance * DistanceCost;
                if (score < bestScore)
                    (best, bestScore, bestShare) = ((row, column), score, share);
            }
        }

        if (bestShare > current - ClearGain)
            return null;

        return new Point(
            Math.Min(area.Left + best.Column * ContentCell, area.Right - view.Width),
            Math.Min(area.Top + best.Row * ContentCell, area.Bottom - view.Height));
    }

    /// <summary>All of a view's picture, as a share of its width and height.</summary>
    public static readonly RectangleF WholeView = new(0, 0, 1, 1);

    /// <summary>
    /// A closer or wider look, keeping <paramref name="spot"/>, a share of the visible part, where it is.
    /// Zoom never goes past 8x, and never shows more than the whole picture or anything outside it.
    /// </summary>
    public static RectangleF ZoomAround(RectangleF view, PointF spot, double factor)
    {
        float side = (float)Math.Clamp(view.Width / factor, 1.0 / MaximumZoom, 1);
        float x = view.X + spot.X * (view.Width - side);
        float y = view.Y + spot.Y * (view.Height - side);
        return KeepInPicture(new RectangleF(x, y, side, side));
    }

    /// <summary>Slides a zoomed view by this share of the picture, stopping at its edges.</summary>
    public static RectangleF Pan(RectangleF view, float across, float down) =>
        KeepInPicture(new RectangleF(view.X + across, view.Y + down, view.Width, view.Height));

    /// <summary>The pixels of <paramref name="part"/> that <paramref name="view"/> covers.</summary>
    public static Rectangle ZoomedPart(Rectangle part, RectangleF view)
    {
        if (view == WholeView)
            return part;
        var zoomed = Rectangle.FromLTRB(
            part.Left + (int)Math.Round(view.Left * part.Width),
            part.Top + (int)Math.Round(view.Top * part.Height),
            part.Left + (int)Math.Round(view.Right * part.Width),
            part.Top + (int)Math.Round(view.Bottom * part.Height));
        return zoomed.Width > 0 && zoomed.Height > 0 ? zoomed : part;
    }

    private static RectangleF KeepInPicture(RectangleF view) => new(
        Math.Clamp(view.X, 0, 1 - view.Width), Math.Clamp(view.Y, 0, 1 - view.Height), view.Width, view.Height);

    /// <summary>Where a window of this size goes for a position saved as shares of the free space.</summary>
    public static Point FromShares(double left, double top, Size size, Rectangle area) => new(
        area.Left + (int)Math.Round(left * Math.Max(0, area.Width - size.Width)),
        area.Top + (int)Math.Round(top * Math.Max(0, area.Height - size.Height)));

    /// <summary>A position as shares of the free space, so it survives a change of screen size.</summary>
    public static (double Left, double Top) ToShares(Rectangle bounds, Rectangle area) => (
        area.Width > bounds.Width ? Math.Clamp((bounds.Left - area.Left) / (double)(area.Width - bounds.Width), 0, 1) : 0,
        area.Height > bounds.Height ? Math.Clamp((bounds.Top - area.Top) / (double)(area.Height - bounds.Height), 0, 1) : 0);

    /// <summary>The rectangle between two points, whichever way the mouse was dragged.</summary>
    public static Rectangle Between(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
