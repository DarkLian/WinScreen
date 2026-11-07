namespace WinScreen.Pip;

internal static class PipRenderGeometry
{
    // WinForms PerMonitorV2 client coordinates already are physical pixels: never multiply by DPI again.
    internal static Rectangle Crop(Rectangle crop, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var whole = new Rectangle(Point.Empty, size);
        if (crop.IsEmpty) return whole;
        long right = Math.Min((long)size.Width, (long)crop.X + crop.Width);
        long bottom = Math.Min((long)size.Height, (long)crop.Y + crop.Height);
        int left = Math.Max(0, crop.X), top = Math.Max(0, crop.Y);
        return right > left && bottom > top ? Rectangle.FromLTRB(left, top, (int)right, (int)bottom) : whole;
    }

    internal static Size Target(Size pixels, Size source, int limit, int pixelBudget)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || source.Width <= 0 || source.Height <= 0 ||
            limit <= 0 || pixelBudget <= 0) throw new ArgumentOutOfRangeException(nameof(pixels));
        double scale = Math.Min(1, Math.Min(limit / (double)pixels.Width, limit / (double)pixels.Height));
        scale = Math.Min(scale, Math.Sqrt(pixelBudget / ((double)pixels.Width * pixels.Height)));
        return new Size(Math.Max(1, (int)Math.Floor(pixels.Width * scale)),
            Math.Max(1, (int)Math.Floor(pixels.Height * scale)));
    }
}
