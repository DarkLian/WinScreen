namespace WinScreen.Settings;

// On by default (plain Glass passes presses through its see-through page); a PC with no graphics card gets plain Glass.
public sealed record EffectPreferences
{
    public bool LayeredGlass { get; init; } = true;
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int FadeRangePercent { get; init; } = 100;
    // Apps with their own levels; every other app uses the ones above.
    public List<AppGlassLevels> Apps { get; init; } = [];

    internal EffectPreferences Validated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppGlassLevels>();
        foreach (AppGlassLevels? app in Apps ?? [])
            if (app is not null && BackgroundPreferences.ProgramKey(app.Program) is { Length: > 0 } program && seen.Add(program))
                apps.Add(app.Validated() with { Program = program });
        return this with
        {
            BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
            TextPercent = Math.Clamp(TextPercent, 20, 100),
            FadeRangePercent = Math.Clamp(FadeRangePercent, 1, 100),
            Apps = apps,
        };
    }

    internal Glass.ContentLevels Levels => new(BackgroundPercent / 100f, TextPercent / 100f, FadeRangePercent / 100f);

    internal AppGlassLevels? OwnOf(string program)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        return Apps.Find(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase));
    }

    internal Glass.ContentLevels LevelsFor(string program) => OwnOf(program)?.Levels ?? Levels;

    // The app's own levels, or (with null) back to the ones every app has.
    internal EffectPreferences With(string program, AppGlassLevels? own)
    {
        string key = BackgroundPreferences.ProgramKey(program);
        var apps = Apps.Where(app => !string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own is not null && key.Length > 0) apps.Add(own.Validated() with { Program = key });
        return this with { Apps = apps };
    }

    internal AppGlassLevels LevelsOf(string program) => OwnOf(program) ?? new AppGlassLevels
    {
        Program = BackgroundPreferences.ProgramKey(program),
        BackgroundPercent = BackgroundPercent, TextPercent = TextPercent, FadeRangePercent = FadeRangePercent,
    };
}

public sealed record AppGlassLevels
{
    public string Program { get; init; } = "";
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int FadeRangePercent { get; init; } = 100;

    internal AppGlassLevels Validated() => this with
    {
        Program = Program?.Trim() ?? "",
        BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
        TextPercent = Math.Clamp(TextPercent, 20, 100),
        FadeRangePercent = Math.Clamp(FadeRangePercent, 1, 100),
    };

    internal Glass.ContentLevels Levels => new(BackgroundPercent / 100f, TextPercent / 100f, FadeRangePercent / 100f);
}
