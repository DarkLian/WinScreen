namespace WinScreen.Pip;

internal static class PipOriginalApps
{
    private static volatile string[] _programs = [];

    internal static IReadOnlyList<string> Programs
    {
        get => _programs;
        set => _programs = value.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    internal static bool Includes(string program) =>
        program.Length > 0 && _programs.Contains(program, StringComparer.OrdinalIgnoreCase);

    // Java ignores clicks while its window is hidden past the screen's edge, so a view of one always shows its original
    // on a click, listed or not.
    internal static bool IsJavaWindow(nint window) => IsJavaClass(Windows.WindowInfo.ClassName(window));

    internal static bool IsJavaClass(string windowClass) => Compatibility.AppProfiles.ShowsOriginalOnClick(windowClass);
}
