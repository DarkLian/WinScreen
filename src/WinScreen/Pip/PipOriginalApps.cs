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

    /// <summary>
    /// A window of a Java desktop app (IntelliJ IDEA, Rider and the like). Java ignores clicks while its window is
    /// hidden past the edge of the screen, so a view of one always shows its original on a click, listed or not.
    /// </summary>
    internal static bool IsJavaWindow(nint window) => IsJavaClass(Windows.WindowInfo.ClassName(window));

    /// <summary>Java's own window classes: SunAwtFrame for a main window, SunAwtDialog and the rest (see the app profiles).</summary>
    internal static bool IsJavaClass(string windowClass) => Compatibility.AppProfiles.ShowsOriginalOnClick(windowClass);
}
