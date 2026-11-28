namespace WinScreen.Settings;

// Parts of an app's windows that Glass and Dark mode leave as they are, such as a video player's picture.
public sealed record KeptAreaPreferences
{
    internal const int MostPerApp = 8;

    public List<AppKeptAreas> Apps { get; init; } = [];

    internal KeptAreaPreferences Validated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppKeptAreas>();
        foreach (AppKeptAreas? app in Apps ?? [])
        {
            string program = BackgroundPreferences.ProgramKey(app?.Program);
            List<KeptArea> areas = (app?.Areas ?? []).Where(area => area is not null).Select(area => area.Validated())
                .Where(area => area.IsUsable).Distinct().Take(MostPerApp).ToList();
            if (program.Length > 0 && areas.Count > 0 && seen.Add(program))
                apps.Add(new AppKeptAreas { Program = program, Areas = areas });
        }
        return this with { Apps = apps };
    }

    internal IReadOnlyList<KeptArea> For(string? program)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        return Apps.FirstOrDefault(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase))?.Areas ?? [];
    }

    internal KeptAreaPreferences With(string program, IEnumerable<KeptArea> areas)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        List<AppKeptAreas> apps = Apps.Where(app => !string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase)).ToList();
        apps.Add(new AppKeptAreas { Program = key, Areas = areas.ToList() });
        return (this with { Apps = apps }).Validated();
    }

    internal KeptAreaPreferences Without(IEnumerable<string> programs)
    {
        var gone = new HashSet<string>(programs.Select(program => BackgroundPreferences.ProgramKey(program)), StringComparer.OrdinalIgnoreCase);
        return this with { Apps = Apps.Where(app => !gone.Contains(app.Program)).ToList() };
    }
}

public sealed record AppKeptAreas
{
    public string Program { get; init; } = "";
    public List<KeptArea> Areas { get; init; } = [];
}

// Each edge is a distance, in pixels at 100% scaling, from one side of the window: by default the area's own side
// (an inset), or the other side when the edge was nearer that one. So a player's picture, between a top bar and a
// bottom bar of fixed height, stays on the picture when the window is resized.
// Marked over a control (ControlType not 0), the area follows that control instead, found by these fields where the
// rectangle is; the rectangle is used only while the app doesn't answer (KeptAreaFollower).
public sealed record KeptArea
{
    private const int Largest = 20000;
    private const int LongestName = 256;

    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public bool LeftFromRight { get; init; }
    public bool TopFromBottom { get; init; }
    public bool RightFromLeft { get; init; }
    public bool BottomFromTop { get; init; }
    public int ControlType { get; init; }
    public string ControlClass { get; init; } = "";
    public string ControlId { get; init; } = "";

    internal bool Follows => ControlType != 0;

    internal bool IsUsable => Left >= 0 && Top >= 0 && Right >= 0 && Bottom >= 0;

    internal KeptArea Validated() => this with
    {
        Left = Math.Clamp(Left, 0, Largest), Top = Math.Clamp(Top, 0, Largest),
        Right = Math.Clamp(Right, 0, Largest), Bottom = Math.Clamp(Bottom, 0, Largest),
        ControlType = Math.Max(0, ControlType), ControlClass = Name(ControlClass), ControlId = Name(ControlId),
    };

    private static string Name(string? name) => name is null ? "" : name.Length > LongestName ? name[..LongestName] : name;

    // part is in the window's pixels; scale is the window's DPI over 96.
    internal static KeptArea Of(Rectangle part, Size window, float scale)
    {
        int Unscaled(int pixels) => (int)MathF.Round(Math.Max(0, pixels) / scale);
        bool leftFromRight = part.Left > window.Width / 2, rightFromLeft = part.Right < window.Width / 2;
        bool topFromBottom = part.Top > window.Height / 2, bottomFromTop = part.Bottom < window.Height / 2;
        return new KeptArea
        {
            Left = Unscaled(leftFromRight ? window.Width - part.Left : part.Left),
            Right = Unscaled(rightFromLeft ? part.Right : window.Width - part.Right),
            Top = Unscaled(topFromBottom ? window.Height - part.Top : part.Top),
            Bottom = Unscaled(bottomFromTop ? part.Bottom : window.Height - part.Bottom),
            LeftFromRight = leftFromRight, RightFromLeft = rightFromLeft, TopFromBottom = topFromBottom, BottomFromTop = bottomFromTop,
        };
    }

    // Empty when the window is too small for it.
    internal Rectangle In(Size window, float scale)
    {
        int Scaled(int units) => (int)MathF.Round(units * scale);
        int left = LeftFromRight ? window.Width - Scaled(Left) : Scaled(Left);
        int right = RightFromLeft ? Scaled(Right) : window.Width - Scaled(Right);
        int top = TopFromBottom ? window.Height - Scaled(Top) : Scaled(Top);
        int bottom = BottomFromTop ? Scaled(Bottom) : window.Height - Scaled(Bottom);
        Rectangle area = Rectangle.Intersect(Rectangle.FromLTRB(left, top, right, bottom), new Rectangle(Point.Empty, window));
        return area.Width > 0 && area.Height > 0 ? area : Rectangle.Empty;
    }
}
