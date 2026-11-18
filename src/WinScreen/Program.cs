using static WinScreen.Localization.UiText;
using Velopack;
using WinScreen.Diagnostics;
using WinScreen.Hotkeys;
using WinScreen.Localization;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen;

internal static class Program
{
    private static string DataFolder =>
        Environment.GetEnvironmentVariable(AppRoles.DataFolderVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinScreen");

    /// <summary>What a copy in this role muted, until it is unmuted (<see cref="ProgramSound"/>).</summary>
    private static string SoundJournal(AppRole role) => Path.Combine(DataFolder, AppRoles.SoundJournalFile(role));

    private static ChangeJournal Journal(AppRole role) =>
        new(Path.Combine(DataFolder, AppRoles.JournalFile(role)), AppRoles.RecoveryMutexName(role));

    [STAThread]
    private static void Main(string[] arguments)
    {
        // A helper started for a copy whose data is elsewhere (10-2); its own watcher inherits this.
        if (AppRoles.DataFolderFrom(arguments) is { } dataFolder) Environment.SetEnvironmentVariable(AppRoles.DataFolderVariable, dataFolder);
        if (RecoveryWatcher.ParentFrom(arguments) is { } parent)
        {
            AppRole watched = AppRoles.WatcherRole(arguments);
            RecoveryWatcher.WatchUntilRecovered(parent, () => RecoverLeftWindows(watched), AppRoles.MutexName(watched),
                AppRoles.RecoveryMutexName(watched));
            return;
        }
        if (Elevation.WaitingFor(arguments) is { } previous) Elevation.WaitForExit(previous);

        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUpdateFastCallback(_ => RunningInstance.Close())
            .OnBeforeUninstallFastCallback(_ =>
            {
                // First, so WinScreen stops starting at sign-in even if the running copy won't close.
                try { new StartWithWindows().IsOn = false; }
                catch (IOException caught) { Diagnostics.Log.Swallowed(caught); } // A task only an administrator can remove; it fails harmlessly once the app is gone.
                RunningInstance.Close();
            })
            .Run();

        ApplicationConfiguration.Initialize();
        string settingsPath = Path.Combine(DataFolder, "settings.json");
        AppSettings settings = AppSettings.Load(settingsPath);
        Settings.ProgramIcons.File = Path.Combine(DataFolder, "program-paths.json");
        UiText.Initialize(settings.Language);
        MenuStyle.UseTheme(settings.Theme);
        Native.Translucency.Allowed = settings.MenuTransparency;

        // A command for the running copy (or the list of commands): answered here, and nothing else starts.
        CommandLineRequest request = CommandLine.Parse(arguments);
        if (request is not CommandLineRequest.None)
        {
            Environment.ExitCode = (int)CommandLine.Handle(request);
            return;
        }

        if (Environment.OSVersion.Version.Build < Native.WindowsBuild.Minimum)
        {
            Settings.ThemedMessage.Show(null, T("errors.app.windowsTooOld"), "WinScreen");
            return;
        }

        // What this copy is (10-2): as the person, the one elevated copy of before, or the administrator helper.
        AppRole role = AppRoles.Choose(Native.ProcessInfo.SelfElevated, Native.ProcessInfo.SplitToken, settings.RunAsAdministrator,
            settings.AdministratorHelper, arguments.Contains(AppRoles.SingleArgument));
        using var singleInstance = new Mutex(true, AppRoles.MutexName(role), out bool isFirstCopy);
        if (!isFirstCopy)
        {
            // A second helper (the sign-in task and the ordinary copy both started one) just goes.
            if (role != AppRole.Helper) Settings.ThemedMessage.Show(null, T("errors.app.alreadyRunning"), "WinScreen");
            return;
        }

        // Asked once: a copy started as administrator that Windows still ran without the rights carries on as it is.
        // A standard account isn't asked: Windows would want another person's password and run WinScreen as them.
        // With the helper chosen this copy stays as it is and starts the helper instead (TrayApplication).
        if (settings.RunAsAdministrator && !Native.ProcessInfo.SelfElevated && Native.ProcessInfo.CanElevate &&
            !AppRoles.WantsHelper(role, settings.RunAsAdministrator, settings.AdministratorHelper, Native.ProcessInfo.CanElevate) &&
            !arguments.Contains(Elevation.ElevatedArgument) && Elevation.StartElevatedCopy())
            return;

        // Only the copy that carries on writes the log: not a second copy, nor one handing over to an elevated one.
        Log.Start(DataFolder, role == AppRole.Helper ? "helper" : "app", AppRoles.LogFile(role));
        StartupFacts.LogInBackground();

        var windows = new WindowController(settings.ExcludedPrograms, Journal(role));
        windows.RecoverFromJournal();
        ProgramSound.Recover(Native.AudioSessions.Instance, SoundJournal(role));
        var sound = new ProgramSound(Native.AudioSessions.Instance, SoundJournal(role));

        // A crash must not leave someone's window see-through and impossible to click, nor a program muted.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            windows.RestoreAll();
            ProgramSound.Recover(Native.AudioSessions.Instance, SoundJournal(role));
            Log.Event("app.exited");
            Log.Flush(TimeSpan.FromMilliseconds(500));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            windows.RestoreAll();
            ProgramSound.Recover(Native.AudioSessions.Instance, SoundJournal(role));
            if (e.ExceptionObject is Exception crash) Log.Error("app.crashed", crash);
            Log.Flush(TimeSpan.FromSeconds(1));
        };
        Application.ThreadException += (_, e) =>
        {
            windows.RestoreAll();
            ProgramSound.Recover(Native.AudioSessions.Instance, SoundJournal(role));
            Log.Error("app.stopped", e.Exception);
            Log.Flush(TimeSpan.FromSeconds(1));
            MessageBox.Show(e.Exception.Message, T("errors.app.unexpectedStop"), MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Application.Exit();
        };

        Application.Run(new TrayApplication(windows, settings, settingsPath, sound, Elevation.SettingsPage(arguments), role));
    }

    /// <summary>What the watcher does after the app was killed: the same recovery as the next start, for that copy's role.</summary>
    private static bool RecoverLeftWindows(AppRole role)
    {
        AppSettings settings = AppSettings.Load(Path.Combine(DataFolder, "settings.json"));
        UiText.Initialize(settings.Language);
        // Called only once that copy is gone, so the log has a single writer.
        Log.Start(DataFolder, "watcher", AppRoles.LogFile(role));
        var windows = new WindowController(settings.ExcludedPrograms, Journal(role));
        windows.RecoverFromJournal();
        // Sessions on a device that isn't there now wait for the next start; they don't keep the watcher going.
        ProgramSound.Recover(Native.AudioSessions.Instance, SoundJournal(role));
        bool done = !windows.RecoveryBlocked && windows.PendingRestores == 0;
        Log.Event("watcher.recovered", ("done", done), ("left", windows.PendingRestores));
        Log.Flush(TimeSpan.FromSeconds(1));
        return done;
    }
}
