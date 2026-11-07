namespace WinScreen.Windows;

/// <summary>One window in the stack. <paramref name="Covers"/> is false for anything you can see through.</summary>
internal readonly record struct StackedWindow(nint Handle, Rectangle Bounds, bool Covers, bool CanBeCovered);

/// <summary>Works out which windows are out of sight. Pure, so it can be tested with plain rectangles.</summary>
internal static class CoverTracker
{
    /// <summary>The windows entirely hidden by those above them, topmost first. Several windows can share the job.</summary>
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

    private static bool IsHidden(Rectangle window, List<Rectangle> above)
    {
        var showing = new List<Rectangle> { window };
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
