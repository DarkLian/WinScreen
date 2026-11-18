namespace WinScreen.Settings;

public enum AnchorSide { Right, Left, NearWindow }
// New looks go at the end, so saved choices keep their meaning.
public enum AnchorLook { Dark, Light, NoBackground }

public sealed record AnchorPreferences
{
    public const int DefaultLimit = 8;

    public bool Automatic { get; init; }
    public List<string> OnlyPrograms { get; init; } = [];
    public List<string> ExcludedPrograms { get; init; } = [];
    public AnchorSide Side { get; init; }
    public int MaxAutomatic { get; init; } = DefaultLimit;
    public int Size { get; init; } = 48;
    public AnchorLook Style { get; init; }
    public bool RecencyEmphasis { get; init; } = true;
    public bool AvoidContent { get; init; }
    public bool HoverPreview { get; init; } = true;
    public bool OffTaskbar { get; init; }
    public bool EveryDesktop { get; init; } = true;

    internal AnchorPreferences Validated() => this with
    {
        OnlyPrograms = Clean(OnlyPrograms),
        ExcludedPrograms = Clean(ExcludedPrograms),
        Side = Enum.IsDefined(Side) ? Side : AnchorSide.Right,
        MaxAutomatic = Math.Clamp(MaxAutomatic, 1, 10),
        Size = Math.Clamp(Size, 32, 72),
        Style = Enum.IsDefined(Style) ? Style : AnchorLook.Dark,
    };

    private static List<string> Clean(List<string>? names) =>
        [.. (names ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())];

    internal bool Allows(string program) => !ExcludedPrograms.Any(name => Same(name, program)) &&
        (OnlyPrograms.Count == 0 || OnlyPrograms.Any(name => Same(name, program)));

    private static bool Same(string listed, string program) =>
        string.Equals(Plain(listed), Plain(program), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
