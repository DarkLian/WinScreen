namespace WinScreen.Pip;

internal static class ViewCovers
{
    internal const int Most = 8;

    // The parts of source left once the covers are taken out, each with where it goes in destination. Edges are
    // worked out once for all parts, so parts side by side meet exactly.
    internal static List<(Rectangle Source, Rectangle Destination)> Pieces(Rectangle source, Rectangle destination,
        IReadOnlyList<Rectangle> covers)
    {
        var result = new List<(Rectangle, Rectangle)>();
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return result;
        Rectangle[] inside = covers.Select(cover => Rectangle.Intersect(cover, source))
            .Where(cover => cover.Width > 0 && cover.Height > 0).ToArray();
        if (inside.Length == 0)
        {
            result.Add((source, destination));
            return result;
        }
        int[] xs = inside.SelectMany(cover => new[] { cover.Left, cover.Right }).Append(source.Left).Append(source.Right)
            .Distinct().Order().ToArray();
        int[] ys = inside.SelectMany(cover => new[] { cover.Top, cover.Bottom }).Append(source.Top).Append(source.Bottom)
            .Distinct().Order().ToArray();
        int Across(int x) => destination.Left + (int)Math.Round((x - source.Left) * (double)destination.Width / source.Width);
        int Down(int y) => destination.Top + (int)Math.Round((y - source.Top) * (double)destination.Height / source.Height);

        // Runs of open cells across each band, joined with the same run in the band above.
        var open = new Dictionary<(int Left, int Right), Rectangle>();
        var finished = new List<Rectangle>();
        for (int band = 0; band < ys.Length - 1; band++)
        {
            int top = ys[band], bottom = ys[band + 1];
            var runs = new List<(int Left, int Right)>();
            int? start = null;
            for (int column = 0; column < xs.Length - 1; column++)
            {
                var cell = Rectangle.FromLTRB(xs[column], top, xs[column + 1], bottom);
                bool covered = inside.Any(cover => cover.IntersectsWith(cell));
                if (!covered) start ??= xs[column];
                else if (start is { } left) { runs.Add((left, xs[column])); start = null; }
            }
            if (start is { } last) runs.Add((last, xs[^1]));
            var next = new Dictionary<(int, int), Rectangle>();
            foreach ((int left, int right) in runs)
                next[(left, right)] = open.Remove((left, right), out Rectangle above) && above.Bottom == top
                    ? Rectangle.FromLTRB(left, above.Top, right, bottom)
                    : Rectangle.FromLTRB(left, top, right, bottom);
            finished.AddRange(open.Values);
            open = next;
        }
        finished.AddRange(open.Values);
        foreach (Rectangle part in finished)
        {
            var place = Rectangle.FromLTRB(Across(part.Left), Down(part.Top), Across(part.Right), Down(part.Bottom));
            if (place.Width > 0 && place.Height > 0) result.Add((part, place));
        }
        return result;
    }

    internal static Rectangle[] Moved(IReadOnlyList<Rectangle> covers, Point by) =>
        covers.Select(cover => cover with { X = cover.X + by.X, Y = cover.Y + by.Y }).ToArray();
}

// One compositor picture made of pieces, so covered parts are left out of it: what the window shows there is its own
// black background.
internal sealed class DwmPieces : IDisposable
{
    private readonly nint _destination, _source;
    private readonly List<DwmThumbnail> _thumbnails = [];

    private DwmPieces(nint destination, nint source, DwmThumbnail first)
    {
        _destination = destination;
        _source = source;
        _thumbnails.Add(first);
    }

    public static DwmPieces? TryCreate(nint destination, nint source) =>
        DwmThumbnail.TryCreate(destination, source) is { } first ? new DwmPieces(destination, source, first) : null;

    public Size SourceSize => _thumbnails[0].SourceSize;

    public void Show(Rectangle destination, Rectangle source, IReadOnlyList<Rectangle> covers, byte opacity = 255, bool visible = true)
    {
        List<(Rectangle Source, Rectangle Destination)> pieces = visible ? ViewCovers.Pieces(source, destination, covers) : [];
        // Pieces no longer needed are let go, not just hidden: one left showing an old part could show a covered one.
        // The first stays registered, for the source's size.
        while (_thumbnails.Count > Math.Max(1, pieces.Count))
        {
            _thumbnails[^1].Dispose();
            _thumbnails.RemoveAt(_thumbnails.Count - 1);
        }
        while (_thumbnails.Count < pieces.Count)
            _thumbnails.Add(DwmThumbnail.TryCreate(_destination, _source) ??
                throw new Windows.WindowOperationException(Localization.UiText.T("errors.pip.renderFailed")));
        if (pieces.Count == 0)
        {
            _thumbnails[0].Hide();
            return;
        }
        for (int i = 0; i < pieces.Count; i++) _thumbnails[i].Show(pieces[i].Destination, pieces[i].Source, opacity);
    }

    public void Dispose()
    {
        foreach (DwmThumbnail thumbnail in _thumbnails) thumbnail.Dispose();
        _thumbnails.Clear();
    }
}
