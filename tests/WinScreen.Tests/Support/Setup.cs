using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinScreen.Tests.Support;

internal static class Setup
{
    /// <summary>Screen coordinates must be real pixels, as they are in WinScreen itself.</summary>
    [ModuleInitializer]
    internal static void UseRealPixels() => Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

    /// <summary>
    /// For every tray app a test makes: starting with Windows through a key and task list of its own. With the real ones,
    /// saving Settings on a PC whose WinScreen runs as administrator wrote the person's sign-in entry and tried to remove
    /// their task (23 Nov 2026). <see cref="AdministratorStartTests"/> checks that every test passes it.
    /// </summary>
    /// <summary>
    /// Before WinScreen is started on a test data folder: no notifications. Windows keeps one on the screen while it sees
    /// no input from a person, in front, and every later test that needs the focus failed (21 on 23 Nov 2026).
    /// </summary>
    internal static void QuietNotices(string dataFolder)
    {
        string file = Path.Combine(dataFolder, "settings.json");
        WinScreen.Settings.AppSettings settings = File.Exists(file) ? WinScreen.Settings.AppSettings.Load(file) : new() { WelcomeShown = true };
        if (settings.Notifications != WinScreen.Settings.NotificationMode.All) return;
        settings.Notifications = WinScreen.Settings.NotificationMode.Problems;
        settings.Save(file);
    }

    internal static WinScreen.Tray.StartWithWindows OwnStartup() =>
        new(@"Software\WinScreenTests\TrayStartup", NoTasks.Instance);
}
