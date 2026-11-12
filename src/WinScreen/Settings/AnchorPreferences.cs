namespace WinScreen.Settings;

public enum AnchorSide { Right, Left, NearWindow }
/// <summary>How an Anchor looks. New looks go at the end, so saved choices keep their meaning.</summary>
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
    /// <summary>Resting the pointer on an Anchor shows its window's live picture beside it.</summary>
    public bool HoverPreview { get; init; } = true;
    /// <summary>A window with an Anchor the person added has no taskbar button while the Anchor is there.</summary>
    public bool OffTaskbar { get; init; }

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

    /// <summary>Whether this program's windows may get an automatic Anchor.</summary>
    internal bool Allows(string program) => !ExcludedPrograms.Any(name => Same(name, program)) &&
        (OnlyPrograms.Count == 0 || OnlyPrograms.Any(name => Same(name, program)));

    private static bool Same(string listed, string program) =>
        string.Equals(Plain(listed), Plain(program), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
