using System.Reflection;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Dark mode and Glass change a window's colours in ways that can't be combined. A window has at most one of them,
/// whichever kind of each is chosen in Settings, and a failed or refused switch leaves the one it had.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class EffectExclusivityTests
{
    private static void Run(TrayApplication app, AppCommand command, nint window) =>
        typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [command, window]);

    private static bool IsOn(TrayApplication app, AppCommand command, nint window) =>
        (bool)typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [command, window])!;

    private static void Settle() { for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); } }

    [Fact]
    public void Dark_and_Glass_are_never_on_together() => Check(pictureSafeDark: false, layeredGlass: false);

    /// <summary>The same with the kinds that redraw the window's picture, which need a graphics card.</summary>
    [GraphicsCardFact]
    public void Picture_keeping_Dark_and_layered_Glass_are_never_on_together_either()
    {
        Check(pictureSafeDark: true, layeredGlass: false);
        Check(pictureSafeDark: false, layeredGlass: true);
        Check(pictureSafeDark: true, layeredGlass: true);
    }

    private static void Check(bool pictureSafeDark, bool layeredGlass)
    {
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "F0F0F0");
        string folder = Directory.CreateTempSubdirectory("winscreen-looks-").FullName;
        try
        {
            // Dark mode's filter works only on the first thread of a process that uses it: the one Dark mode's tests share.
            DarkModeTests.OnUiThread(_ =>
            {
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false, ExperimentalFeatures = true,
                    DetectAlreadyDark = false, Effects = new EffectPreferences { PictureSafeDark = pictureSafeDark, LayeredGlass = layeredGlass },
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(new WindowController([]), settings, Path.Combine(folder, "settings.json"));
                nint window = target.Handle;
                try
                {
                    void Expect(bool dark, bool glass, string when)
                    {
                        Settle();
                        string kinds = $"picture-keeping Dark {pictureSafeDark}, layered Glass {layeredGlass}";
                        Assert.True(IsOn(app, AppCommand.ToggleDark, window) == dark, $"{when} ({kinds}): Dark should be {(dark ? "on" : "off")}.");
                        Assert.True(IsOn(app, AppCommand.ToggleGlass, window) == glass, $"{when} ({kinds}): Glass should be {(glass ? "on" : "off")}.");
                    }
                    Run(app, AppCommand.ToggleDark, window);
                    Expect(true, false, "Dark");
                    Run(app, AppCommand.ToggleGlass, window);
                    Expect(false, true, "Glass after Dark");
                    Run(app, AppCommand.ToggleDark, window);
                    Expect(true, false, "Dark after Glass");
                    Run(app, AppCommand.ToggleDark, window);
                    Expect(false, false, "Dark off");
                    Run(app, AppCommand.ToggleGlass, window);
                    Run(app, AppCommand.ToggleGlass, window);
                    Expect(false, false, "Glass on and off");
                }
                finally { Run(app, AppCommand.RestoreWindow, window); Settle(); app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>
    /// Dark refused for a window that is dark already leaves its Glass on: the Glass is put back rather than lost for
    /// a change that didn't happen.
    /// </summary>
    [Fact]
    public void Glass_stays_when_Dark_is_refused()
    {
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "202020");
        string folder = Directory.CreateTempSubdirectory("winscreen-looks-").FullName;
        try
        {
            DarkModeTests.OnUiThread(_ =>
            {
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false, ExperimentalFeatures = true,
                    DetectAlreadyDark = true, Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(new WindowController([]), settings, Path.Combine(folder, "settings.json"));
                nint window = target.Handle;
                try
                {
                    Run(app, AppCommand.ToggleGlass, window);
                    Settle();
                    Assert.True(IsOn(app, AppCommand.ToggleGlass, window));
                    Run(app, AppCommand.ToggleDark, window); // The window is dark already, so Dark says so and does nothing.
                    Settle();
                    Assert.False(IsOn(app, AppCommand.ToggleDark, window));
                    Assert.True(IsOn(app, AppCommand.ToggleGlass, window), "Glass was lost for a Dark mode that never came.");
                }
                finally { Run(app, AppCommand.RestoreWindow, window); Settle(); app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
