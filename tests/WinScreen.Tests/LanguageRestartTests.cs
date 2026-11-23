using System.Reflection;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A new language restarts WinScreen by itself (the owner's report on 1.1.0-beta, F2): the way exit goes, so windows
/// are put back first, asking first when there is something to close, and with Settings back on the same page.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class LanguageRestartTests
{
    private static SettingsWindow Current(TrayApplication app) =>
        (SettingsWindow)typeof(TrayApplication).GetField("_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private static void Open(TrayApplication app, int page = 0) =>
        typeof(TrayApplication).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app,
            [null, page, false, null, false, FormWindowState.Normal]);

    /// <summary>The Language list: the first list that drops down on the General page.</summary>
    private static ComboBox Language(SettingsWindow window) => Descendants(window).OfType<ComboBox>().First();

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Pump(int milliseconds = 300)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static AppSettings Quiet() => new()
    {
        WelcomeShown = true, ShowNotifications = false, ShowToolbar = false, Language = "en",
        Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
    };

    private static void InFolder(Action<string> test)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-language-").FullName;
        try { WindowMenuBuilderTests.OnUiThread(() => test(Path.Combine(folder, "settings.json"))); }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void With_nothing_open_a_new_language_restarts_at_once_on_the_same_page()
    {
        InFolder(path =>
        {
            var started = new List<int>();
            bool asked = false;
            var app = new TrayApplication(new WindowController([]), Quiet(), path)
                { StartCopy = page => { started.Add(page); return true; }, AskToRestart = (_, _) => asked = true };
            try
            {
                Open(app);
                Language(Current(app)).SelectedIndex = 2; // 简体中文
                { SettingsWindow shown = Current(app); Assert.True(shown.ApplyChanges(), shown.StatusText); }
                Pump();
                Assert.False(asked);
                Assert.Equal([0], started);
                Assert.True(app.Exiting);
                Assert.Equal("zh-Hans", AppSettings.Load(path).Language);
            }
            finally { if (!app.Exiting) app.ExitThread(); }
        });
    }

    [Fact]
    public void With_a_changed_window_it_asks_and_puts_the_window_back_before_restarting()
    {
        using var target = new TargetProcess(new Rectangle(300, 220, 400, 300));
        target.UseFrame();
        InFolder(path =>
        {
            var started = new List<int>();
            bool answer = false;
            var asked = new List<(int Views, int Windows)>();
            var windows = new WindowController([]);
            var app = new TrayApplication(windows, Quiet(), path)
                { StartCopy = page => { started.Add(page); return true; }, AskToRestart = (views, changed) => { asked.Add((views, changed)); return answer; } };
            try
            {
                int height = Desktop.BoundsOf(target.Handle).Height;
                Assert.True(windows.ToggleRollUp(target.Handle));

                // "Later": nothing closes, and the language waits for the next start.
                Open(app);
                Language(Current(app)).SelectedIndex = 3; // 繁體中文
                { SettingsWindow shown = Current(app); Assert.True(shown.ApplyChanges(), shown.StatusText); }
                Pump();
                Assert.Equal([(0, 1)], asked);
                Assert.Empty(started);
                Assert.False(app.Exiting);
                Assert.True(windows.IsRolledUp(target.Handle));

                // Yes: the window is put back first, then the new copy starts, then this one exits.
                answer = true;
                Language(Current(app)).SelectedIndex = 2;
                { SettingsWindow shown = Current(app); Assert.True(shown.ApplyChanges(), shown.StatusText); }
                Pump();
                Assert.Equal(2, asked.Count);
                Assert.Single(started);
                Assert.Equal(height, Desktop.BoundsOf(target.Handle).Height); // Put back before the restart.
                Assert.True(app.Exiting);
            }
            finally { if (!app.Exiting) app.ExitThread(); windows.RestoreAll(); }
        });
    }

    [Fact]
    public void A_copy_started_for_Settings_opens_it_on_that_page()
    {
        InFolder(path =>
        {
            var app = new TrayApplication(new WindowController([]), Quiet(), path, settingsPage: 3);
            try
            {
                Assert.True(Desktop.WaitFor(() =>
                {
                    Application.DoEvents();
                    return typeof(TrayApplication).GetField("_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app) is SettingsWindow { Visible: true };
                }, 3000, Application.DoEvents), "Settings didn't open.");
                Assert.Equal(3, Current(app).PageIndex);
                Current(app).Close();
            }
            finally { app.ExitThread(); }
        });
    }

    [Theory]
    [InlineData(new[] { "--elevated", "--after", "12", "--settings-page", "4" }, 4)]
    [InlineData(new[] { "--settings-page" }, null)]
    [InlineData(new[] { "--settings-page", "-1" }, null)]
    [InlineData(new string[0], null)]
    public void The_page_is_read_from_the_arguments(string[] arguments, int? page) =>
        Assert.Equal(page, Elevation.SettingsPage(arguments));

    /// <summary>The new copy's arguments are not a command for a running copy: it starts as WinScreen.</summary>
    [Fact]
    public void A_restarted_copy_starts_rather_than_answering_a_command() =>
        Assert.IsType<CommandLineRequest.None>(CommandLine.Parse(["--elevated", "--after", "12", "--settings-page", "4"]));
}
