namespace WinScreen.Pip;

/// <summary>A captured frame boiled down to the brightness of each small tile, for telling what moves.</summary>
internal sealed record FrameSample(Size Surface, int Columns, int Rows, byte[] Brightness)
{
    public const int Tile = 8;

    /// <summary>From a frame's pixels (blue, green, red, alpha), reading 4 points in each tile.</summary>
    internal static unsafe FrameSample From(byte* pixels, int pitch, Size surface)
    {
        int columns = Math.Max(1, surface.Width / Tile), rows = Math.Max(1, surface.Height / Tile);
        var brightness = new byte[columns * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int sum = 0;
                for (int dy = 2; dy < Tile; dy += 4)
                {
                    byte* row = pixels + (y * Tile + dy) * pitch;
                    for (int dx = 2; dx < Tile; dx += 4)
                    {
                        byte* p = row + (x * Tile + dx) * 4;
                        sum += (p[0] * 29 + p[1] * 150 + p[2] * 77) >> 8;
                    }
                }
                brightness[y * columns + x] = (byte)(sum / 4);
            }
        return new FrameSample(surface, columns, rows, brightness);
    }
}

/// <summary>
/// Finds the largest moving picture in a window, such as a playing video, from frames taken a moment apart (roadmap
/// step 8.2). Browsers don't tell accessibility where a playing video is, so this looks at the picture: the tiles that
/// keep changing, joined into areas, and the largest area that is mostly moving and big enough to be a video.
/// </summary>
internal sealed class MotionFinder
{
    /// <summary>A brightness change below this is noise, such as a cursor's blink being anti-aliased.</summary>
    private const int Threshold = 6;
    private const int SmallestColumns = 12, SmallestRows = 8; // 96 by 64 pixels.

    private FrameSample? _previous;
    private int[] _changes = [];

    /// <summary>How many pairs of frames have been compared.</summary>
    public int Comparisons { get; private set; }

    public Size Surface => _previous?.Surface ?? Size.Empty;

    public void Add(FrameSample sample)
    {
        if (_previous is null || _previous.Columns != sample.Columns || _previous.Rows != sample.Rows)
        {
            _previous = sample;
            _changes = new int[sample.Brightness.Length];
            Comparisons = 0;
            return;
        }
        for (int k = 0; k < _changes.Length; k++)
            if (Math.Abs(sample.Brightness[k] - _previous.Brightness[k]) > Threshold)
                _changes[k]++;
        Comparisons++;
        _previous = sample;
    }

    /// <summary>The largest moving area, in the frame's pixels, or null when nothing big enough moves.</summary>
    public Rectangle? Largest()
    {
        if (_previous is null || Comparisons == 0) return null;
        int columns = _previous.Columns, rows = _previous.Rows;
        int needed = Math.Max(1, Comparisons / 3);
        var moving = new bool[_changes.Length];
        for (int k = 0; k < moving.Length; k++) moving[k] = _changes[k] >= needed;

        // A video's dark or still patches don't change every time; joining across one tile keeps it whole.
        var joined = new bool[moving.Length];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
                for (int dy = -1; dy <= 1 && !joined[y * columns + x]; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < columns && ny < rows && moving[ny * columns + nx]) { joined[y * columns + x] = true; break; }
                    }

        Rectangle best = Rectangle.Empty;
        var seen = new bool[moving.Length];
        var queue = new Queue<int>();
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start] || seen[start]) continue;
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), x = k % columns, y = k / columns;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                foreach (int next in new[] { x > 0 ? k - 1 : -1, x < columns - 1 ? k + 1 : -1, y > 0 ? k - columns : -1, y < rows - 1 ? k + columns : -1 })
                    if (next >= 0 && joined[next] && !seen[next]) { seen[next] = true; queue.Enqueue(next); }
            }
            Rectangle area = Trim(moving, columns, Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
            if (area.Width < SmallestColumns || area.Height < SmallestRows) continue;
            if (Count(moving, columns, area) < area.Width * area.Height * 6 / 10) continue; // Scattered changes, not one picture.
            if (area.Width * area.Height > best.Width * best.Height) best = area;
        }
        if (best.IsEmpty) return null;
        var pixels = new Rectangle(best.X * FrameSample.Tile, best.Y * FrameSample.Tile, best.Width * FrameSample.Tile, best.Height * FrameSample.Tile);
        return Rectangle.Intersect(pixels, new Rectangle(Point.Empty, _previous.Surface));
    }

    /// <summary>Takes off edge rows and columns that are mostly still: the joining above adds one all round.</summary>
    private static Rectangle Trim(bool[] moving, int columns, Rectangle area)
    {
        bool Mostly(Rectangle line) => line.Width > 0 && line.Height > 0 && Count(moving, columns, line) * 2 >= line.Width * line.Height;
        while (area.Height > 0 && !Mostly(area with { Height = 1 })) area = Rectangle.FromLTRB(area.Left, area.Top + 1, area.Right, area.Bottom);
        while (area.Height > 0 && !Mostly(area with { Y = area.Bottom - 1, Height = 1 })) area = Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom - 1);
        while (area.Width > 0 && !Mostly(area with { Width = 1 })) area = Rectangle.FromLTRB(area.Left + 1, area.Top, area.Right, area.Bottom);
        while (area.Width > 0 && !Mostly(area with { X = area.Right - 1, Width = 1 })) area = Rectangle.FromLTRB(area.Left, area.Top, area.Right - 1, area.Bottom);
        return area;
    }

    private static int Count(bool[] moving, int columns, Rectangle area)
    {
        int count = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (moving[y * columns + x]) count++;
        return count;
    }
}
