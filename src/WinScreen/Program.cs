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
        Environment.GetEnvironmentVariable("WINSCREEN_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinScreen");

    /// <summary>Windows 10 version 2004. Older builds lack what hiding views from screen capture needs.</summary>
    private const int MinimumBuild = 19041;

    [STAThread]
    private static void Main(string[] arguments)
    {
        if (RecoveryWatcher.ParentFrom(arguments) is { } parent)
        {
            RecoveryWatcher.WatchUntilRecovered(parent, RecoverLeftWindows);
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
                catch (IOException) { } // A task only an administrator can remove; it fails harmlessly once the app is gone.
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

        if (Environment.OSVersion.Version.Build < MinimumBuild)
        {
            MessageBox.Show(T("errors.app.windowsTooOld"), "WinScreen");
            return;
        }

        using var singleInstance = new Mutex(true, RunningInstance.MutexName, out bool isFirstCopy);
        if (!isFirstCopy)
        {
            MessageBox.Show(T("errors.app.alreadyRunning"), "WinScreen");
            return;
        }

        // Asked once: a copy started as administrator that Windows still ran without the rights carries on as it is.
        // A standard account isn't asked: Windows would want another person's password and run WinScreen as them.
        if (settings.RunAsAdministrator && !Native.ProcessInfo.SelfElevated && Native.ProcessInfo.CanElevate &&
            !arguments.Contains(Elevation.ElevatedArgument) && Elevation.StartElevatedCopy())
            return;

        // Only the copy that carries on writes the log: not a second copy, nor one handing over to an elevated one.
        Log.Start(DataFolder, "app");
        StartupFacts.LogInBackground();

        var windows = new WindowController(settings.ExcludedPrograms,
            new ChangeJournal(Path.Combine(DataFolder, "changed-windows.json")));
        windows.RecoverFromJournal();

        // A crash must not leave someone's window see-through and impossible to click.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            windows.RestoreAll();
            Log.Event("app.exited");
            Log.Flush(TimeSpan.FromMilliseconds(500));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            windows.RestoreAll();
            if (e.ExceptionObject is Exception crash) Log.Error("app.crashed", crash);
            Log.Flush(TimeSpan.FromSeconds(1));
        };
        Application.ThreadException += (_, e) =>
        {
            windows.RestoreAll();
            Log.Error("app.stopped", e.Exception);
            Log.Flush(TimeSpan.FromSeconds(1));
            MessageBox.Show(e.Exception.Message, T("errors.app.unexpectedStop"), MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Application.Exit();
        };

        Application.Run(new TrayApplication(windows, settings, settingsPath));
    }

    /// <summary>What the watcher does after the app was killed: the same recovery as the next start.</summary>
    private static bool RecoverLeftWindows()
    {
        AppSettings settings = AppSettings.Load(Path.Combine(DataFolder, "settings.json"));
        UiText.Initialize(settings.Language);
        // Called only once WinScreen itself is gone, so the log has a single writer.
        Log.Start(DataFolder, "watcher");
        var windows = new WindowController(settings.ExcludedPrograms, new ChangeJournal(Path.Combine(DataFolder, "changed-windows.json")));
        windows.RecoverFromJournal();
        bool done = !windows.RecoveryBlocked && windows.PendingRestores == 0;
        Log.Event("watcher.recovered", ("done", done), ("left", windows.PendingRestores));
        Log.Flush(TimeSpan.FromSeconds(1));
        return done;
    }
}
