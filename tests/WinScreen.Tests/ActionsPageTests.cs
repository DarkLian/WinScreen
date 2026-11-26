using System.Reflection;
using WinScreen.Localization;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Settings' Actions and shortcuts (the owner's ideas, 27 Nov): actions in the menus' groups, the shortcut changed in
/// its row with a mark that puts the default back, filters, a search by the keys themselves, where else each action is,
/// and one button that puts every shortcut back.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class ActionsPageTests
{
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static SettingsWindow Window(AppSettings? settings = null, Func<AppCommand, bool>? registered = null) =>
        new(settings ?? new AppSettings { Language = "en" }, new StartWithWindows(@"Software\WinScreenTests\Actions", NoTasks.Instance),
            _ => [], registered ?? (_ => true));

    private static ListView Table(SettingsWindow window) => Descendants(window).OfType<ListView>().Single();

    private static List<ListViewItem> Rows(SettingsWindow window) => Table(window).Items.Cast<ListViewItem>().ToList();

    private static void Raise(Control control, string method, EventArgs args) =>
        typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, [args.GetType()])!.Invoke(control, [args]);

    [Fact]
    public void Every_action_shows_once_under_its_group_and_a_heading_is_never_chosen()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using SettingsWindow window = Window();
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = new Rectangle(0, 0, 1100, 760);
            window.Show();
            Descendants(window).OfType<TabControl>().Single().SelectedIndex = 7;
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); }
            List<ListViewItem> rows = Rows(window);
            Assert.IsType<SettingsWindow.FeatureHeading>(rows[0].Tag);
            Assert.Equal(CommandCatalog.All.OrderBy(command => command), rows.Select(row => row.Tag).OfType<AppCommand>().OrderBy(command => command));
            string heading = "";
            foreach (ListViewItem row in rows)
                if (row.Tag is SettingsWindow.FeatureHeading title) heading = title.Text;
                else if (row.Tag is AppCommand command) Assert.Equal(SettingsWindow.GroupName(CommandCatalog.GroupOf(command)), heading);
                else Assert.Equal(UiText.T("actions.group.mouse"), heading);
            Assert.Equal(Enum.GetValues<ActionGroup>().Length + 1, rows.Count(row => row.Tag is SettingsWindow.FeatureHeading));

            ListView table = Table(window);
            Assert.IsNotType<SettingsWindow.FeatureHeading>(table.SelectedItems[0].Tag);
            int second = rows.FindIndex(1, row => row.Tag is SettingsWindow.FeatureHeading);
            rows[second].Selected = true;
            Assert.Single(table.SelectedItems.Cast<ListViewItem>());
            Assert.Equal(second + 1, table.SelectedIndices[0]); // Moved on past it, downwards.
            window.Close();
        });
    }

    [Fact]
    public void A_shortcut_is_changed_in_its_row_and_Esc_keeps_the_old_one()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var saved = new List<AppSettings>();
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(@"Software\WinScreenTests\Actions", NoTasks.Instance),
                draft => { saved.Add(draft); return []; }, _ => true);
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = new Rectangle(0, 0, 1100, 760);
            window.Show();
            TabControl tabs = Descendants(window).OfType<TabControl>().Single();
            tabs.SelectedIndex = 7;
            void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); } }
            Pump();
            ListView table = Table(window);
            ListViewItem pin = Rows(window).Single(row => Equals(row.Tag, AppCommand.TogglePin));

            // A click on the shortcut opens the box right over it.
            Rectangle cell = pin.SubItems[SettingsWindow.ShortcutColumn].Bounds;
            Raise(table, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, cell.Left + cell.Width / 2, cell.Top + cell.Height / 2, 0));
            Assert.Equal(AppCommand.TogglePin, window.EditingShortcut);
            Control frame = table.Controls.Cast<Control>().Single(control => control.Visible);
            Assert.True(Rectangle.Inflate(cell, 1, 1).Contains(frame.Bounds), $"The box {frame.Bounds} isn't over its shortcut {cell}.");
            TextBox box = Descendants(frame).OfType<TextBox>().Single();
            Assert.Equal("Ctrl+Alt+T", box.Text);

            Raise(box, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Alt | Keys.F23));
            Assert.Null(window.EditingShortcut); // The keys were the whole change.
            Assert.False(frame.Visible);
            Assert.Equal("Ctrl+Alt+F23", box.Text);
            Pump();
            Rows(window).Single(row => Equals(row.Tag, AppCommand.TogglePin)).Selected = true;
            window.RefreshFeatures();
            pin = Rows(window).Single(row => Equals(row.Tag, AppCommand.TogglePin));
            Assert.Equal("Ctrl+Alt+F23", pin.SubItems[SettingsWindow.ShortcutColumn].Text);
            Assert.NotNull(window.ResetGlyph(pin));

            window.BeginShortcutEdit(AppCommand.TogglePin);
            Raise(box, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Alt | Keys.F22));
            window.BeginShortcutEdit(AppCommand.TogglePin);
            Raise(box, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Assert.Null(window.EditingShortcut);
            Assert.Equal("Ctrl+Alt+F22", box.Text); // Esc keeps what was there when the box opened.

            // The mark after a changed shortcut puts the default back.
            window.RefreshFeatures();
            pin = Rows(window).Single(row => Equals(row.Tag, AppCommand.TogglePin));
            Rectangle mark = window.ResetGlyph(pin)!.Value;
            Raise(table, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, mark.Left + mark.Width / 2, mark.Top + mark.Height / 2, 0));
            Assert.Equal("Ctrl+Alt+T", box.Text);
            Assert.Null(window.EditingShortcut);
            window.RefreshFeatures();
            Assert.Null(window.ResetGlyph(Rows(window).Single(row => Equals(row.Tag, AppCommand.TogglePin))));
            window.Close();
        });
    }

    [Fact]
    public void Reset_all_puts_every_shortcut_back_and_is_off_when_nothing_changed()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using SettingsWindow window = Window();
            window.RefreshFeatures();
            Button resetAll = Descendants(window).OfType<Button>().Single(button => button.Text == UiText.T("actions.resetAll"));
            Assert.False(resetAll.Enabled);
            window.CaptureHotkey(AppCommand.TogglePin, Keys.Control | Keys.Alt | Keys.F21);
            window.CaptureHotkey(AppCommand.ToggleDark, Keys.Delete);
            window.CaptureHotkey(AppCommand.ViewMute, Keys.Control | Keys.Alt | Keys.F20);
            window.RefreshFeatures();
            Assert.True(resetAll.Enabled);
            window.ResetAllShortcuts();
            Assert.DoesNotContain(CommandCatalog.All, window.ShortcutChanged);
            window.RefreshFeatures();
            Assert.False(resetAll.Enabled);
        });
    }

    [Fact]
    public void Filters_show_actions_with_a_shortcut_changed_ones_and_ones_not_working()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using SettingsWindow window = Window(registered: command => command != AppCommand.ToggleGlass);
            window.CaptureHotkey(AppCommand.ViewMute, Keys.Control | Keys.Alt | Keys.F20);
            IEnumerable<AppCommand> Shown() => Rows(window).Select(row => row.Tag).OfType<AppCommand>();

            window.SetActionFilter(SettingsWindow.ActionFilter.WithShortcut);
            Assert.Equal(new AppSettings().Hotkeys.Keys.Append(AppCommand.ViewMute).OrderBy(command => command), Shown().OrderBy(command => command));
            Assert.DoesNotContain(Rows(window), row => row.Tag is string && row.SubItems[SettingsWindow.StateColumn].Text == UiText.T("features.disabled"));

            window.SetActionFilter(SettingsWindow.ActionFilter.Changed);
            Assert.Equal([AppCommand.ViewMute], Shown());
            Assert.DoesNotContain(Rows(window), row => row.Tag is string);

            window.SetActionFilter(SettingsWindow.ActionFilter.NotWorking);
            Assert.Equal([AppCommand.ToggleGlass], Shown());
            Assert.Equal(UiText.T("actions.unavailable"), Rows(window).Single(row => Equals(row.Tag, AppCommand.ToggleGlass)).SubItems[SettingsWindow.StateColumn].Text);

            window.SetActionFilter(SettingsWindow.ActionFilter.All);
            Assert.Equal(CommandCatalog.All.Count(), Shown().Count());
        });
    }

    [Fact]
    public void Press_keys_finds_the_action_with_those_keys_or_says_whether_they_are_free()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using SettingsWindow window = Window();
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = new Rectangle(0, 0, 1100, 760);
            window.Show();
            Descendants(window).OfType<TabControl>().Single().SelectedIndex = 7;
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); }
            TextBox search = Descendants(window).OfType<TextBox>().Single(box => box.AccessibleName == UiText.T("actions.search"));

            window.ListenForShortcutSearch();
            Raise(search, "OnKeyDown", new KeyEventArgs(Keys.ControlKey | Keys.Control)); // A modifier alone is not yet a shortcut.
            Assert.Equal("", search.Text);
            Raise(search, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Alt | Keys.T));
            Assert.Equal("Ctrl+Alt+T", search.Text);
            Assert.Equal([AppCommand.TogglePin], Rows(window).Select(row => row.Tag).OfType<AppCommand>());

            // Not Ctrl+Alt+Shift+S's action for Ctrl+Alt+S, and keys no action has are said to be free.
            window.ListenForShortcutSearch();
            Raise(search, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Alt | Keys.F24));
            Assert.Empty(Rows(window));
            Assert.Contains(Descendants(window).OfType<Label>(), label => label.Text == string.Format(UiText.T("actions.keys.free"), "Ctrl+Alt+F24"));

            // Typed words still search by name.
            search.Text = "pin";
            Assert.Contains(Rows(window), row => Equals(row.Tag, AppCommand.TogglePin));
            window.Close();
        });
    }

    [Fact]
    public void Also_in_names_the_menus_that_have_the_action()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using SettingsWindow window = Window(new AppSettings { Language = "en", WindowMenu = true });
            Assert.Contains(UiText.T("actions.place.windowMenu"), window.AlsoIn(AppCommand.ToggleCrop));
            Assert.Contains(UiText.T("actions.place.viewMenu"), window.AlsoIn(AppCommand.ViewMute));
            Assert.DoesNotContain(UiText.T("actions.place.windowMenu"), window.AlsoIn(AppCommand.ViewMute));
            Assert.Contains(UiText.T("actions.place.tray"), window.AlsoIn(AppCommand.UnpinEverywhere));
            Assert.Contains(UiText.T("gestures.menu.ctrlRight"), window.AlsoIn(AppCommand.ShowWindowMenu));
        });
    }

    /// <summary>What Settings says is in a window's menu is what the menu has: the menu itself is built to check.</summary>
    [Fact]
    public void The_catalog_knows_every_action_a_window_menu_has()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            foreach (bool slider in new[] { false, true })
            {
                using var menu = new ContextMenuStrip();
                int opacity = 100;
                var builder = slider
                    ? new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
                        { OpacityPercent = _ => opacity, SetOpacityPercent = (_, value) => opacity = value }
                    : new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { });
                builder.Populate(menu.Items, false);
                var found = new HashSet<AppCommand>();
                void Walk(ToolStripItemCollection items)
                {
                    foreach (ToolStripItem item in items)
                    {
                        if (item is PlacementRow)
                            foreach (AppCommand place in CommandCatalog.All.Where(command => CommandCatalog.AnchorOf(command) is not null)) found.Add(place);
                        else if (item is OpacityRow) { found.Add(AppCommand.OpacityUp); found.Add(AppCommand.OpacityDown); }
                        else if (item.Tag is AppCommand command) found.Add(command);
                        if (item is ToolStripMenuItem { HasDropDownItems: true } parent) Walk(parent.DropDownItems);
                    }
                }
                Walk(menu.Items);
                Assert.Equal(CommandCatalog.All.Where(CommandCatalog.InWindowMenu).OrderBy(command => command), found.OrderBy(command => command));
            }
        });
    }
}
