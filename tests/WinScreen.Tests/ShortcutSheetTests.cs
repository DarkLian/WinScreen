using WinScreen.Localization;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;
using Step = WinScreen.Tray.ShortcutSheetTrigger.Step;

namespace WinScreen.Tests;

/// <summary>Holding Ctrl+Alt alone for a moment shows the shortcuts (the owner's idea, 27 Nov).</summary>
public sealed class ShortcutSheetTests
{
    private const int Ctrl = 0x11, Alt = 0x12, RightAlt = 0xA5, T = 0x54, E = 0x45;
    private const int Hold = ShortcutSheetTrigger.HoldMilliseconds;

    [Fact]
    public void Ctrl_and_Alt_held_alone_for_a_moment_show_it_and_letting_go_hides_it()
    {
        var trigger = new ShortcutSheetTrigger();
        Assert.Equal(Step.Nothing, trigger.Key(Ctrl, true, 0));
        Assert.Equal(Step.Wait, trigger.Key(Alt, true, 10));
        Assert.False(trigger.Due(Hold - 100));
        Assert.Equal(Step.Nothing, trigger.Key(Ctrl, true, 300)); // A held key repeats; the wait goes on from the start.
        Assert.Equal(Step.Nothing, trigger.Key(Alt, true, 600));
        Assert.True(trigger.Due(Hold + 10));
        trigger.Showing();
        Assert.False(trigger.Due(Hold + 500)); // Once.
        Assert.Equal(Step.Hide, trigger.Key(Alt, false, Hold + 600));
        Assert.Equal(Step.Nothing, trigger.Key(Ctrl, false, Hold + 700));
    }

    [Fact]
    public void A_key_pressed_with_them_is_a_shortcut_or_typing_and_never_shows_it()
    {
        var trigger = new ShortcutSheetTrigger();
        trigger.Key(Ctrl, true, 0);
        Assert.Equal(Step.Wait, trigger.Key(RightAlt, true, 0)); // AltGr comes as Ctrl and right Alt together.
        trigger.Key(E, true, 150);
        trigger.Key(E, false, 200);
        Assert.False(trigger.Due(Hold * 3)); // Still held after the letter: still typing, not asking.

        trigger.Key(Ctrl, false, Hold * 3);
        trigger.Key(RightAlt, false, Hold * 3);
        trigger.Key(Ctrl, true, Hold * 4);
        Assert.Equal(Step.Wait, trigger.Key(Alt, true, Hold * 4)); // Let go of both, and it can come again.
        Assert.True(trigger.Due(Hold * 5));
        trigger.Showing();
        Assert.Equal(Step.Hide, trigger.Key(T, true, Hold * 5 + 100)); // The shortcut pressed while it shows closes it.
        Assert.False(trigger.Due(Hold * 7));
    }

    [Fact]
    public void The_mouse_used_with_them_is_the_opacity_wheel_or_a_drag_and_never_shows_it()
    {
        var trigger = new ShortcutSheetTrigger();
        trigger.Key(Ctrl, true, 0);
        trigger.Key(Alt, true, 0);
        Assert.Equal(Step.Nothing, trigger.Mouse());
        Assert.False(trigger.Due(Hold * 2));

        var shown = new ShortcutSheetTrigger();
        shown.Key(Ctrl, true, 0);
        shown.Key(Alt, true, 0);
        shown.Showing();
        Assert.Equal(Step.Hide, shown.Mouse());
    }

    [Fact]
    public void Keys_Windows_took_away_are_put_right_by_what_is_really_held()
    {
        var trigger = new ShortcutSheetTrigger();
        trigger.Key(Ctrl, true, 0);
        trigger.Key(Alt, true, 0);
        trigger.Correct(ctrl: false, alt: false); // Ctrl+Alt+Del: the secure desktop had the key-ups.
        Assert.False(trigger.Due(Hold * 2));
        trigger.Key(Ctrl, true, Hold * 3);
        Assert.Equal(Step.Wait, trigger.Key(Alt, true, Hold * 3)); // Not taken for a key still held from before.
        Assert.True(trigger.Due(Hold * 4));
    }

    [Fact]
    public void The_sheet_lists_only_shortcuts_that_work_in_Settings_groups_then_the_mouse()
    {
        UiText.Initialize("en");
        var settings = new AppSettings { ModifierDrag = true, OpacityWheel = false, WindowMenu = true };
        settings.Hotkeys[AppCommand.ViewMute] = "Ctrl+Alt+M";
        IReadOnlyList<ShortcutSheetGroup> groups = ShortcutSheetContents.Of(settings, command => command != AppCommand.ToggleGlass);
        Assert.Equal(
            [SettingsWindow.GroupName(ActionGroup.Window), SettingsWindow.GroupName(ActionGroup.Effects), SettingsWindow.GroupName(ActionGroup.Views),
             SettingsWindow.GroupName(ActionGroup.Everywhere), UiText.T("actions.group.mouse")],
            groups.Select(group => group.Title));
        var rows = groups.SelectMany(group => group.Rows).ToList();
        Assert.Contains((CommandCatalog.Name(AppCommand.TogglePin), "Ctrl+Alt+T"), rows);
        Assert.Contains((CommandCatalog.Name(AppCommand.ViewMute), "Ctrl+Alt+M"), rows);
        Assert.DoesNotContain(rows, row => row.Keys == "Ctrl+Alt+G"); // Another app has it.
        Assert.Equal([UiText.T("features.moveResize"), UiText.T("features.windowMenu")], groups[^1].Rows.Select(row => row.Name));
    }

    /// <summary>Shown over a window in use, it leaves that window active and lets its clicks through.</summary>
    [Fact]
    [Trait("Category", "RealWindows")]
    public void The_sheet_never_takes_the_focus_and_lets_clicks_through()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            Desktop.GainForegroundRights();
            using var target = new Form { Text = "Shortcut sheet target", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 160, 700, 500) };
            target.Show();
            Desktop.Activate(target.Handle);
            void Pump() { for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); } }
            Pump();
            Assert.Equal(target.Handle, User32.GetForegroundWindow());

            var settings = new AppSettings { WindowMenu = true };
            using var sheet = new ShortcutSheetWindow(ShortcutSheetContents.Of(settings, _ => true));
            Point center = new(target.Left + target.Width / 2, target.Top + target.Height / 2);
            sheet.ShowAround(center);
            Pump();
            Assert.True(sheet.Visible);
            Assert.Equal(target.Handle, User32.GetForegroundWindow());
            Rectangle area = Screen.FromPoint(center).WorkingArea;
            Assert.True(area.Contains(sheet.Bounds), $"{sheet.Bounds} is not all on {area}.");
            Point middle = new(sheet.Left + sheet.Width / 2, sheet.Top + sheet.Height / 2);
            nint under = User32.WindowFromPoint(new NativePoint { X = middle.X, Y = middle.Y });
            Assert.NotEqual(sheet.Handle, under);
            Assert.Contains(UiText.T("actions.sheet.title"), sheet.Lines);
            Assert.Contains("Ctrl+Alt+T", sheet.Lines);
            Assert.Contains(UiText.T("gestures.menu.ctrlRight"), sheet.Lines);
        });
    }
}
