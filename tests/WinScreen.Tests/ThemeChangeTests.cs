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
                    typeof(TrayApplication).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [null, 0, false, null, false, FormWindowState.Normal]);
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

    private static void Open(TrayApplication app) =>
        typeof(TrayApplication).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app,
            [null, 0, false, null, false, FormWindowState.Normal]);

    /// <summary>
    /// Settings opens where it was left, maximized if it was. A maximized Settings made again for the other look
    /// stays maximized, and one minimized is made again only when it comes back, so nothing takes the keyboard.
    /// </summary>
    [Fact]
    public void Settings_keeps_its_place_and_state_through_closing_and_a_new_look()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-place-").FullName;
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
                    Open(app);
                    var spot = new Rectangle(140, 90, 1000, 700);
                    Current(app).Bounds = spot;
                    Pump();
                    Current(app).Close();
                    Pump();
                    Assert.Equal(new SettingsPlacement(spot.X, spot.Y, spot.Width, spot.Height, false), settings.SettingsWindowPlacement);

                    Open(app);
                    Pump();
                    Assert.Equal(spot, Current(app).Bounds);
                    Current(app).WindowState = FormWindowState.Maximized;
                    Pump();
                    Current(app).Close();
                    Pump();
                    Assert.True(settings.SettingsWindowPlacement!.Maximized);
                    Assert.Equal(spot, settings.SettingsWindowPlacement.Bounds); // The size it goes back to.

                    Open(app);
                    Pump();
                    SettingsWindow maximized = Current(app);
                    Assert.True(maximized.FillsScreen);
                    Theme(maximized).SelectedIndex = 2; // Dark.
                    Assert.True(maximized.ApplyChanges());
                    Pump();
                    Assert.True(maximized.IsDisposed);
                    Assert.True(Current(app).FillsScreen);

                    SettingsWindow minimized = Current(app);
                    minimized.WindowState = FormWindowState.Minimized;
                    Pump();
                    Theme(minimized).SelectedIndex = 1; // Light.
                    Assert.True(minimized.ApplyChanges());
                    Pump();
                    Assert.False(minimized.IsDisposed); // Waits on the taskbar in the old look.
                    Assert.Same(minimized, Current(app));

                    Open(app); // As the tray icon does: back from the taskbar, then made again in the new look.
                    Pump();
                    Assert.True(minimized.IsDisposed);
                    Assert.False(Current(app).MadeDark);
                    Assert.True(Current(app).FillsScreen);
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
