using System.Drawing;

namespace WinScreen.Pip;

/// <summary>The arithmetic behind a PiP window, kept apart from the window so it can be tested directly.</summary>
public static class PipGeometry
{
    public const int Border = 2;
    public const int MinimumWidth = 160;
    public const int MinimumHeight = 60;

    /// <summary>The size in pixels of the squares the screen is judged in.</summary>
    public const int ContentCell = 16;

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

    public static Point KeepInside(Rectangle bounds, Rectangle area) => new(
        Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - bounds.Width)),
        Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height)));

    /// <summary>
    /// Where a source window goes so that <paramref name="spot"/> sits under the cursor, kept on
    /// screen. Near a screen edge the spot can't reach the cursor, so the cursor moves to it.
    /// </summary>
    public static Point PeekPosition(Point cursor, Point spot, Size sourceSize, Rectangle area)
    {
        return new Point(
            Math.Clamp(cursor.X - spot.X, area.Left, Math.Max(area.Left, area.Right - sourceSize.Width)),
            Math.Clamp(cursor.Y - spot.Y, area.Top, Math.Max(area.Top, area.Bottom - sourceSize.Height)));
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

    /// <summary>The rectangle between two points, whichever way the mouse was dragged.</summary>
    public static Rectangle Between(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
