namespace WinScreen.Pip;

// Between the picture as a view shows it (View, on the screen or in the view's client area) and the part of the
// source it shows (Shown). A turned view's points go through the upright picture pixel by pixel, as the shader draws.
internal readonly record struct PipMapping(Rectangle View, Rectangle Shown, ViewTurn Turn = default)
{
    private Rectangle UprightView => Turn.Oriented(View);

    private Point Upright(Point point)
    {
        var pixel = new Point(Math.Clamp(point.X - View.Left, 0, Math.Max(0, View.Width - 1)),
            Math.Clamp(point.Y - View.Top, 0, Math.Max(0, View.Height - 1)));
        return Turn.ToUpright(pixel, View.Size) + (Size)View.Location;
    }

    public Point ToSource(Point point) => Turn.IsUpright
        ? PipGeometry.PointToSource(point, View, Shown)
        : PipGeometry.PointToSource(Upright(point), UprightView, Shown);

    public Rectangle SelectionToSource(Rectangle selection)
    {
        if (Turn.IsUpright) return PipGeometry.SelectionToSource(selection, View, Shown);
        Rectangle inView = Rectangle.Intersect(selection, View);
        if (inView.Width <= 0 || inView.Height <= 0) return Rectangle.Empty;
        Point a = Upright(inView.Location), b = Upright(new Point(inView.Right - 1, inView.Bottom - 1));
        var upright = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
        return PipGeometry.SelectionToSource(upright, UprightView, Shown);
    }

    // A rectangle of the source, as the view shows it: the smallest rectangle around it.
    public Rectangle ToView(Rectangle inSource)
    {
        Rectangle upright = UprightView;
        if (upright.Width <= 0 || upright.Height <= 0 || Shown.Width <= 0 || Shown.Height <= 0) return Rectangle.Empty;
        Rectangle shown = Shown;
        double scaleX = upright.Width / (double)shown.Width, scaleY = upright.Height / (double)shown.Height;
        int Across(int x) => (int)Math.Round((x - shown.Left) * scaleX);
        int Down(int y) => (int)Math.Round((y - shown.Top) * scaleY);
        int left = Math.Clamp(Across(inSource.Left), 0, upright.Width - 1), top = Math.Clamp(Down(inSource.Top), 0, upright.Height - 1);
        int right = Math.Clamp(Across(inSource.Right), left + 1, upright.Width) - 1;
        int bottom = Math.Clamp(Down(inSource.Bottom), top + 1, upright.Height) - 1;
        Point a = Turn.ToView(new Point(left, top), upright.Size);
        Point b = Turn.ToView(new Point(right, bottom), upright.Size);
        return Rectangle.FromLTRB(View.Left + Math.Min(a.X, b.X), View.Top + Math.Min(a.Y, b.Y),
            View.Left + Math.Max(a.X, b.X) + 1, View.Top + Math.Max(a.Y, b.Y) + 1);
    }

    public Size DirectionToSource(Size direction) => Turn.ToUpright(direction);
}
