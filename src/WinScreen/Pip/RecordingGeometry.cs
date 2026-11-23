namespace WinScreen.Pip;

// A recording keeps the size of its first frame; a later picture of another shape is scaled into it, centred, with
// black around it.
internal static class RecordingGeometry
{
    // H.264 encoders refuse odd sizes, and many hardware ones anything over 4096 on a side or tiny frames.
    internal const int Longest = 4096, Shortest = 64;
    // A view whose rate isn't known yet records at this.
    internal const int DefaultRate = 60;
    // H.264 level 5.2: macroblocks (16 x 16) a second.
    private const long MostMacroblocks = 2_073_600;
    private static readonly int[] Standard = [240, 165, 144, 120, 90, 75, 60, 50, 48, 30, 25, 24, 15];
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

    // The view's own rate (its frame rate, or its display's refresh), as far as H.264 allows at this size.
    internal static int RateFor(int viewRate, Size frame)
    {
        int rate = viewRate <= 0 ? DefaultRate : Math.Clamp(viewRate, 15, 240);
        long macroblocks = (long)((frame.Width + 15) / 16) * ((frame.Height + 15) / 16);
        long most = MostMacroblocks / Math.Max(1, macroblocks);
        return rate <= most ? rate : Standard.FirstOrDefault(each => each <= most, 15);
    }

    // To try when an encoder refuses a rate: that one, then each usual one below it.
    internal static IEnumerable<int> RatesFrom(int rate) => [rate, .. Standard.Where(each => each < rate && each is 144 or 120 or 60 or 30)];

    // Enough for screen content to stay sharp: about 7.8 Mbit/s at 1920 x 1080 and 30 a second. More frames need less
    // each, as each differs less from the last: twice the rate, about 1.4 times the bits.
    internal static int Bitrate(Size frame, int rate) =>
        (int)Math.Clamp((long)(frame.Width * (double)frame.Height * 30 / 8 * Math.Sqrt(rate / 30.0)), 1_000_000L, 50_000_000L);

    // The next tick to write. A short stall (an encoder starting cold, seen as a second on the test VM) is caught up by
    // writing the same frame again, which encodes fast; more than three seconds behind, the clock jumps ahead instead.
    internal static long NextTick(long tick, double elapsedSeconds, int rate)
    {
        long now = (long)Math.Floor(elapsedSeconds * rate);
        return now - tick > 3L * rate ? now : tick;
    }
}
