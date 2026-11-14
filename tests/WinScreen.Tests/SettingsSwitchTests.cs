using System.Reflection;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Every plain on/off setting is shown by its switch and read back from it (roadmap P2-9).</summary>
[Trait("Category", "RealWindows")]
public sealed class SettingsSwitchTests
{
    private static readonly string[] Plain =
    [
        nameof(AppSettings.ShowToolbar), nameof(AppSettings.ShowNotifications), nameof(AppSettings.MenuTransparency),
        nameof(AppSettings.AutomaticUpdates), nameof(AppSettings.ExperimentalFeatures), nameof(AppSettings.RunAsAdministrator),
        nameof(AppSettings.PauseInFullScreen), nameof(AppSettings.AllowPipInFullScreen), nameof(AppSettings.ModifierDrag),
        nameof(AppSettings.SnapWhileDragging), nameof(AppSettings.WindowMenu), nameof(AppSettings.PausePinWhenMaximized),
        nameof(AppSettings.OpaqueOnHover), nameof(AppSettings.HighlightPinned), nameof(AppSettings.PipAutoMove),
        nameof(AppSettings.PipMiddleClickScroll), nameof(AppSettings.OpacityWheel), nameof(AppSettings.DetectAlreadyDark),
        nameof(AppSettings.HideCaptureBorder),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_switch_shows_its_setting_and_gives_it_back(bool on)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var settings = new AppSettings { Language = "en" };
            foreach (string name in Plain) typeof(AppSettings).GetProperty(name)!.SetValue(settings, on);
            using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            // Some switches are unavailable on some PCs (administrator rights, updates); they still show and keep the value.
            var draft = (AppSettings)typeof(SettingsWindow).GetMethod("Draft", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [settings.Clone()])!;
            string[] wrong = Plain.Where(name => (bool)typeof(AppSettings).GetProperty(name)!.GetValue(draft)! != on).ToArray();
            Assert.True(wrong.Length == 0, "Not given back as set: " + string.Join(", ", wrong));
        });
    }
}
