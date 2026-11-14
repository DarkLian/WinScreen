namespace WinScreen.Glass;

/// <summary>What the GPU measured for one 16-pixel tile.</summary>
internal readonly record struct TileStats(float R, float G, float B, float Exact, bool Busy);

/// <summary>
/// Finds the pictures in a window from its tile statistics, so effects can treat pictures, text and the
/// page differently. A picture is a large, rich block that isn't page colour. Flat panels and bars don't
/// count, unless they're black and part of the picture's edge, as a letterbox is.
/// </summary>
internal sealed class PictureFinder(Func<long>? clock = null)
{
    public const int Tile = 16;
    /// <summary>The mask's values: picture, decide each pixel, a plain panel, or nothing.</summary>
    public const float Picture = 1, PerPixel = 0.5f, Panel = 0.2f, None = 0;
    /// <summary>For Dark: a part that is dark already, such as a dark title bar or panel, which stays as it is.</summary>
    public const float AlreadyDark = 0.05f;
    private const int HoldMilliseconds = 5000;

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly List<(Rectangle Tiles, long Until)> _held = [];

    public (float R, float G, float B) Page { get; private set; }

    public float[] Find(TileStats[] tiles, int columns, int rows)
    {
        var mask = new float[tiles.Length];
        if (tiles.Length == 0 || columns * rows != tiles.Length)
            return mask;
        Page = PageColour(tiles);
        (float pr, float pg, float pb) = Page;

        bool IsPage(int k)
        {
            TileStats t = tiles[k];
            return Distance(t.R - pr, t.G - pg, t.B - pb) < 0.0004f && t.Exact > 0.5f;
        }

        var other = new bool[tiles.Length];
        for (int k = 0; k < tiles.Length; k++)
        {
            TileStats t = tiles[k];
            bool flat = t.Exact > 0.9f; // A flat panel or background, whatever its colour.
            other[k] = !flat && (t.Busy || Distance(t.R - pr, t.G - pg, t.B - pb) > 0.0144f || t.Exact < 0.5f);
        }

        float Mean(Rectangle area, Func<TileStats, float> channel)
        {
            float sum = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    sum += channel(tiles[y * columns + x]);
            return sum / Math.Max(1, area.Width * area.Height);
        }

        float Flat(Rectangle area) => Mean(area, t => t.Exact > 0.9f ? 1 : 0);
        bool Black(Rectangle area) => Math.Max(Mean(area, t => t.R), Math.Max(Mean(area, t => t.G), Mean(area, t => t.B))) < 3f / 255;
        bool AnyPage(Rectangle area)
        {
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (IsPage(y * columns + x)) return true;
            return false;
        }

        bool Joins(Rectangle strip, Rectangle edge)
        {
            float dr = Mean(strip, t => t.R) - Mean(edge, t => t.R), dg = Mean(strip, t => t.G) - Mean(edge, t => t.G),
                db = Mean(strip, t => t.B) - Mean(edge, t => t.B);
            if (Flat(strip) > 0.8f && Black(strip)) return true; // Letterbox and photo borders.
            if (Flat(strip) > 0.8f && (Flat(edge) < 0.5f || Distance(dr, dg, db) > 0.0004f)) return false;
            return Distance(dr, dg, db) < 0.03f;
        }

        var current = new List<Rectangle>();
        var label = new bool[tiles.Length];
        var stack = new Stack<int>();
        for (int start = 0; start < tiles.Length; start++)
        {
            if (!other[start] || label[start]) continue;
            int x0 = columns, x1 = 0, y0 = rows, y1 = 0, cells = 0, busy = 0;
            var colours = new HashSet<int>();
            label[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % columns, cy = c / columns;
                cells++;
                if (tiles[c].Busy) busy++;
                colours.Add((int)(tiles[c].R * 15) << 8 | (int)(tiles[c].G * 15) << 4 | (int)(tiles[c].B * 15));
                x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
                        int n = ny * columns + nx;
                        if (other[n] && !label[n]) { label[n] = true; stack.Push(n); }
                    }
            }

            int width = x1 - x0 + 1, height = y1 - y0 + 1;
            if (width < 4 || height < 4 || cells < width * height * 0.6 || (colours.Count < 8 && busy < cells * 0.15))
                continue;

            Rectangle Row(int y) => new(x0, y, x1 - x0 + 1, 1);
            Rectangle Column(int x) => new(x, y0, 1, y1 - y0 + 1);
            // The block's box can take in a flat bar that only touches the picture at its ends.
            for (int trim = 0; trim < 12; trim++)
            {
                bool any = false;
                if (y1 > y0 + 3 && Flat(Row(y1)) > 0.6f && !Black(Row(y1))) { y1--; any = true; }
                if (y1 > y0 + 3 && Flat(Row(y0)) > 0.6f && !Black(Row(y0))) { y0++; any = true; }
                if (x1 > x0 + 3 && Flat(Column(x1)) > 0.6f && !Black(Column(x1))) { x1--; any = true; }
                if (x1 > x0 + 3 && Flat(Column(x0)) > 0.6f && !Black(Column(x0))) { x0++; any = true; }
                if (!any) break;
            }

            // A picture's flat dark or light edge isn't busy. Grow over a strip that matches the edge it meets.
            for (int grow = 0; grow < 12; grow++)
            {
                bool any = false;
                if (x0 > 0 && !AnyPage(Column(x0 - 1)) && Joins(Column(x0 - 1), Column(x0))) { x0--; any = true; }
                if (x1 < columns - 1 && !AnyPage(Column(x1 + 1)) && Joins(Column(x1 + 1), Column(x1))) { x1++; any = true; }
                if (y0 > 0 && !AnyPage(Row(y0 - 1)) && Joins(Row(y0 - 1), Row(y0))) { y0--; any = true; }
                if (y1 < rows - 1 && !AnyPage(Row(y1 + 1)) && Joins(Row(y1 + 1), Row(y1))) { y1++; any = true; }
                if (!any) break;
            }

            current.Add(Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int k = y * columns + x;
                    TileStats t = tiles[k];
                    // Partly page colour, such as a gap between pictures: each pixel decides.
                    mask[k] = Distance(t.R - pr, t.G - pg, t.B - pb) < 0.0004f && t.Exact > 0.3f ? PerPixel : Picture;
                }
        }

