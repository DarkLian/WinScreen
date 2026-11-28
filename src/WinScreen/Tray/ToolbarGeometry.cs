namespace WinScreen.Tray;

public enum ToolbarHide { Away, PressedElsewhere, WindowChanged, Dragged }

public static class ToolbarGeometry
{
    public const int LeaveMilliseconds = 500;

    // The pointer can stray off the bar for a moment, as a hand does; a press elsewhere means it is done with.
    public static ToolbarHide? Leaves(bool nearby, int awayMilliseconds, bool pressed) =>
        nearby ? null : pressed ? ToolbarHide.PressedElsewhere : awayMilliseconds >= LeaveMilliseconds ? ToolbarHide.Away : null;

    public static Point Place(Rectangle window, Size bar, Rectangle area, int reservedRight, int captionHeight)
        => PlaceAt(window, bar, area, reservedRight, captionHeight, Settings.ToolbarPlacement.Centre);

    public static Point PlaceAt(Rectangle window, Size bar, Rectangle area, int reservedRight, int captionHeight,
        Settings.ToolbarPlacement placement)
    {
        int top = Math.Max(window.Top, area.Top);
        int left = placement switch
        {
            Settings.ToolbarPlacement.Left => window.Left + captionHeight,
            Settings.ToolbarPlacement.Right => window.Right - reservedRight - bar.Width,
            _ => window.Left + (window.Width - bar.Width) / 2,
        };
        left = Math.Min(left, window.Right - reservedRight - bar.Width);
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - bar.Width));
        if (left + bar.Width > window.Right - reservedRight)
            top += captionHeight;
        return new Point(left, Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - bar.Height)));
    }

    // Inside the toolbar's width, so the pointer that opened it never starts too far away to keep it open.
    public static Rectangle HoverZone(Rectangle window, Rectangle bar, Rectangle area, int reservedRight,
        int captionHeight)
    {
        int left = Math.Max(window.Left, bar.Left);
        int right = Math.Max(left, Math.Min(window.Right - reservedRight, bar.Right));
        int top = Math.Max(window.Top, area.Top);
        return Rectangle.FromLTRB(left, top, right, top + captionHeight);
    }

    public static Rectangle CaptionButtonsZone(Rectangle window, Rectangle area, int reservedRight, int captionHeight) =>
        new(window.Right - reservedRight, Math.Max(window.Top, area.Top), reservedRight, captionHeight);

    public static int BelowCaption(Rectangle window, int barHeight, Rectangle area, int captionHeight) =>
        Math.Clamp(Math.Max(window.Top, area.Top) + captionHeight, area.Top, Math.Max(area.Top, area.Bottom - barHeight));
}
