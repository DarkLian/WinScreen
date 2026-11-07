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
                foreach (AppCommand command in CommandCatalog.Ordered.Where(command => tray || command != AppCommand.RestoreAll))
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                foreach (AppCommand command in new[] { AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar, AppCommand.ShowWindowInfo })
                    Assert.Contains(commands, item => Equals(item.Tag, command));
                Assert.All(commands.Where(item => !Equals(item.Tag, AppCommand.RestoreAll)), item => Assert.False(item.Enabled));
                if (tray) Assert.True(Assert.Single(commands, item => Equals(item.Tag, AppCommand.RestoreAll)).Enabled);
                else Assert.DoesNotContain(commands, item => Equals(item.Tag, AppCommand.RestoreAll));
                Assert.Contains(menu.Items.OfType<ToolStripMenuItem>(), item => item.Text == UiText.T("common.copy"));
            }
        });
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
                         "window.copy.all", "pip.hideSwitcher", "gestures.menu.ctrlRight", "gestures.menu.ctrlShiftRight", "gestures.menu.ctrlMiddle",
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
