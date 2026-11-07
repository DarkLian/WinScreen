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

    [STAThread]
    private static void Main()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUpdateFastCallback(_ => RunningInstance.Close())
            .OnBeforeUninstallFastCallback(_ =>
            {
                RunningInstance.Close();
                new StartWithWindows().IsOn = false;
            })
            .Run();

        ApplicationConfiguration.Initialize();
        string settingsPath = Path.Combine(DataFolder, "settings.json");
        AppSettings settings = AppSettings.Load(settingsPath);
        UiText.Initialize(settings.Language);

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
}
