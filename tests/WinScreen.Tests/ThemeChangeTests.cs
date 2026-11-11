using System.Reflection;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Changing between light and dark takes effect at once, without restarting WinScreen.</summary>
[Trait("Category", "RealWindows")]
public sealed class ThemeChangeTests
{
    [Fact]
    public void Saving_a_new_look_switches_it_and_reopens_Settings_on_the_same_page()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-theme-").FullName;
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                MenuStyle.UseTheme("light");
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false, Theme = "light",
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(new WindowController([]), settings, Path.Combine(folder, "settings.json"));
                try
                {
                    Image lightIcon = MenuParts.Icon(MenuParts.Pin)!;
                    typeof(TrayApplication).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [null, 0, false, null, false]);
                    SettingsWindow first = Current(app);
                    first.PageIndex = 4;
                    Pump();
                    Theme(first).SelectedIndex = 2; // Dark.
                    Assert.True(first.ApplyChanges());
                    Pump();

                    Assert.True(MenuStyle.Dark);
                    Assert.True(first.IsDisposed);
                    SettingsWindow second = Current(app);
                    Assert.False(second.IsDisposed);
                    Assert.Equal(4, second.PageIndex);
                    Assert.Equal(SettingsStyle.Page, second.BackColor);
                    Assert.NotSame(lightIcon, MenuParts.ForCurrentTheme(lightIcon)); // Menus made earlier show it in dark.

                    Theme(second).SelectedIndex = 1; // Light.
                    Assert.True(second.ApplyChanges());
                    Pump();
                    Assert.False(MenuStyle.Dark);
                    Assert.True(second.IsDisposed);
                    Assert.Equal(4, Current(app).PageIndex);
                    Assert.Same(lightIcon, MenuParts.ForCurrentTheme(lightIcon));
                    Current(app).Close();
                }
                finally
                {
                    app.ExitThread();
                    MenuStyle.UseTheme("system");
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    private static SettingsWindow Current(TrayApplication app) =>
        (SettingsWindow)typeof(TrayApplication).GetField("_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    /// <summary>The Look list: the second list that drops down on the General page, after Language.</summary>
    private static ComboBox Theme(SettingsWindow window) => Descendants(window).OfType<ComboBox>().Skip(1).First();

    private static void Pump()
    {
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
