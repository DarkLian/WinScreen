namespace WinScreen.Settings;

// What Glass shows behind a window's text: one background for every app, and apps of the person's choosing with their
// own. An app listed without a file shows none, whatever every app has.
public sealed record BackgroundPreferences
{
    public BackgroundChoice Everywhere { get; init; } = new();
    public List<AppBackground> Apps { get; init; } = [];

    // As a desktop wallpaper app does it: sound only while that app is in front, and videos held still behind a
    // full-screen game or while Windows saves battery.
    public bool SoundOnlyInFront { get; init; } = true;
    public bool PauseInFullScreen { get; init; } = true;
    public bool PauseOnBatterySaver { get; init; } = true;

    internal BackgroundPreferences Validated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppBackground>();
        foreach (AppBackground? app in Apps ?? [])
        {
            if (app is null || ProgramKey(app.Program) is not { Length: > 0 } program || !seen.Add(program)) continue;
            apps.Add(new AppBackground { Program = program, Background = (app.Background ?? new()).Validated() });
        }
        return this with { Everywhere = (Everywhere ?? new()).Validated(), Apps = apps };
    }

    internal BackgroundChoice? OwnOf(string program)
    {
        string key = ProgramKey(program);
        return Apps.Find(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase))?.Background;
    }

    internal Glass.BackdropSet? For(string program) => (OwnOf(program) ?? Everywhere).Backdrop;

    // The app's own background, or Every app's when it has none, changed.
    internal BackgroundPreferences Change(string program, Func<BackgroundChoice, BackgroundChoice> change)
    {
        string key = ProgramKey(program);
        if (OwnOf(key) is null) return this with { Everywhere = change(Everywhere).Validated() };
        return this with
        {
            Apps = Apps.Select(app => string.Equals(app.Program, key, StringComparison.OrdinalIgnoreCase)
                ? app with { Background = change(app.Background).Validated() } : app).ToList(),
        };
    }

    internal static string ProgramKey(string? program)
    {
        string name = program?.Trim() ?? "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }
}

// One picture or video (a file, or a link to one) and how it shows. Volume, Speed and Brightness are percents.
public sealed record BackgroundItem
{
    public const int SlowestSpeed = 25, FastestSpeed = 400, DimmestBrightness = 10;

    public string File { get; init; } = "";
    public Glass.BackdropFit Fit { get; init; } = Glass.BackdropFit.Fill;
    public int Volume { get; init; }
    public int Speed { get; init; } = 100;
    public int Brightness { get; init; } = 100;

    internal BackgroundItem Validated() => this with
    {
        File = File?.Trim() ?? "",
        Fit = Enum.IsDefined(Fit) ? Fit : Glass.BackdropFit.Fill,
        Volume = Math.Clamp(Volume, 0, 100),
        Speed = Math.Clamp(Speed == 0 ? 100 : Speed, SlowestSpeed, FastestSpeed),
        Brightness = Math.Clamp(Brightness == 0 ? 100 : Brightness, DimmestBrightness, 100),
    };

    internal Glass.BackdropChoice Backdrop => new(File, Fit, Volume, Speed, Brightness);
}

// A background is one picture or video, or several taking turns. The first is kept in File, Fit, Volume, Speed and
// Brightness, as before sets, so an older copy of WinScreen still shows it; the rest are in More.
public sealed record BackgroundChoice
{
    public string File { get; init; } = "";
    public Glass.BackdropFit Fit { get; init; } = Glass.BackdropFit.Fill;

    // Percent; none unless the person turns it up.
    public int Volume { get; init; }
    public int Speed { get; init; } = 100;
    public int Brightness { get; init; } = 100;
    public List<BackgroundItem> More { get; init; } = [];
    public Glass.BackdropOrder Order { get; init; }

    // How long each picture of a set shows; also each video, when Switch is Timed.
    public int PictureMinutes { get; init; } = 5;
    public Glass.BackdropSwitch Switch { get; init; }

    internal const int MostPictureMinutes = 24 * 60;

    internal IReadOnlyList<BackgroundItem> Items => File.Length == 0 ? [] :
        [new BackgroundItem { File = File, Fit = Fit, Volume = Volume, Speed = Speed, Brightness = Brightness }, .. More];

    internal BackgroundChoice WithItems(IEnumerable<BackgroundItem> items)
    {
        BackgroundItem[] all = [.. items.Where(item => !string.IsNullOrWhiteSpace(item.File))];
        BackgroundItem first = all.FirstOrDefault() ?? new BackgroundItem();
        return this with
        {
            File = first.File, Fit = first.Fit, Volume = first.Volume, Speed = first.Speed, Brightness = first.Brightness, More = [.. all.Skip(1)],
        };
    }

    internal BackgroundChoice Validated()
    {
        BackgroundChoice tidied = WithItems(new[] { new BackgroundItem { File = File ?? "", Fit = Fit, Volume = Volume, Speed = Speed, Brightness = Brightness } }
            .Concat((More ?? []).Where(item => item is not null)).Select(item => item.Validated()));
        return tidied with
        {
            Order = Enum.IsDefined(Order) ? Order : Glass.BackdropOrder.InOrder,
            Switch = Enum.IsDefined(Switch) ? Switch : Glass.BackdropSwitch.VideoEnds,
            PictureMinutes = Math.Clamp(PictureMinutes, 1, MostPictureMinutes),
        };
    }

    internal Glass.BackdropSet? Backdrop => Items.Count == 0 ? null : new([.. Items.Select(item => item.Backdrop)], Order, PictureMinutes, Switch);

    public bool Equals(BackgroundChoice? other) => other is not null && File == other.File && Fit == other.Fit && Volume == other.Volume &&
        Speed == other.Speed && Brightness == other.Brightness && Order == other.Order && PictureMinutes == other.PictureMinutes && Switch == other.Switch &&
        (More ?? []).SequenceEqual(other.More ?? []);

    public override int GetHashCode() => HashCode.Combine(File, Fit, Volume, Speed, Brightness, Order, PictureMinutes, More?.Count ?? 0);
}

public sealed record AppBackground
{
    public string Program { get; init; } = "";
    public BackgroundChoice Background { get; init; } = new();
}
