namespace WinScreen.Tray;

public enum AppRole
{
    Ordinary,
    Elevated,

    // No tray icon, hotkeys or Settings of its own: the ordinary copy has those and hands it what is for administrator
    // apps' windows.
    Helper,
}

internal static class AppRoles
{
    public const string HelperArgument = Windows.RecoveryWatcher.HelperRole;

    public const string SingleArgument = "--single-copy";

    // Windows starts the helper with the person's own environment, so a data folder elsewhere (tests) is passed this
    // way.
    public const string DataFolderArgument = "--data-folder";

    public const string DataFolderVariable = "WINSCREEN_DATA";

    internal static string? DataFolderFrom(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(DataFolderArgument);
        return at >= 0 && at + 1 < arguments.Count && Path.IsPathFullyQualified(arguments[at + 1]) ? arguments[at + 1] : null;
    }

    // Only a copy Windows elevated from a split token can be the helper, since only then can an ordinary copy run
    // beside it.
    internal static AppRole Choose(bool selfElevated, bool splitToken, bool runAsAdministrator, bool helperWanted, bool singleAsked)
    {
        if (!selfElevated) return AppRole.Ordinary;
        return runAsAdministrator && helperWanted && splitToken && !singleAsked ? AppRole.Helper : AppRole.Elevated;
    }

    internal static bool Reaches(AppRole role, bool targetElevated) => role switch
    {
        AppRole.Ordinary => !targetElevated,
        AppRole.Elevated => true,
        AppRole.Helper => targetElevated,
        _ => false,
    };

    internal static bool WantsHelper(AppRole role, bool runAsAdministrator, bool helperWanted, bool canElevate) =>
        role == AppRole.Ordinary && runAsAdministrator && helperWanted && canElevate;

    // The ordinary and the elevated copy share one, so only one of them runs.
    internal static string MutexName(AppRole role) =>
        role == AppRole.Helper ? @"Local\WinScreen.AdministratorHelper" : Hotkeys.RunningInstance.MutexName;

    internal static string RecoveryMutexName(AppRole role) =>
        role == AppRole.Helper ? @"Local\WinScreen.Recovery.AdministratorHelper" : @"Local\WinScreen.Recovery";

    // The command line looks for the ordinary copy's title.
    internal static string HostTitle(AppRole role) =>
        role == AppRole.Helper ? HelperHostTitle : Hotkeys.HotkeyHost.WindowTitle;

    public const string HelperHostTitle = "WinScreen.AdministratorHelper";

    internal static string JournalFile(AppRole role) =>
        role == AppRole.Helper ? "changed-windows-administrator.json" : "changed-windows.json";

    internal static string SoundJournalFile(AppRole role) =>
        role == AppRole.Helper ? "muted-programs-administrator.json" : "muted-programs.json";

    internal static string LogFile(AppRole role) =>
        role == AppRole.Helper ? Diagnostics.DiagnosticLog.HelperFileName : Diagnostics.DiagnosticLog.FileName;

    internal static AppRole WatcherRole(IReadOnlyList<string> arguments) =>
        arguments.Contains(HelperArgument) ? AppRole.Helper : AppRole.Ordinary;
}
