using WinScreen.Localization;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class WindowMenuBuilderTests
{
    internal static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<ToolStripMenuItem> Commands(ToolStripItemCollection items) =>
        items.OfType<ToolStripMenuItem>().SelectMany(item => new[] { item }.Concat(Commands(item.DropDownItems)));

    /// <summary>
    /// The toolbar's Window menu offers what the window menu has that the bar doesn't: with every button on the bar,
    /// Minimize to an Anchor, the title bar and frame choices and the window's details. Anything shown as a button is left out, and so is the
    /// slider or grid a button already covers; with fewer buttons, more stays.
    /// </summary>
    [Fact]
    public void The_toolbar_window_menu_leaves_out_what_the_bar_shows()
    {
        OnUiThread(() =>
        {
            ToolStripItemCollection Built(IReadOnlySet<AppCommand> onBar)
            {
                var menu = new ContextMenuStrip();
                var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
                    { OpacityPercent = _ => 100, SetOpacityPercent = (_, _) => { } };
                builder.Populate(menu.Items, false);
                WindowMenuBuilder.LeaveOut(menu.Items, onBar);
                return menu.Items;
            }
            ToolStripItemCollection all = Built(CommandCatalog.ToolbarButtons.ToHashSet());
            AppCommand[] left = Commands(all).Select(item => item.Tag).OfType<AppCommand>().ToArray();
            Assert.Equal([AppCommand.MinimizeToAnchor, AppCommand.ToggleDarkTitleBar, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill], left);
            Assert.DoesNotContain(all.Cast<ToolStripItem>(), item => item is OpacityRow or PlacementRow);
            Assert.Contains(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("window.openLocation"));
            Assert.Contains(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("common.copy"));
            Assert.DoesNotContain(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("menu.more"));
            // No heading without anything under it, and no divider at an end or beside another.
            ToolStripItem[] rows = all.Cast<ToolStripItem>().ToArray();
            Assert.IsNotType<ToolStripSeparator>(rows[0]);
            Assert.IsNotType<ToolStripSeparator>(rows[^1]);
            for (int index = 0; index < rows.Length; index++)
            {
                ToolStripItem? next = index + 1 < rows.Length ? rows[index + 1] : null;
                if (rows[index] is ToolStripSeparator) Assert.IsNotType<ToolStripSeparator>(next);
                if (Equals(rows[index].Tag, MenuParts.HeaderTag)) Assert.True(next is ToolStripMenuItem, rows[index].Text);
            }

            // A bar with only Pin keeps the slider, the grid, Arrange and the effects.
            ToolStripItemCollection few = Built(new HashSet<AppCommand> { AppCommand.TogglePin });
            Assert.Contains(few.Cast<ToolStripItem>(), item => item is OpacityRow);
            Assert.Contains(few.Cast<ToolStripItem>(), item => item is PlacementRow);
            Assert.DoesNotContain(Commands(few), item => Equals(item.Tag, AppCommand.TogglePin));
            foreach (AppCommand command in new[] { AppCommand.Arrange, AppCommand.ToggleDark, AppCommand.RestoreWindow, AppCommand.SaveWindowSetup })
                Assert.Contains(Commands(few), item => Equals(item.Tag, command));
        });
    }

    [Fact]
    public void Both_menus_share_commands_and_disable_missing_targets()
    {
        OnUiThread(() =>
        {
            foreach (bool tray in new[] { false, true })
            {
                using var menu = new ContextMenuStrip();
                var builder = new WindowMenuBuilder(() => null, (_, _) => Assert.Fail("No target"), (_, _) => Assert.Fail("No target"),
                    command => !CommandCatalog.IsExperimental(command), (_, _) => true, (_, _) => false, () => { });
                builder.Populate(menu.Items, tray);
                builder.Refresh(menu.Items);
                var commands = Commands(menu.Items).Where(item => item.Tag is AppCommand).ToArray();
                // The nine places are in the grid, not listed again under Arrange; everything else is a command row.
                foreach (AppCommand command in CommandCatalog.Ordered.Where(command => command != AppCommand.RestoreAll))
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                Assert.DoesNotContain(commands, item => item.Tag is AppCommand command && CommandCatalog.AnchorOf(command) is not null);
                AccessibleObject places = menu.Items.OfType<PlacementRow>().Single().Control.AccessibilityObject;
                Assert.Equal(CommandCatalog.All.Where(command => CommandCatalog.AnchorOf(command) is not null).Select(CommandCatalog.Name).Order(),
                    Enumerable.Range(0, places.GetChildCount()).Select(index => places.GetChild(index)!.Name).Order());
                foreach (AppCommand command in new[] { AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar, AppCommand.ShowWindowInfo })
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                Assert.All(commands.Where(item => !Equals(item.Tag, AppCommand.RestoreAll)), item => Assert.False(item.Enabled));
                Assert.DoesNotContain(commands, item => Equals(item.Tag, AppCommand.RestoreAll));
                Assert.Single(commands, item => Equals(item.Tag, AppCommand.ShowWindowInfo));
                Assert.Equal(CommandCatalog.ArrangeMenu.OfType<AppCommand>().Where(command => CommandCatalog.AnchorOf(command) is null),
                    Commands(menu.Items).Single(item => Equals(item.Tag, AppCommand.Arrange)).DropDownItems
                        .OfType<ToolStripMenuItem>().Select(item => (AppCommand)item.Tag!));
                using var toolbar = new ContextMenuStrip();
                builder.PopulateArrange(toolbar.Items); // The toolbar's Arrange button has no grid, so it keeps all nine.
                Assert.Equal(CommandCatalog.ArrangeMenu.OfType<AppCommand>(),
                    toolbar.Items.OfType<ToolStripMenuItem>().Select(item => (AppCommand)item.Tag!));
                Assert.Contains(Commands(menu.Items), item => item.Text == UiText.T("common.copy"));
                // Group headings and the two rows that hold a control are not commands, and nothing lists them as such.
                Assert.Equal(3, menu.Items.OfType<ToolStripLabel>().Count());
                Assert.Single(menu.Items.OfType<PlacementRow>());
            }
        });
    }

    [Fact]
    public void The_placement_grid_reads_as_nine_named_buttons()
    {
        OnUiThread(() =>
        {
            AppCommand? chosen = null;
            using var row = new PlacementRow(place => chosen = place);
            AccessibleObject grid = row.Control.AccessibilityObject;
            Assert.Equal(9, grid.GetChildCount());
            AccessibleObject corner = grid.GetChild(8)!;
            Assert.Equal(AccessibleRole.PushButton, corner.Role);
            Assert.Equal(CommandCatalog.Name(AppCommand.AlignBottomRight), corner.Name);
            Assert.Equal(CommandCatalog.Name(AppCommand.AlignTopLeft), grid.GetChild(0)!.Name);
            corner.DoDefaultAction();
            Assert.Equal(AppCommand.AlignBottomRight, chosen);
        });
    }

    [Fact]
    public void Help_text_splits_into_one_note_per_gesture()
    {
        try
        {
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant" })
            {
                UiText.Initialize(language);
                foreach (string key in new[] { "pip.controls.help", "pip.controls.directHelp" })
                    Assert.All(MenuParts.Notes(UiText.T(key)).ToArray(), note =>
                    {
                        Assert.NotEmpty(note.Text!);
                        Assert.NotEmpty(note.How);
                        note.Dispose();
                    });
            }
        }
        finally { UiText.Initialize("en"); }
    }

    [Fact]
    public void Clicking_a_stale_target_never_dispatches_a_command()
    {
        OnUiThread(() =>
        {
            using var menu = new ContextMenuStrip();
            int stale = 0;
            var builder = new WindowMenuBuilder(() => null, (_, _) => Assert.Fail("Stale target dispatched"), (_, _) => Assert.Fail("Stale target dispatched"),
                _ => true, (_, _) => true, (_, _) => false, () => stale++);
            builder.Populate(menu.Items, false);
            Assert.Single(Commands(menu.Items), item => Equals(item.Tag, AppCommand.TogglePin)).PerformClick();
            Assert.Equal(1, stale);
        });
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void New_commands_and_gesture_strings_are_translated(string language)
    {
        try
        {
            UiText.Initialize(language);
            foreach (string text in new[] { "gestures.title", "window.menu", "window.borderless", "window.fillDisplay", "window.darkTitleBar",
                         "window.copy.all", "pip.keepAspectRatio", "gestures.menu.ctrlRight", "gestures.menu.ctrlShiftRight", "gestures.menu.ctrlMiddle",
                         "errors.borderless.disableFirst", "errors.window.nonstandardTitleBar" })
                Assert.NotEqual(UiText.GetCatalog("en")[text], UiText.T(text));
        }
        finally { UiText.Initialize("en"); }
    }
}

[Trait("Category", "Interactive")]
public sealed class WindowMenuGestureTests
{
    [Theory]
    [InlineData(WindowMenuGesture.CtrlRightClick)]
    [InlineData(WindowMenuGesture.CtrlShiftRightClick)]
    [InlineData(WindowMenuGesture.CtrlMiddleClick)]
    public void Gesture_swallows_the_click_opens_a_focused_menu_and_returns_focus(WindowMenuGesture gesture)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            using var target = new TargetProcess(new Rectangle(300, 200, 400, 300));
            Desktop.Activate(target.Handle);
            Point cursor = Cursor.Position;
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            WindowMenuHost? host = null;
            using var gestures = new MouseGestures(_ => false, _ => { })
            {
                MoveEnabled = false, MenuEnabled = true, MenuGesture = gesture,
                MenuEligible = window => window == target.Handle,
                MenuRequested = (_, point) => dispatcher.BeginInvoke(() =>
                {
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("First"); menu.Items.Add("Second");
                    host = new WindowMenuHost(menu, point);
                    host.Open();
                }),
            };
            gestures.SetActive(true);
            try
            {
                Desktop.Key(Keys.ControlKey, true);
                if (gesture == WindowMenuGesture.CtrlShiftRightClick) Desktop.Key(Keys.ShiftKey, true);
                if (gesture == WindowMenuGesture.CtrlMiddleClick) Desktop.MiddleClickOn(target.Center, target.Handle);
                else Desktop.RightClickOn(target.Center, target.Handle);
                Desktop.Key(Keys.ShiftKey, false);
                Desktop.Key(Keys.ControlKey, false);
                Assert.True(Desktop.WaitFor(() => host is { Visible: true }, 1000, Application.DoEvents));
                Assert.Equal(host!.Handle, User32.GetForegroundWindow());
                Assert.Null(target.NextReport(100));
                Desktop.Press(Keys.Down);
                Desktop.Press(Keys.Escape);
                Assert.True(Desktop.WaitFor(() => host.IsDisposed, 1000, Application.DoEvents));
                Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle, 1000));
                Assert.InRange(gestures.LongestCallback.TotalMilliseconds, 0, 100);
            }
            finally
            {
                Desktop.Key(Keys.ControlKey, false); Desktop.Key(Keys.ShiftKey, false);
                host?.Dispose(); gestures.SetActive(false); Cursor.Position = cursor;
            }
        });
    }

    [Fact]
    public void Fallback_menu_closes_on_outside_press_and_temporary_escape_hotkey()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            foreach (bool escape in new[] { false, true })
            {
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Test");
                using var host = new WindowMenuHost(menu, new Point(500, 300)) { ForceFallback = true };
                host.Open();
                Assert.True(Desktop.WaitFor(() => menu.Visible));
                Assert.True(host.NeedsHook);
                if (escape) User32.PostMessage(host.Handle, User32.WM_HOTKEY, 0x5780, 0);
                else host.OutsidePress(new Point(10, 10));
                Assert.True(Desktop.WaitFor(() => host.IsDisposed, 1000, Application.DoEvents));
            }
        });
    }
}
