namespace WinScreen.Windows;

internal readonly record struct StackedWindow(nint Handle, Rectangle Bounds, bool Covers, bool CanBeCovered);

internal static class CoverTracker
{
    public static List<nint> FullyCovered(IReadOnlyList<StackedWindow> topFirst)
    {
        var covered = new List<nint>();
        var above = new List<Rectangle>();
        foreach (StackedWindow window in topFirst)
        {
            if (window.Bounds.IsEmpty)
                continue;
            if (window.CanBeCovered && IsHidden(window.Bounds, above))
                covered.Add(window.Handle);
            if (window.Covers)
                above.Add(window.Bounds);
        }

        return covered;
    }

    // Only what is on a screen can show; a window wholly off every screen doesn't count as covered.
    public static bool IsCovered(Rectangle window, IEnumerable<Rectangle> above, IEnumerable<Rectangle> screens)
    {
        var showing = screens.Select(screen => Rectangle.Intersect(screen, window)).Where(part => part.Width > 0 && part.Height > 0).ToList();
        return showing.Count > 0 && IsHidden(showing, above.ToList());
    }

    private static bool IsHidden(Rectangle window, List<Rectangle> above) => IsHidden([window], above);

    private static bool IsHidden(List<Rectangle> showing, List<Rectangle> above)
    {
        foreach (Rectangle cover in above)
        {
            var left = new List<Rectangle>();
            foreach (Rectangle part in showing)
                Subtract(part, cover, left);
            showing = left;
            if (showing.Count == 0)
                return true;
        }

        return false;
    }

    private static void Subtract(Rectangle part, Rectangle cover, List<Rectangle> into)
    {
        Rectangle overlap = Rectangle.Intersect(part, cover);
        if (overlap.IsEmpty)
        {
            into.Add(part);
            return;
        }

        void Keep(int left, int top, int right, int bottom)
        {
            if (right > left && bottom > top)
                into.Add(Rectangle.FromLTRB(left, top, right, bottom));
        }

        Keep(part.Left, part.Top, part.Right, overlap.Top);
        Keep(part.Left, overlap.Bottom, part.Right, part.Bottom);
        Keep(part.Left, overlap.Top, overlap.Left, overlap.Bottom);
        Keep(overlap.Right, overlap.Top, part.Right, overlap.Bottom);
    }
}
