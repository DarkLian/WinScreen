namespace WinScreen.Settings;

// On by default (plain Glass passes presses through its see-through page); a PC with no graphics card gets plain Glass.
public sealed record EffectPreferences
{
    // The fade's band ends at 0.03 + 0.27 x the range (ContentShaders.Composite); at 650% that passes sqrt(3), the farthest
    // any colour can be from the page, so every colour fades.
    internal const int MostFadeRange = 650;
    internal const int MostDarkPage = 40, LeastDarkText = 50;
    public bool LayeredGlass { get; init; } = true;
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int FadeRangePercent { get; init; } = 100;
    public Glass.GlassText TextColor { get; init; }
    public int ShadowPercent { get; init; } = 100;
    public int ShadowWidth { get; init; } = TextShadowLevels.Width;
    // Read by copies from before 1.5.3 only.
    public bool TextShadow => ShadowPercent > 0;
    public int DarkPagePercent { get; init; } = 10;
    public int DarkTextPercent { get; init; } = 100;
    public int DarkColorsPercent { get; init; }
    // Apps with their own Glass or Dark levels, or both; every other app, and the other part, use the ones above.
    public List<AppGlassLevels> Apps { get; init; } = [];

    internal EffectPreferences Validated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppGlassLevels>();
        foreach (AppGlassLevels? app in Apps ?? [])
            if (app is { OwnGlass: true } or { OwnDark: true } && BackgroundPreferences.ProgramKey(app.Program) is { Length: > 0 } program && seen.Add(program))
                apps.Add(app.Validated() with { Program = program });
        return this with
        {
            BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
            TextPercent = Math.Clamp(TextPercent, 20, 100),
            FadeRangePercent = Math.Clamp(FadeRangePercent, 1, EffectPreferences.MostFadeRange),
            TextColor = Enum.IsDefined(TextColor) ? TextColor : Glass.GlassText.AsItIs,
            DarkPagePercent = Math.Clamp(DarkPagePercent, 0, MostDarkPage),
            DarkTextPercent = Math.Clamp(DarkTextPercent, LeastDarkText, 100),
            DarkColorsPercent = Math.Clamp(DarkColorsPercent, 0, 100),
            ShadowPercent = Math.Clamp(ShadowPercent, 0, 100),
            ShadowWidth = Math.Clamp(ShadowWidth, TextShadowLevels.Thinnest, TextShadowLevels.Widest),
            Apps = apps,
        };
    }

    internal Glass.ContentLevels Levels => Main("").Levels;

    internal AppGlassLevels? OwnOf(string program)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        return Apps.Find(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase));
    }

    internal Glass.ContentLevels LevelsFor(string program) => LevelsOf(program).Levels;

    // The app's own levels, or (with null) back to the ones every app has.
    internal EffectPreferences With(string program, AppGlassLevels? own)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        var apps = Apps.Where(app => !string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own is { OwnGlass: true } or { OwnDark: true } && key.Length > 0) apps.Add(own.Validated() with { Program = key });
        return this with { Apps = apps };
    }

    // One part, Glass's or Dark's, becomes the app's own; the other part stays as it was.
    internal EffectPreferences WithPart(string program, AppGlassLevels levels, bool dark)
    {
        AppGlassLevels own = OwnOf(program) ?? Main(program) with { OwnGlass = false };
        return With(program, dark ? own.WithDarkOf(levels) with { OwnDark = true } : own.WithGlassOf(levels) with { OwnGlass = true });
    }

    // As the app's windows get them: each part its own or every app's.
    internal AppGlassLevels LevelsOf(string program)
    {
        AppGlassLevels main = Main(program);
        if (OwnOf(program) is not { } own) return main with { OwnGlass = false };
        AppGlassLevels glass = own.OwnGlass ? own : own.WithGlassOf(main);
        return own.OwnDark ? glass : glass.WithDarkOf(main);
    }

    private AppGlassLevels Main(string program) => new()
    {
        Program = BackgroundPreferences.ProgramKey(program),
        BackgroundPercent = BackgroundPercent, TextPercent = TextPercent, FadeRangePercent = FadeRangePercent,
        TextColor = TextColor, ShadowPercent = ShadowPercent, ShadowWidth = ShadowWidth,
        DarkPagePercent = DarkPagePercent, DarkTextPercent = DarkTextPercent, DarkColorsPercent = DarkColorsPercent,
    };
}

public sealed record AppGlassLevels
{
    public string Program { get; init; } = "";
    public bool OwnGlass { get; init; } = true;
    public bool OwnDark { get; init; }
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int FadeRangePercent { get; init; } = 100;
    public Glass.GlassText TextColor { get; init; }
    public int ShadowPercent { get; init; } = 100;
    public int ShadowWidth { get; init; } = TextShadowLevels.Width;
    // Read by copies from before 1.5.3 only.
    public bool TextShadow => ShadowPercent > 0;
    public int DarkPagePercent { get; init; } = 10;
    public int DarkTextPercent { get; init; } = 100;
    public int DarkColorsPercent { get; init; }

    internal AppGlassLevels Validated() => this with
    {
        Program = Program?.Trim() ?? "",
        BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
        TextPercent = Math.Clamp(TextPercent, 20, 100),
        FadeRangePercent = Math.Clamp(FadeRangePercent, 1, EffectPreferences.MostFadeRange),
        TextColor = Enum.IsDefined(TextColor) ? TextColor : Glass.GlassText.AsItIs,
        DarkPagePercent = Math.Clamp(DarkPagePercent, 0, EffectPreferences.MostDarkPage),
        DarkTextPercent = Math.Clamp(DarkTextPercent, EffectPreferences.LeastDarkText, 100),
        DarkColorsPercent = Math.Clamp(DarkColorsPercent, 0, 100),
        ShadowPercent = Math.Clamp(ShadowPercent, 0, 100),
        ShadowWidth = Math.Clamp(ShadowWidth, TextShadowLevels.Thinnest, TextShadowLevels.Widest),
    };

    internal AppGlassLevels WithGlassOf(AppGlassLevels other) => this with
    {
        BackgroundPercent = other.BackgroundPercent, TextPercent = other.TextPercent, FadeRangePercent = other.FadeRangePercent,
        TextColor = other.TextColor, ShadowPercent = other.ShadowPercent, ShadowWidth = other.ShadowWidth,
    };

    internal AppGlassLevels WithDarkOf(AppGlassLevels other) => this with
    {
        DarkPagePercent = other.DarkPagePercent, DarkTextPercent = other.DarkTextPercent, DarkColorsPercent = other.DarkColorsPercent,
    };

    internal Glass.ContentLevels Levels =>
        new(BackgroundPercent / 100f, TextPercent / 100f, FadeRangePercent / 100f, TextColor: TextColor, Shadow: ShadowPercent / 100f, ShadowWidth: ShadowWidth,
            DarkPage: DarkPagePercent / 100f, DarkText: DarkTextPercent / 100f, DarkColors: DarkColorsPercent / 100f);
}

// The shadow behind Glass's text: how solid it is (ShadowPercent, 0 for none) and how far it reaches, in pixels at 100%
// scaling.
internal static class TextShadowLevels
{
    internal const int Thinnest = 1, Width = 3, Widest = 8;
}
