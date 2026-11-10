namespace WinScreen.Pip;

/// <summary>
/// Programs that don't work well through a view. Nothing is sent to them: a click on a view of one shows its
/// original to be used as it is, and the original is hidden again afterwards.
/// </summary>
internal static class PipOriginalApps
{
    private static volatile string[] _programs = [];

    /// <summary>What Settings says now, by program name without ".exe".</summary>
    internal static IReadOnlyList<string> Programs
    {
        get => _programs;
        set => _programs = value.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    internal static bool Includes(string program) =>
        program.Length > 0 && _programs.Contains(program, StringComparer.OrdinalIgnoreCase);
}
