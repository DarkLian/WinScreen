namespace WinScreen.Settings;

// What Glass shows behind a window's text: one background for every app, and apps of the person's choosing with their
// own. An app listed without a file shows none, whatever every app has.
public sealed record BackgroundPreferences
{
    public BackgroundChoice Everywhere { get; init; } = new();
    public List<AppBackground> Apps { get; init; } = [];

    internal BackgroundPreferences Validated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppBackground>();
        foreach (AppBackground? app in Apps ?? [])
        {
            if (app is null || ProgramKey(app.Program) is not { Length: > 0 } program || !seen.Add(program)) continue;
            apps.Add(new AppBackground { Program = program, Background = (app.Background ?? new()).Validated() });
        }
        return new BackgroundPreferences { Everywhere = (Everywhere ?? new()).Validated(), Apps = apps };
    }

    internal BackgroundChoice? OwnOf(string program)
    {
        string key = ProgramKey(program);
        return Apps.Find(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase))?.Background;
    }

    internal Glass.BackdropChoice? For(string program) => (OwnOf(program) ?? Everywhere).Backdrop;

    internal static string ProgramKey(string? program)
    {
        string name = program?.Trim() ?? "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }
}

public sealed record BackgroundChoice
{
    public string File { get; init; } = "";
    public Glass.BackdropFit Fit { get; init; } = Glass.BackdropFit.Fill;

    // Percent; none unless the person turns it up.
    public int Volume { get; init; }

    internal BackgroundChoice Validated() => this with
    {
        File = File?.Trim() ?? "",
        Fit = Enum.IsDefined(Fit) ? Fit : Glass.BackdropFit.Fill,
        Volume = Math.Clamp(Volume, 0, 100),
    };

    internal Glass.BackdropChoice? Backdrop => string.IsNullOrEmpty(File) ? null : new(File, Fit, Volume);
}

public sealed record AppBackground
{
    public string Program { get; init; } = "";
    public BackgroundChoice Background { get; init; } = new();
}
