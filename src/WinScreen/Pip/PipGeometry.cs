using System.Drawing;

namespace WinScreen.Pip;

public static class PipGeometry
{
    internal static bool NeedsWholePreview(int pictureWidth, int shownWidth) =>
        pictureWidth > 0 && shownWidth > 0 && pictureWidth / (double)shownWidth < 0.5;

    // A view used directly keeps thin edges, so the app's own controls near them (a browser's tabs) can still be
    // clicked.
    internal const int EdgeBand = 6, DirectEdgeBand = 2, DirectCornerBand = 8;

    internal static int SizingHit(Point point, Rectangle bounds, int band, int corner = 0)
    {
        if (!bounds.Contains(point)) return 0;
        corner = Math.Max(corner, band);
        bool nearLeft = point.X < bounds.Left + corner, nearRight = point.X >= bounds.Right - corner;
        bool nearTop = point.Y < bounds.Top + corner, nearBottom = point.Y >= bounds.Bottom - corner;
        if (nearTop && nearLeft) return 13;
        if (nearTop && nearRight) return 14;
        if (nearBottom && nearLeft) return 16;
        if (nearBottom && nearRight) return 17;
        if (point.Y < bounds.Top + band) return 12;
        if (point.Y >= bounds.Bottom - band) return 15;
        if (point.X < bounds.Left + band) return 10;
        return point.X >= bounds.Right - band ? 11 : 0;
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

    public const int ContentCell = 16;

    private const double MaximumZoom = 8;

    private const double BusyShare = 0.12;
    private const double ClearGain = 0.08;
    private const double DistanceCost = 0.0001;

    public static int FitHeight(int windowWidth, Rectangle source)
    {
        int inner = windowWidth - 2 * Border;
        int height = (int)Math.Round(inner * (double)source.Height / source.Width);
        return Math.Max(MinimumHeight, height + 2 * Border);
    }

    public static Rectangle ViewArea(Size clientSize)
    {
        return new Rectangle(Border, Border, clientSize.Width - 2 * Border, clientSize.Height - 2 * Border);
    }

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

    // Depends only on its inputs, so fitting again never makes the view creep smaller.
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

    public static Point KeepCorner(Rectangle old, Size size, Rectangle area)
    {
        bool right = old.Left + old.Width / 2 > area.Left + area.Width / 2;
        bool bottom = old.Top + old.Height / 2 > area.Top + area.Height / 2;
        return new Point(right ? old.Right - size.Width : old.Left, bottom ? old.Bottom - size.Height : old.Top);
    }

    public static Point KeepUnderPointer(Rectangle old, Size size, Point pointer) => new(
        pointer.X - (int)Math.Round((pointer.X - old.Left) * (double)size.Width / old.Width),
        pointer.Y - (int)Math.Round((pointer.Y - old.Top) * (double)size.Height / old.Height));

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

    public static Size FitInside(Size shape, Size limit)
    {
        if (shape.Width <= 0 || shape.Height <= 0 || limit.Width <= 0 || limit.Height <= 0)
            return new Size(Math.Max(1, limit.Width), Math.Max(1, limit.Height));
        double scale = Math.Min((double)limit.Width / shape.Width, (double)limit.Height / shape.Height);
        return new Size(Math.Clamp((int)Math.Round(shape.Width * scale), 1, limit.Width),
            Math.Clamp((int)Math.Round(shape.Height * scale), 1, limit.Height));
    }

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

    public static readonly RectangleF WholeView = new(0, 0, 1, 1);

    public static RectangleF ZoomAround(RectangleF view, PointF spot, double factor)
    {
        float side = (float)Math.Clamp(view.Width / factor, 1.0 / MaximumZoom, 1);
        float x = view.X + spot.X * (view.Width - side);
        float y = view.Y + spot.Y * (view.Height - side);
        return KeepInPicture(new RectangleF(x, y, side, side));
    }

    public static RectangleF Pan(RectangleF view, float across, float down) =>
        KeepInPicture(new RectangleF(view.X + across, view.Y + down, view.Width, view.Height));

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

    public static Point FromShares(double left, double top, Size size, Rectangle area) => new(
        area.Left + (int)Math.Round(left * Math.Max(0, area.Width - size.Width)),
        area.Top + (int)Math.Round(top * Math.Max(0, area.Height - size.Height)));

    public static (double Left, double Top) ToShares(Rectangle bounds, Rectangle area) => (
        area.Width > bounds.Width ? Math.Clamp((bounds.Left - area.Left) / (double)(area.Width - bounds.Width), 0, 1) : 0,
        area.Height > bounds.Height ? Math.Clamp((bounds.Top - area.Top) / (double)(area.Height - bounds.Height), 0, 1) : 0);

    public static Rectangle Between(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
