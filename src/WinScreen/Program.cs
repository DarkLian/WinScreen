using static WinScreen.Localization.UiText;
using Velopack;
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

        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUpdateFastCallback(_ => RunningInstance.Close())
            .OnBeforeUninstallFastCallback(_ =>
            {
                // First, so WinScreen stops starting at sign-in even if the running copy won't close.
                new StartWithWindows().IsOn = false;
                RunningInstance.Close();
            })
            .Run();

        ApplicationConfiguration.Initialize();
        string settingsPath = Path.Combine(DataFolder, "settings.json");
        AppSettings settings = AppSettings.Load(settingsPath);
        UiText.Initialize(settings.Language);
        MenuStyle.UseTheme(settings.Theme);

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

        var windows = new WindowController(settings.ExcludedPrograms,
            new ChangeJournal(Path.Combine(DataFolder, "changed-windows.json")));
        windows.RecoverFromJournal();

        // A crash must not leave someone's window see-through and impossible to click.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => windows.RestoreAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => windows.RestoreAll();
        Application.ThreadException += (_, e) =>
        {
            windows.RestoreAll();
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
        var windows = new WindowController(settings.ExcludedPrograms, new ChangeJournal(Path.Combine(DataFolder, "changed-windows.json")));
        windows.RecoverFromJournal();
        return !windows.RecoveryBlocked && windows.PendingRestores == 0;
    }
}
