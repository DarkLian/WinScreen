namespace WinScreen.Pip;

// A recording keeps the size of its first frame; a later picture of another shape is scaled into it, centred, with
// black around it.
internal static class RecordingGeometry
{
    // H.264 encoders refuse odd sizes, and many hardware ones anything over 4096 on a side or tiny frames.
    internal const int Longest = 4096, Shortest = 64, Rate = 30;
    private const long MostPixels = 4096L * 2304;

    internal static Size FrameSize(Size shown)
    {
        if (shown.Width <= 0 || shown.Height <= 0) return new Size(Shortest, Shortest);
        double scale = Math.Min(1, Math.Min(Longest / (double)Math.Max(shown.Width, shown.Height),
            Math.Sqrt(MostPixels / ((double)shown.Width * shown.Height))));
        if (Math.Min(shown.Width, shown.Height) * scale < Shortest) scale = Shortest / (double)Math.Min(shown.Width, shown.Height);
        return new Size(Even(shown.Width * scale), Even(shown.Height * scale));
    }

    private static int Even(double side) => Math.Clamp((int)Math.Round(side) / 2 * 2, Shortest, Longest);

    internal static Rectangle Letterbox(Size picture, Size frame)
    {
        if (picture.Width <= 0 || picture.Height <= 0) return new Rectangle(Point.Empty, frame);
        double scale = Math.Min(frame.Width / (double)picture.Width, frame.Height / (double)picture.Height);
        int width = Math.Clamp((int)Math.Round(picture.Width * scale), 1, frame.Width);
        int height = Math.Clamp((int)Math.Round(picture.Height * scale), 1, frame.Height);
        return new Rectangle((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
    }

    // Enough for screen content to stay sharp: about 7.5 Mbit/s at 1920 x 1080.
    internal static int Bitrate(Size frame, int rate) =>
        (int)Math.Clamp((long)frame.Width * frame.Height * rate / 8, 1_000_000L, 50_000_000L);

    // Where the newest of `due` frames falls; one running more than a second late jumps ahead rather than queueing.
    internal static long NextTick(long tick, double elapsedSeconds, int rate)
    {
        long now = (long)Math.Floor(elapsedSeconds * rate);
        return now - tick > rate ? now : tick;
    }
}
