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

    // The owner, 28 Nov: a video page's long Chinese title made the window menu about 700 px wide, and the opacity
    // slider with it. The title is cut to the width the commands need.
    [Theory]
    [InlineData("谁是最适合当主人的AI？五大AI挑战“人类饲养”测试！【牢大百科18】_哔哩哔哩_bilibili - Google Chrome", true)]
    [InlineData("Notes", false)]
    public void A_long_window_title_never_widens_its_menu(string title, bool cut)
    {
        OnUiThread(() =>
        {
            UiText.Initialize("en");
            int Width(string? shown)
            {
                using var menu = new ContextMenuStrip();
                if (shown is not null)
                    menu.Items.Add(new ToolStripMenuItem(shown) { Enabled = false, Name = MenuRenderer.TitleName });
                menu.Items.Add(new ToolStripSeparator());
                var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { });
                builder.Populate(menu.Items, false);
                MenuRenderer.Apply(menu);
                if (shown is not null)
                {
                    string now = menu.Items[MenuRenderer.TitleName]!.Text!;
                    Assert.Equal(cut, now.EndsWith('…'));
                    if (cut) Assert.StartsWith(now[..^1], title);
                    else Assert.Equal(title, now);
                }
                return menu.GetPreferredSize(Size.Empty).Width;
            }
            Assert.True(Width(title) <= Width(null), "The title widened the menu.");
        });
    }

    // The owner, 26 Nov: the background's sound, next and change left Effects for the Glass and background panel,
    // which has the same sliders as Settings. Effects holds the effects only.
    // 27 Nov: Glass and Dark mode moved up to Look, one click away, and the dark title bar moved into Effects.
    [Fact]
    public void Effects_holds_only_the_effects()
    {
        OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var menu = new ContextMenuStrip();
            var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { });
            builder.Populate(menu.Items, false);
            ToolStripMenuItem effects = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == UiText.T("menu.effects"));
            Assert.Equal([AppCommand.GlassLevels, AppCommand.KeepAreas, AppCommand.ToggleShrink, AppCommand.ToggleCrop, (AppCommand)(-1), AppCommand.ToggleDarkTitleBar],
                effects.DropDownItems.OfType<ToolStripItem>().Select(item => item.Tag is AppCommand command ? command : (AppCommand)(-1)));
            // Under Look: Glass, Dark mode, Borderless and Fill display, then Effects.
            int look = menu.Items.IndexOf(menu.Items.OfType<ToolStripLabel>().Single(item => item.Text == UiText.T("menu.group.look")));
            Assert.Equal(new AppCommand?[] { AppCommand.ToggleGlass, AppCommand.ToggleDark, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill },
                Enumerable.Range(look + 1, 4).Select(index => menu.Items[index].Tag as AppCommand?));
            Assert.Same(effects, menu.Items[look + 5]);
            Assert.Equal(UiText.T("window.keepAreas"), effects.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Tag is AppCommand.KeepAreas).Text);
            Assert.Equal("Select areas to keep", UiText.T("window.keepAreas"));
        });
    }

    /// <summary>
    /// The toolbar's Window menu offers what the window menu has that the bar doesn't: without the added buttons,
    /// Minimize to an Anchor, the title bar and frame choices and the window's details. Anything
    /// shown as a button is left out, Arrange window with its grid too; with fewer buttons, more stays.
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
            // With every button, the menu keeps only what no button has: the window's details.
            ToolStripItemCollection every = Built(CommandCatalog.ToolbarButtons.ToHashSet());
            Assert.Equal(Screen.AllScreens.Length > 1 ? new[] { AppCommand.ToggleBorderlessFill, AppCommand.ShowWindowInfo } : new[] { AppCommand.ShowWindowInfo },
                Commands(every).Select(item => item.Tag).OfType<AppCommand>());
            Assert.DoesNotContain(every.Cast<ToolStripItem>(), item => item is OpacityRow);
            Assert.Contains(every.Cast<ToolStripItem>(), item => item.Text == UiText.T("common.copy"));
            Tidy(every);
            ToolStripItemCollection all = Built(CommandCatalog.ToolbarButtons.Except(CommandCatalog.AddedToolbarButtons).ToHashSet());
            AppCommand[] left = Commands(all).Select(item => item.Tag).OfType<AppCommand>().ToArray();
            Assert.Equal([AppCommand.MinimizeToAnchor, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.GlassLevels,
                AppCommand.KeepAreas, AppCommand.ToggleDarkTitleBar, AppCommand.ShowWindowInfo], left);
            Assert.DoesNotContain(all.Cast<ToolStripItem>(), item => item is PlacementRow);
            Assert.DoesNotContain(all.Cast<ToolStripItem>(), item => item is OpacityRow);
            Assert.Contains(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("window.openLocation"));
            Assert.Contains(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("common.copy"));
            Assert.DoesNotContain(all.Cast<ToolStripItem>(), item => item.Text == UiText.T("menu.more"));
            Tidy(all);

            // A bar with only Pin keeps the slider, Arrange with its grid, and the effects.
            ToolStripItemCollection few = Built(new HashSet<AppCommand> { AppCommand.TogglePin });
            Tidy(few);
            Assert.Contains(few.Cast<ToolStripItem>(), item => item is OpacityRow);
            Assert.Single(Commands(few).Single(item => Equals(item.Tag, AppCommand.Arrange)).DropDownItems.OfType<PlacementRow>());
            Assert.DoesNotContain(Commands(few), item => Equals(item.Tag, AppCommand.TogglePin));
            foreach (AppCommand command in new[] { AppCommand.Arrange, AppCommand.ToggleDark, AppCommand.RestoreWindow, AppCommand.SaveWindowSetup, AppCommand.ToggleDefaultOpacity })
                Assert.Contains(Commands(few), item => Equals(item.Tag, command));
        });

        // No heading without anything under it, and no divider at an end or beside another, in any submenu either.
        static void Tidy(ToolStripItemCollection items)
        {
            ToolStripItem[] rows = items.Cast<ToolStripItem>().ToArray();
            Assert.NotEmpty(rows);
            Assert.IsNotType<ToolStripSeparator>(rows[0]);
            Assert.IsNotType<ToolStripSeparator>(rows[^1]);
            for (int index = 0; index < rows.Length; index++)
            {
                ToolStripItem? next = index + 1 < rows.Length ? rows[index + 1] : null;
                if (rows[index] is ToolStripSeparator) Assert.IsNotType<ToolStripSeparator>(next);
                if (Equals(rows[index].Tag, MenuParts.HeaderTag)) Assert.True(next is ToolStripMenuItem, rows[index].Text);
                if (rows[index] is ToolStripMenuItem { HasDropDownItems: true } parent) Tidy(parent.DropDownItems);
            }
        }
    }

    // 27 Nov: the window menu, the toolbar and Settings' Actions and shortcuts share one grouping and order.
    [Fact]
    public void The_window_menu_lists_the_toolbar_buttons_in_the_toolbar_order()
    {
        OnUiThread(() =>
        {
            using var menu = new ContextMenuStrip();
            var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
                { OpacityPercent = _ => 100, SetOpacityPercent = (_, _) => { } };
            builder.Populate(menu.Items, false);
            AppCommand[] inMenu = [.. Commands(menu.Items).Select(item => item.Tag).OfType<AppCommand>()];
            Assert.Equal(CommandCatalog.ToolbarButtons.Where(inMenu.Contains), inMenu.Where(CommandCatalog.ToolbarButtons.Contains));
            Assert.Equal(CommandCatalog.ToolbarButtons.Length - 1, inMenu.Count(CommandCatalog.ToolbarButtons.Contains)); // All but the Window menu button.
            // Each gap on the bar is a divider, a heading or the end of a submenu in the menu. More, which has no button,
            // shares Arrange's group.
            Assert.All(CommandCatalog.ToolbarButtons.Where(CommandCatalog.EndsGroup).SkipLast(1), command =>
            {
                ToolStripItem row = Commands(menu.Items).Single(item => Equals(item.Tag, command));
                ToolStripItemCollection siblings = row.Owner!.Items;
                int at = siblings.IndexOf(row) + 1;
                if (at < siblings.Count && siblings[at].Text == UiText.T("menu.more")) at++;
                ToolStripItem? next = at < siblings.Count ? siblings[at] : null;
                Assert.True(next is null or ToolStripSeparator || Equals(next.Tag, MenuParts.HeaderTag), command.ToString());
            });
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
                // The nine places are the grid at the top of Arrange, not rows; everything else is a command row.
                foreach (AppCommand command in CommandCatalog.Ordered.Where(command => command != AppCommand.RestoreAll))
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                Assert.DoesNotContain(commands, item => item.Tag is AppCommand command && CommandCatalog.AnchorOf(command) is not null);
                ToolStripMenuItem arrange = Commands(menu.Items).Single(item => Equals(item.Tag, AppCommand.Arrange));
                Assert.IsType<PlacementRow>(arrange.DropDownItems[0]);
                Assert.IsType<ToolStripSeparator>(arrange.DropDownItems[1]);
                AccessibleObject places = ((PlacementRow)arrange.DropDownItems[0]).Control.AccessibilityObject;
                Assert.Equal(CommandCatalog.All.Where(command => CommandCatalog.AnchorOf(command) is not null).Select(CommandCatalog.Name).Order(),
                    Enumerable.Range(0, places.GetChildCount()).Select(index => places.GetChild(index)!.Name).Order());
                foreach (AppCommand command in new[] { AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar, AppCommand.ShowWindowInfo })
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                Assert.All(commands.Where(item => !Equals(item.Tag, AppCommand.RestoreAll)), item => Assert.False(item.Enabled));
                Assert.DoesNotContain(commands, item => Equals(item.Tag, AppCommand.RestoreAll));
                Assert.Single(commands, item => Equals(item.Tag, AppCommand.ShowWindowInfo));
                AppCommand[] rest = [.. CommandCatalog.ArrangeMenu.OfType<AppCommand>().Where(command => CommandCatalog.AnchorOf(command) is null)];
                Assert.Equal(rest, arrange.DropDownItems.OfType<ToolStripMenuItem>().Select(item => (AppCommand)item.Tag!));
                // The toolbar's Arrange button opens the same menu.
                using var toolbar = new ContextMenuStrip();
                builder.PopulateArrange(toolbar.Items);
                Assert.IsType<PlacementRow>(toolbar.Items[0]);
                Assert.Equal(rest, toolbar.Items.OfType<ToolStripMenuItem>().Select(item => (AppCommand)item.Tag!));
                Assert.Contains(Commands(menu.Items), item => item.Text == UiText.T("common.copy"));
                // Group headings and the two rows that hold a control are not commands, and nothing lists them as such.
                Assert.Equal(2, menu.Items.OfType<ToolStripLabel>().Count());
                Assert.Empty(menu.Items.OfType<PlacementRow>());
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

// 27 Nov: the grid of places moved into Arrange window. Open on screen, it shows there, and choosing a place closes
// every menu, not just Arrange, before the window moves.
[Trait("Category", "RealWindows")]
public sealed class WindowMenuOnScreenTests
{
    [Fact]
    public void The_grid_shows_in_arrange_and_a_place_closes_every_menu()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            int stale = 0;
            using var menu = new ContextMenuStrip();
            var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => stale++)
                { OpacityPercent = _ => 80, SetOpacityPercent = (_, _) => { }, MoveToDisplay = (_, _) => { }, ResizeTo = (_, _) => { } };
            builder.Populate(menu.Items, false);
            MenuRenderer.Apply(menu);
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            menu.Show(new Point(area.Left + 40, area.Top + 40));
            ToolStripMenuItem arrange = menu.Items.OfType<ToolStripMenuItem>().Single(item => Equals(item.Tag, AppCommand.Arrange));
            arrange.ShowDropDown();
            Assert.True(Desktop.WaitFor(() => arrange.DropDown.Visible, 1000, Application.DoEvents));
            var grid = (PlacementRow)arrange.DropDownItems[0];
            OpacityRow slider = menu.Items.OfType<OpacityRow>().Single();
            Assert.True(grid.Control.Visible && grid.Control.Width > 100 && grid.Control.Height > 50);
            Assert.True(slider.Control.Visible && slider.Control.Width > 100);
            Assert.True(arrange.DropDown.Bounds.Contains(grid.Control.RectangleToScreen(grid.Control.ClientRectangle)));
            ViewMenuLayoutTests.Save("menu-on-screen-arrange", menu, arrange.DropDown);
            // With two displays or more, Fill display asks which one.
            ToolStripMenuItem fill = menu.Items.OfType<ToolStripMenuItem>().Single(item => Equals(item.Tag, AppCommand.ToggleBorderlessFill));
            Assert.Equal(Screen.AllScreens.Length > 1 ? Screen.AllScreens.Length : 0, fill.DropDownItems.Count);
            if (Screen.AllScreens.Length > 1)
            {
                arrange.HideDropDown();
                fill.ShowDropDown();
                Assert.True(Desktop.WaitFor(() => fill.DropDown.Visible, 1000, Application.DoEvents));
                Assert.Equal(Screen.AllScreens.Select(screen => screen.DeviceName), fill.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text));
                ViewMenuLayoutTests.Save("menu-on-screen-fill", menu, fill.DropDown);
                fill.HideDropDown();
                arrange.ShowDropDown();
                Assert.True(Desktop.WaitFor(() => arrange.DropDown.Visible, 1000, Application.DoEvents));
            }
            else ViewMenuLayoutTests.Save("menu-on-screen-one-display", menu);
            grid.Control.AccessibilityObject.GetChild(2)!.DoDefaultAction();
            Assert.True(Desktop.WaitFor(() => !menu.Visible && !arrange.DropDown.Visible, 1000, Application.DoEvents));
            Assert.Equal(1, stale); // No window here: the place was asked for, and found none to move.
        });
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
