namespace WinScreen.Tray;

/// <summary>What this copy of WinScreen is (10-2): which windows it acts on, and what it shows.</summary>
public enum AppRole
{
    /// <summary>Not elevated: every window except administrator apps'. With Run as administrator it has a helper for those.</summary>
    Ordinary,

    /// <summary>The one copy running as administrator, acting on every window (before 10-2, and the fallback).</summary>
    Elevated,

    /// <summary>
    /// The administrator helper: elevated, beside an ordinary copy, acting only on administrator apps' windows. No tray
    /// icon, hotkeys or Settings of its own: the ordinary copy has those and hands it what is for those windows.
    /// </summary>
    Helper,
}

/// <summary>The facts that go with each role, in one place.</summary>
internal static class AppRoles
{
    /// <summary>Given to a copy started to be the helper (by the sign-in task or the ordinary copy).</summary>
    public const string HelperArgument = Windows.RecoveryWatcher.HelperRole;

    /// <summary>Asks for the one elevated copy of before, even with the helper chosen: the helper's fallback.</summary>
    public const string SingleArgument = "--single-copy";

    /// <summary>
    /// Followed by a folder: where the settings and journals are, for a copy whose data isn't in the usual place (tests).
    /// The helper is started by Windows with the person's own environment, so it is told this way.
    /// </summary>
    public const string DataFolderArgument = "--data-folder";

    /// <summary>The environment variable that moves the data folder.</summary>
    public const string DataFolderVariable = "WINSCREEN_DATA";

    /// <summary>The data folder given on the command line, if any.</summary>
    internal static string? DataFolderFrom(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(DataFolderArgument);
        return at >= 0 && at + 1 < arguments.Count && Path.IsPathFullyQualified(arguments[at + 1]) ? arguments[at + 1] : null;
    }

    /// <summary>
    /// The role a copy takes at start. Only a copy Windows elevated from a split token can be the helper, since only then
    /// can an ordinary copy run beside it: with UAC off, or a standard account, it stays as before.
    /// </summary>
    /// <param name="helperWanted">The JSON-only choice <c>AdministratorHelper</c>; off keeps the one elevated copy.</param>
    internal static AppRole Choose(bool selfElevated, bool splitToken, bool runAsAdministrator, bool helperWanted, bool singleAsked)
    {
        if (!selfElevated) return AppRole.Ordinary;
        return runAsAdministrator && helperWanted && splitToken && !singleAsked ? AppRole.Helper : AppRole.Elevated;
    }

    /// <summary>Whether a copy in this role acts on a window of a program that runs as administrator, or one that doesn't.</summary>
    internal static bool Reaches(AppRole role, bool targetElevated) => role switch
    {
        AppRole.Ordinary => !targetElevated,
        AppRole.Elevated => true,
        AppRole.Helper => targetElevated,
        _ => false,
    };

    /// <summary>An ordinary copy starts a helper for administrator apps' windows.</summary>
    internal static bool WantsHelper(AppRole role, bool runAsAdministrator, bool helperWanted, bool canElevate) =>
        role == AppRole.Ordinary && runAsAdministrator && helperWanted && canElevate;

    /// <summary>The single-copy lock: the ordinary and the elevated copy share one, so only one of them runs.</summary>
    internal static string MutexName(AppRole role) =>
        role == AppRole.Helper ? @"Local\WinScreen.AdministratorHelper" : Hotkeys.RunningInstance.MutexName;

    /// <summary>The lock taken while recovering windows from a journal; one per journal.</summary>
    internal static string RecoveryMutexName(AppRole role) =>
        role == AppRole.Helper ? @"Local\WinScreen.Recovery.AdministratorHelper" : @"Local\WinScreen.Recovery";

    /// <summary>The title of the hidden window that takes hotkeys and messages; the command line looks for the ordinary one.</summary>
    internal static string HostTitle(AppRole role) =>
        role == AppRole.Helper ? HelperHostTitle : Hotkeys.HotkeyHost.WindowTitle;

    public const string HelperHostTitle = "WinScreen.AdministratorHelper";

    /// <summary>What the copy changed in other programs' windows, so a crash can be undone: each copy its own.</summary>
    internal static string JournalFile(AppRole role) =>
        role == AppRole.Helper ? "changed-windows-administrator.json" : "changed-windows.json";

    /// <summary>The programs the copy muted.</summary>
    internal static string SoundJournalFile(AppRole role) =>
        role == AppRole.Helper ? "muted-programs-administrator.json" : "muted-programs.json";

    /// <summary>The log: one writer per file.</summary>
    internal static string LogFile(AppRole role) =>
        role == AppRole.Helper ? Diagnostics.DiagnosticLog.HelperFileName : Diagnostics.DiagnosticLog.FileName;

    /// <summary>The role a recovery watcher works for, from the arguments it was started with after its parent's id.</summary>
    internal static AppRole WatcherRole(IReadOnlyList<string> arguments) =>
        arguments.Contains(HelperArgument) ? AppRole.Helper : AppRole.Ordinary;
}
