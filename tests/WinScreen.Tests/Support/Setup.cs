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
    /// A tray app made in a test starts with Windows through a key and task list of its own. With the real ones, saving
    /// Settings on a PC whose WinScreen runs as administrator wrote the person's sign-in entry and tried to remove their
    /// task (23 Nov 2026).
    /// </summary>
    [ModuleInitializer]
    internal static void KeepSignInStartToTests() =>
        WinScreen.Tray.TrayApplication.NewStartup = () => new WinScreen.Tray.StartWithWindows(@"Software\WinScreenTests\TrayStartup", NoTasks.Instance);
}
