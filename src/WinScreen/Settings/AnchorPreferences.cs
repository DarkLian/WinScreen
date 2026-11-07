namespace WinScreen.Settings;

public sealed record AnchorPreferences
{
    public bool Automatic { get; init; }
    public List<string> OnlyPrograms { get; init; } = [];
    public List<string> ExcludedPrograms { get; init; } = [];

    internal AnchorPreferences Validated() => this with
    {
        OnlyPrograms = Clean(OnlyPrograms),
        ExcludedPrograms = Clean(ExcludedPrograms),
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
