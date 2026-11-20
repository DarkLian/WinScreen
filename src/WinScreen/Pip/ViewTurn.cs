namespace WinScreen.Pip;

// How a view shows its picture: turned clockwise a number of quarter turns, then mirrored left to right.
internal readonly record struct ViewTurn
{
    public ViewTurn(int turns, bool mirrored)
    {
        Turns = (turns % 4 + 4) % 4;
        Mirrored = mirrored;
    }

    public int Turns { get; }
    public bool Mirrored { get; }

    public static ViewTurn Upright => default;
    public bool IsUpright => Turns == 0 && !Mirrored;
    public bool Swaps => Turns % 2 == 1;

    // What the person sees turns or flips; a mirror reverses which way the stored turns go.
    public ViewTurn RotatedRight => new(Turns + (Mirrored ? -1 : 1), Mirrored);
    public ViewTurn RotatedLeft => new(Turns + (Mirrored ? 1 : -1), Mirrored);
    public ViewTurn FlippedAcross => new(Turns, !Mirrored);
    public ViewTurn FlippedDown => new(Turns + 2, !Mirrored);

    public Size Oriented(Size size) => Swaps ? new Size(size.Height, size.Width) : size;
    public Rectangle Oriented(Rectangle rectangle) => new(rectangle.Location, Oriented(rectangle.Size));

    // Pixel indexes, so the drawing and the input agree to the pixel: the upright picture's pixel shown at a view
    // pixel, and back. The shader's PS does the same steps as ToUpright.
    public Point ToUpright(Point view, Size viewSize)
    {
        int x = view.X, y = view.Y, width = viewSize.Width, height = viewSize.Height;
        if (Mirrored) x = width - 1 - x;
        for (int turn = 0; turn < Turns; turn++)
        {
            (x, y) = (y, width - 1 - x);
            (width, height) = (height, width);
        }
        return new Point(x, y);
    }

    public Point ToView(Point upright, Size uprightSize)
    {
        int x = upright.X, y = upright.Y, width = uprightSize.Width, height = uprightSize.Height;
        for (int turn = 0; turn < Turns; turn++)
        {
            (x, y) = (height - 1 - y, x);
            (width, height) = (height, width);
        }
        if (Mirrored) x = width - 1 - x;
        return new Point(x, y);
    }

    // Shares of the picture (0 to 1 each way).
    public PointF ToUpright(PointF view)
    {
        float x = Mirrored ? 1 - view.X : view.X, y = view.Y;
        for (int turn = 0; turn < Turns; turn++) (x, y) = (y, 1 - x);
        return new PointF(x, y);
    }

    // A direction only, such as a drag or a pan.
    public SizeF ToUpright(SizeF view)
    {
        float x = Mirrored ? -view.Width : view.Width, y = view.Height;
        for (int turn = 0; turn < Turns; turn++) (x, y) = (y, -x);
        return new SizeF(x, y);
    }

    public Size ToUpright(Size view)
    {
        int x = Mirrored ? -view.Width : view.Width, y = view.Height;
        for (int turn = 0; turn < Turns; turn++) (x, y) = (y, -x);
        return new Size(x, y);
    }

    public override string ToString() => $"{Turns * 90}{(Mirrored ? " mirrored" : "")}";
}
