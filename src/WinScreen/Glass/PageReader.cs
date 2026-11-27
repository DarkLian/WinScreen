namespace WinScreen.Glass;

internal readonly record struct TileStats(float R, float G, float B, float Exact, bool Busy);

internal sealed class PageReader
{
    public const int Tile = 16;
    private int _key = -1;

    public (float R, float G, float B) Page { get; private set; }

    public bool AlreadyDark { get; private set; }

    // Glass's text colour flips a light page for light text and a dark one for dark text. The margin keeps a mid-grey
    // page from flipping back and forth.
    public bool Light { get; private set; }

    // Kept between frames: a new one each frame was steady garbage while a page showed.
    private readonly Dictionary<int, int> _count = [];

    public void Read(ReadOnlySpan<TileStats> tiles)
    {
        if (tiles.Length == 0)
            return;
        Dictionary<int, int> count = _count;
        count.Clear();
        foreach (TileStats t in tiles)
            count[Key(t)] = count.GetValueOrDefault(Key(t)) + 1;
        KeyValuePair<int, int> top = count.MaxBy(pair => pair.Value);
        // Two colours sharing a page about equally made the page colour swap from frame to frame, and every pixel's
        // see-through amount with it: the page colour changes only when another clearly takes over.
        if (_key < 0 || count.GetValueOrDefault(_key) < top.Value * 0.75f)
            _key = top.Key;

        float r = 0, g = 0, b = 0;
        int n = 0;
        foreach (TileStats t in tiles)
            if (!t.Busy && Key(t) == _key) { r += t.R; g += t.G; b += t.B; n++; }
        Page = n == 0 ? ((_key >> 10 & 31) / 31f, (_key >> 5 & 31) / 31f, (_key & 31) / 31f) : (r / n, g / n, b / n);

        // The margin keeps a mid-grey page from switching between left alone and flipped.
        float light = 0.3f * Page.R + 0.59f * Page.G + 0.11f * Page.B;
        AlreadyDark = AlreadyDark ? light < 0.3f : light < 0.2f;
        Light = Light ? light > 0.45f : light > 0.55f;
    }

    public void Reset()
    {
        _key = -1;
        AlreadyDark = false;
        Light = false;
    }

    private static int Key(TileStats t) => (int)(t.R * 31) << 10 | (int)(t.G * 31) << 5 | (int)(t.B * 31);
}
