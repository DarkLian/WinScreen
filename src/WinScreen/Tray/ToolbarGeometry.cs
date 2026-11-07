namespace WinScreen.Tray;

/// <summary>Where the toolbar goes, kept apart from the window so it can be tested directly.</summary>
public static class ToolbarGeometry
{
    /// <summary>
    /// Centred on the title bar, kept on screen and clear of the window's own buttons. When a narrow
    /// window leaves no room for that, the bar drops just below the title bar.
    /// </summary>
    public static Point Place(Rectangle window, Size bar, Rectangle area, int reservedRight, int captionHeight)
    {
        int top = Math.Max(window.Top, area.Top);
        int left = window.Left + (window.Width - bar.Width) / 2;
        left = Math.Min(left, window.Right - reservedRight - bar.Width);
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - bar.Width));
        if (left + bar.Width > window.Right - reservedRight)
            top += captionHeight;
        return new Point(left, Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - bar.Height)));
    }

    /// <summary>
    /// The part of the title bar where resting the mouse opens the toolbar. It lies inside the toolbar's
    /// width, so the pointer that opened the toolbar never starts out too far away to keep it open.
    /// </summary>
    public static Rectangle HoverZone(Rectangle window, Rectangle bar, Rectangle area, int reservedRight,
        int captionHeight)
    {
        int left = Math.Max(window.Left, bar.Left);
        int right = Math.Max(left, Math.Min(window.Right - reservedRight, bar.Right));
        int top = Math.Max(window.Top, area.Top);
        return Rectangle.FromLTRB(left, top, right, top + captionHeight);
    }
}