        Hold(mask, current, columns, rows, k => IsPage(k));
        for (int k = 0; k < mask.Length; k++)
            if (mask[k] == None && tiles[k].Exact > 0.9f)
                mask[k] = Panel;
        return mask;
    }

    /// <summary>
    /// Video fades to black and shows title cards. A picture seen recently stays a picture for a few
    /// seconds, so effects don't flicker. Video black often matches a dark page, so only time ends a hold.
    /// </summary>
    private void Hold(float[] mask, List<Rectangle> current, int columns, int rows, Func<int, bool> isPage)
    {
        long now = _clock();
        _held.RemoveAll(held => now > held.Until);
        var grid = new Rectangle(0, 0, columns, rows);
        foreach ((Rectangle tiles, _) in _held)
        {
            Rectangle area = Rectangle.Intersect(tiles, grid);
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (mask[y * columns + x] == None)
                        // Plain page in a remembered picture's place decides by pixel, or it would stay a white block.
                        mask[y * columns + x] = isPage(y * columns + x) ? PerPixel : Picture;
        }

        foreach (Rectangle block in current)
        {
            _held.RemoveAll(held => held.Tiles.IntersectsWith(block));
            _held.Add((block, now + HoldMilliseconds));
        }
    }

    /// <summary>
    /// For Dark: marks the parts that are dark already, a tile whose own colour is dark and that isn't a picture, so a
    /// dark title bar, toolbar or panel stays dark instead of turning light.
    /// </summary>
    public static void MarkAlreadyDark(float[] mask, TileStats[] tiles)
    {
        for (int k = 0; k < mask.Length && k < tiles.Length; k++)
        {
            TileStats t = tiles[k];
            if (mask[k] is None or Panel && 0.3f * t.R + 0.59f * t.G + 0.11f * t.B < 0.25f)
                mask[k] = AlreadyDark;
        }
    }

    /// <summary>Forgets held pictures, for when the window was resized or scrolled a long way.</summary>
    public void Reset() => _held.Clear();

    private static float Distance(float r, float g, float b) => r * r + g * g + b * b;

    /// <summary>The most common tile colour, averaged over its tiles, so a flat page compares exactly.</summary>
    private static (float, float, float) PageColour(TileStats[] tiles)
    {
        var count = new Dictionary<int, int>();
        int Key(TileStats t) => (int)(t.R * 31) << 10 | (int)(t.G * 31) << 5 | (int)(t.B * 31);
        foreach (TileStats t in tiles)
            count[Key(t)] = count.GetValueOrDefault(Key(t)) + 1;
        int top = count.MaxBy(pair => pair.Value).Key;
        float r = 0, g = 0, b = 0;
        int n = 0;
        foreach (TileStats t in tiles)
            if (!t.Busy && Key(t) == top) { r += t.R; g += t.G; b += t.B; n++; }
        return n == 0 ? ((top >> 10 & 31) / 31f, (top >> 5 & 31) / 31f, (top & 31) / 31f) : (r / n, g / n, b / n);
    }
}
