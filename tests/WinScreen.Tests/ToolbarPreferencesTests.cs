using WinScreen.Localization;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

public sealed class ToolbarPreferencesTests
{
    [Fact]
    public void Loading_old_settings_keeps_the_existing_toolbar_defaults()
    {
        var loaded = AppSettings.Parse("{}");
        Assert.Equal(ToolbarPlacement.Centre, loaded.Toolbar.Placement);
        Assert.Equal(ToolbarTrigger.TitleBar, loaded.Toolbar.Trigger);
        Assert.Equal(500, loaded.Toolbar.HoverDelayMilliseconds);
        Assert.Equal([.. CommandCatalog.Ordered.Where(command => command != AppCommand.RestoreAll), AppCommand.ShowWindowMenu], loaded.Toolbar.Buttons);
    }

    /// <summary>
    /// Settings saved before the Window menu button existed get it once, after their other buttons; once it has been
    /// offered, taking it off stays off.
    /// </summary>
    [Fact]
    public void Older_toolbar_choices_get_the_window_menu_button_once()
    {
        var older = AppSettings.Parse("""{"Toolbar":{"Buttons":["TogglePin","Arrange"]}}""");
        Assert.Equal([AppCommand.TogglePin, AppCommand.Arrange, AppCommand.ShowWindowMenu], older.Toolbar.Buttons);
        Assert.True(older.Toolbar.WindowMenuOffered);
        string saved = System.Text.Json.JsonSerializer.Serialize(older);
        var removed = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(new AppSettings
            { Toolbar = older.Toolbar with { Buttons = [AppCommand.TogglePin] } }));
        Assert.Equal([AppCommand.TogglePin], removed.Toolbar.Buttons);
        Assert.Contains(AppCommand.ShowWindowMenu, new AppSettings().Toolbar.Buttons);
        Assert.Contains(nameof(ToolbarPreferences.WindowMenuOffered), saved);
    }

    [Fact]
    public void Toolbar_choices_survive_saving_and_invalid_buttons_are_removed()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-toolbar-").FullName;
        try
        {
            string path = Path.Combine(folder, "settings.json");
            new AppSettings { Toolbar = new()
            {
                Placement = ToolbarPlacement.Right, Trigger = ToolbarTrigger.MinimizeButton, HoverDelayMilliseconds = 700,
                Buttons = [AppCommand.TogglePin, AppCommand.OpenPip, AppCommand.TogglePin, AppCommand.RestoreAll, (AppCommand)999],
            } }.Save(path);
            var preferences = AppSettings.Load(path).Toolbar;
            Assert.Equal(ToolbarPlacement.Right, preferences.Placement);
            Assert.Equal(ToolbarTrigger.MinimizeButton, preferences.Trigger);
            Assert.Equal(700, preferences.HoverDelayMilliseconds);
            Assert.Equal([AppCommand.TogglePin, AppCommand.OpenPip], preferences.Buttons);
            Assert.Empty(AppSettings.Parse("""{"Toolbar":{"Buttons":[],"WindowMenuOffered":true}}""").Toolbar.Buttons);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(ToolbarPlacement.Left, 134)]
    [InlineData(ToolbarPlacement.Centre, 617)]
    [InlineData(ToolbarPlacement.Right, 984)]
    public void Each_placement_uses_the_title_area_and_keeps_caption_buttons_clear(ToolbarPlacement placement, int expected)
    {
        Point spot = ToolbarGeometry.PlaceAt(new Rectangle(100, 100, 1500, 500), new Size(466, 34),
            new Rectangle(0, 0, 1920, 1032), 150, 34, placement);
        Assert.Equal(new Point(expected, 100), spot);
    }

    [Theory]
    [InlineData(ToolbarPlacement.Left)]
    [InlineData(ToolbarPlacement.Centre)]
    [InlineData(ToolbarPlacement.Right)]
    public void Every_placement_handles_a_narrow_window_on_a_negative_coordinate_monitor(ToolbarPlacement placement)
    {
        var area = new Rectangle(-1920, -200, 1920, 1080);
        var window = new Rectangle(-1900, -180, 180, 300);
        var bar = new Size(466, 34);
        Point place = ToolbarGeometry.PlaceAt(window, bar, area, 150, 34, placement);
        Assert.True(area.Contains(new Rectangle(place, bar)));
        Assert.Equal(window.Top + 34, place.Y);
    }

    [Fact]
    public void Empty_groups_leave_no_gap_and_no_selected_buttons_stop_monitoring()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var toolbar = new WindowToolbar(_ => true, (_, _) => false, _ => true, (_, _) => { });
            toolbar.Configure(new() { Buttons = [AppCommand.TogglePin, AppCommand.RestoreWindow] });
            Assert.Equal(76, toolbar.ClientSize.Width);
            toolbar.Configure(new() { Buttons = [] });
            toolbar.Enable(true);
            var watch = (System.Windows.Forms.Timer)typeof(WindowToolbar).GetField("_watch",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(toolbar)!;
            Assert.False(watch.Enabled);
            Assert.False(toolbar.Visible);
        });
    }

    [Fact]
    public void Features_show_unsaved_shortcut_and_switch_changes()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var preferences = new AppSettings();
            using var settings = new SettingsWindow(preferences, new StartWithWindows(@"Software\WinScreenTests\Toolbar", NoTasks.Instance), _ => []);
            settings.CaptureHotkey(AppCommand.TogglePin, Keys.Control | Keys.F9);
            settings.SetOption(nameof(AppSettings.OpacityWheel), true);
            settings.SetOption(nameof(AppSettings.ShowToolbar), false);
            settings.RefreshFeatures();
            var table = Descendants(settings).OfType<ListView>().Single();
            var pin = table.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, AppCommand.TogglePin));
            Assert.Equal("Ctrl+Alt+T", pin.SubItems[3].Text);
            Assert.Equal("Ctrl+Alt+T", pin.SubItems[2].Text);
            var dark = table.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, AppCommand.ToggleDark));
            Assert.Equal(UiText.T("features.disabled"), dark.SubItems[4].Text);
            Assert.Contains(table.Items.Cast<ListViewItem>(), row => row.Text == UiText.T("features.opacityWheel") && row.SubItems[4].Text == UiText.T("features.enabled"));
            Assert.Contains(table.Items.Cast<ListViewItem>(), row => Equals(row.Tag, "toolbar") && row.SubItems[4].Text == UiText.T("features.disabled"));
            Assert.False(preferences.OpacityWheel);
        });
    }

    [Fact]
    public void Features_show_the_toolbar_off_when_every_chosen_button_is_unavailable()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var preferences = new AppSettings { Toolbar = new() { Buttons = [AppCommand.ToggleDark] } };
            using var settings = new SettingsWindow(preferences, new StartWithWindows(@"Software\WinScreenTests\Toolbar", NoTasks.Instance), _ => []);
            settings.RefreshFeatures();
            var table = Descendants(settings).OfType<ListView>().Single();
            string State() => table.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, "toolbar")).SubItems[4].Text;
            Assert.Equal(UiText.T("features.disabled"), State());
            settings.SetOption(nameof(AppSettings.ExperimentalFeatures), true);
            settings.RefreshFeatures();
            Assert.Equal(UiText.T("features.enabled"), State());
        });
    }

    [Fact]
    public void Buttons_save_by_name_and_read_older_numbers_while_skipping_unknown_ones()
    {
        string saved = System.Text.Json.JsonSerializer.Serialize(new ToolbarPreferences { Buttons = [AppCommand.OpenPip] });
        Assert.Contains("\"Buttons\":[\"OpenPip\"]", saved);
        int pin = (int)AppCommand.TogglePin;
        var loaded = AppSettings.Parse($$$"""{"Toolbar":{"WindowMenuOffered":true,"Buttons":[{{{pin}}},"OpenPip","ACommandFromLater",{"odd":1}]}}""");
        Assert.Equal([AppCommand.TogglePin, AppCommand.OpenPip], loaded.Toolbar.Buttons);
        Assert.Equal(ToolbarPreferences.DefaultButtons(), AppSettings.Parse("""{"Toolbar":{"Buttons":"none"}}""").Toolbar.Buttons);
        Assert.Equal(ToolbarPreferences.DefaultButtons(), AppSettings.Parse("""{"Toolbar":{"Buttons":null}}""").Toolbar.Buttons);
    }

    [Fact]
    public void Export_and_import_keep_the_toolbar_choices()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-toolbar-").FullName;
        try
        {
            string file = Path.Combine(folder, "export.json");
            SettingsTransfer.Export(new AppSettings { Toolbar = new()
            {
                Placement = ToolbarPlacement.Left, Trigger = ToolbarTrigger.MinimizeButton,
                HoverDelayMilliseconds = 900, Buttons = [AppCommand.OpenPip],
            } }, file);
            ToolbarPreferences imported = SettingsTransfer.Import(file, new AppSettings()).Toolbar;
            Assert.Equal(ToolbarPlacement.Left, imported.Placement);
            Assert.Equal(ToolbarTrigger.MinimizeButton, imported.Trigger);
            Assert.Equal(900, imported.HoverDelayMilliseconds);
            Assert.Equal([AppCommand.OpenPip], imported.Buttons);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Settings_page_shows_and_saves_the_toolbar_choices()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            AppSettings? saved = null;
            var preferences = new AppSettings { Toolbar = new()
            {
                Placement = ToolbarPlacement.Right, Trigger = ToolbarTrigger.MinimizeButton,
                HoverDelayMilliseconds = 1200, Buttons = [AppCommand.TogglePin, AppCommand.Arrange],
            } };
            using var settings = new SettingsWindow(preferences, new StartWithWindows(@"Software\WinScreenTests\Toolbar", NoTasks.Instance),
                draft => { saved = draft; return []; });
            Assert.True(settings.SaveChanges());
            Assert.NotNull(saved);
            Assert.Equal(ToolbarPlacement.Right, saved.Toolbar.Placement);
            Assert.Equal(ToolbarTrigger.MinimizeButton, saved.Toolbar.Trigger);
            Assert.Equal(1200, saved.Toolbar.HoverDelayMilliseconds);
            Assert.Equal([AppCommand.TogglePin, AppCommand.Arrange], saved.Toolbar.Buttons);
        });
    }

    [Fact]
    public void Activating_a_feature_opens_the_page_that_changes_it()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var settings = new SettingsWindow(new AppSettings(), new StartWithWindows(@"Software\WinScreenTests\Toolbar", NoTasks.Instance), _ => []);
            settings.RefreshFeatures();
            TabControl tabs = Descendants(settings).OfType<TabControl>().Single();
            ListView table = Descendants(settings).OfType<ListView>().Single();
            void Activate(object tag)
            {
                ListViewItem row = table.Items.Cast<ListViewItem>().First(item => Equals(item.Tag, tag));
                table.SelectedIndices.Clear();
                table.SelectedIndices.Add(row.Index);
                typeof(ListView).GetMethod("OnItemActivate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(table, [EventArgs.Empty]);
            }

            settings.CreateControl();
            _ = table.Handle;
            Activate(AppCommand.TogglePin);
            Assert.Equal(UiText.T("features.title"), tabs.SelectedTab!.Text);
            Activate("toolbar");
            Assert.Equal(UiText.T("toolbar.settings.title"), tabs.SelectedTab!.Text);
            Activate("gestures");
            Assert.Equal(UiText.T("gestures.title"), tabs.SelectedTab!.Text);
            Activate("appearance");
            Assert.Equal(UiText.T("settings.customization"), tabs.SelectedTab!.Text);
            Activate("pip");
            Assert.Equal(UiText.T("pip.open"), tabs.SelectedTab!.Text);
        });
    }

    [Fact]
    public void Rearranging_at_the_same_size_leaves_the_button_font_usable()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var toolbar = new WindowToolbar(_ => true, (_, _) => false, _ => true, (_, _) => { });
            toolbar.Configure(new() { Buttons = [AppCommand.TogglePin] });
            toolbar.Configure(new() { Buttons = [AppCommand.TogglePin, AppCommand.OpenPip] });
            foreach (Button button in toolbar.Controls.OfType<Button>())
                Assert.True(button.Font.GetHeight() > 0);
        });
    }

    [Fact]
    public void A_toolbar_with_only_unavailable_buttons_has_nothing_to_open()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            bool experimental = false;
            using var toolbar = new WindowToolbar(_ => true, (_, _) => false, _ => experimental, (_, _) => { });
            toolbar.Configure(new() { Buttons = [AppCommand.ToggleDark] });
            Assert.False(toolbar.HasButtons);
            experimental = true;
            toolbar.Configure(new() { Buttons = [AppCommand.ToggleDark] });
            Assert.True(toolbar.HasButtons);
        });
    }

    [Fact]
    public void Minimize_trigger_watches_the_caption_buttons_and_opens_below_them()
    {
        var area = new Rectangle(0, 0, 1920, 1032);
        var window = new Rectangle(-8, -8, 1936, 1048); // Maximized: its frame hangs past the work area.
        Assert.Equal(new Rectangle(1778, 0, 150, 34), ToolbarGeometry.CaptionButtonsZone(window, area, 150, 34));
        Assert.Equal(34, ToolbarGeometry.BelowCaption(window, 34, area, 34));
        Assert.Equal(998, ToolbarGeometry.BelowCaption(new Rectangle(100, 1020, 400, 300), 34, area, 34));
    }

    [Fact]
    [Trait("Category", "RealWindows")]
    public void Triggers_tell_a_real_minimize_button_from_the_title_bar()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 500, 300));
        target.UseFrame();
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        var area = new Rectangle(0, 0, 3840, 2160);
        Rectangle zone = ToolbarGeometry.CaptionButtonsZone(bounds, area, 150, 34);
        int middle = bounds.Top + 16;
        bool minimize = Enumerable.Range(zone.Left, zone.Width).Any(x =>
            WindowToolbar.MatchesTrigger(target.Handle, new Point(x, middle), ToolbarTrigger.MinimizeButton));
        Assert.True(minimize, "No point of the caption-button zone reported the Minimize button.");
        var title = new Point(bounds.Left + 120, middle);
        Assert.True(WindowToolbar.MatchesTrigger(target.Handle, title, ToolbarTrigger.TitleBar));
        Assert.False(WindowToolbar.MatchesTrigger(target.Handle, title, ToolbarTrigger.MinimizeButton));
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
