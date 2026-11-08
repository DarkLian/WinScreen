using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>These act on real windows in other processes and read the result back from Windows.</summary>
[Trait("Category", "RealWindows")]
public class WindowControllerTests
{
    private static readonly Rectangle Spot = new(220, 220, 300, 200);

    private static WindowController NewController(params string[] excluded) => new(excluded);

    [Fact]
    public void Pin_keeps_a_window_above_a_normal_window_made_active_after_it()
    {
        using var pinned = new TargetProcess(Spot);
        using var active = new TargetProcess(Spot, "1E64C8");
        Desktop.GainForegroundRights();
        var windows = NewController();

        Assert.True(windows.TogglePin(pinned.Handle));
        Desktop.Activate(active.Handle);

        Assert.NotEqual(0, Desktop.ExStyle(pinned.Handle) & User32.WS_EX_TOPMOST);
        Assert.Equal(pinned.Handle, Desktop.TopWindowAt(active.Center));

        Assert.False(windows.TogglePin(pinned.Handle));
        Assert.Equal(0, Desktop.ExStyle(pinned.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void Pin_works_on_a_window_that_is_not_active()
    {
        using var target = new TargetProcess(Spot);
        using var active = new TargetProcess(new Rectangle(560, 220, 200, 200), "1E64C8");
        Desktop.GainForegroundRights();
        Desktop.Activate(active.Handle);

        Assert.True(NewController().TogglePin(target.Handle));

        Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void Opacity_moves_in_steps_and_stops_at_both_ends()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();

        Assert.Equal(229, windows.ChangeOpacity(target.Handle, -WindowController.OpacityStep));
        Assert.Equal((byte?)229, Desktop.Alpha(target.Handle));

        for (int i = 0; i < 20; i++)
            windows.ChangeOpacity(target.Handle, -WindowController.OpacityStep);
        Assert.Equal(WindowController.MinimumOpacity, Desktop.Alpha(target.Handle));

        for (int i = 0; i < 20; i++)
            windows.ChangeOpacity(target.Handle, WindowController.OpacityStep);
        Assert.Equal((byte?)255, Desktop.Alpha(target.Handle));
    }

    [Fact]
    public void The_default_level_toggles_on_and_off()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        windows.DefaultOpacity = 128;

        Assert.Equal(128, windows.ToggleDefaultOpacity(target.Handle));
        Assert.Equal((byte?)128, Desktop.Alpha(target.Handle));

        Assert.Equal(255, windows.ToggleDefaultOpacity(target.Handle));
        Assert.Equal((byte?)255, Desktop.Alpha(target.Handle));
    }

    [Fact]
    public void Click_through_uses_the_default_level()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        windows.DefaultOpacity = 128;

        windows.ToggleClickThrough(target.Handle);

        Assert.Equal((byte?)128, Desktop.Alpha(target.Handle));
    }

    [Fact]
    public void A_crop_keeps_one_part_and_lets_clicks_through_elsewhere()
    {
        using var back = new TargetProcess(Spot, "000000");
        using var front = new TargetProcess(Spot, "1E64C8");
        Desktop.GainForegroundRights();
        Desktop.Activate(back.Handle);
        Desktop.Activate(front.Handle);
        var windows = NewController();
        var kept = new Rectangle(0, 0, 120, 200);

        windows.Crop(front.Handle, kept);

        Assert.True(windows.IsCropped(front.Handle));
        Assert.Equal(kept, WindowInfo.ShapeOf(front.Handle));
        var inside = new Point(Spot.Left + 60, Spot.Top + 100);
        var outside = new Point(Spot.Right - 60, Spot.Top + 100);
        Assert.Equal(front.Handle, Desktop.TopWindowAt(inside));
        Assert.Equal(back.Handle, Desktop.TopWindowAt(outside));

        windows.RemoveCrop(front.Handle);

        Assert.False(windows.IsCropped(front.Handle));
        Assert.Null(WindowInfo.ShapeOf(front.Handle));
        Assert.Equal(front.Handle, Desktop.TopWindowAt(outside));
    }

    [Fact]
    public void Restoring_removes_a_crop()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        windows.Crop(target.Handle, new Rectangle(10, 10, 100, 100));

        Assert.Equal(0, windows.RestoreAll());

        Assert.Null(WindowInfo.ShapeOf(target.Handle));
    }

    [Fact]
    public void A_crop_left_by_a_crash_is_removed_at_the_next_start()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "journal.json");
        using var target = new TargetProcess(Spot);
        new WindowController([], new ChangeJournal(path)).Crop(target.Handle, new Rectangle(10, 10, 100, 100));
        Assert.NotNull(WindowInfo.ShapeOf(target.Handle));

        new WindowController([], new ChangeJournal(path)).RecoverFromJournal();

        Assert.Null(WindowInfo.ShapeOf(target.Handle));
    }

    [Fact]
    public void A_window_with_its_own_shape_is_not_cropped()
    {
        using var target = new TargetProcess(Spot);
        User32.SetWindowRgn(target.Handle, User32.CreateRectRgn(0, 0, 50, 50), true);

        Assert.Throws<WindowOperationException>(() =>
            NewController().Crop(target.Handle, new Rectangle(10, 10, 100, 100)));

        Assert.Equal(new Rectangle(0, 0, 50, 50), WindowInfo.ShapeOf(target.Handle));
    }

    [Fact]
    public void Only_visible_pinned_windows_are_listed_for_the_highlight()
    {
        using var target = new TargetProcess(Spot);
        Desktop.GainForegroundRights();
        var windows = NewController();
        Assert.Empty(windows.VisiblePinnedWindows());

        windows.TogglePin(target.Handle);
        Assert.Equal([target.Handle], windows.VisiblePinnedWindows());

        windows.Hide(target.Handle);
        Assert.Empty(windows.VisiblePinnedWindows());

        windows.Show(target.Handle);
        windows.RestoreAll();
        Assert.Empty(windows.VisiblePinnedWindows());
    }

    [Fact]
    public void An_include_list_keeps_every_other_program_out()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        Assert.True(windows.CanControl(target.Handle));

        windows.SetIncluded(["some-other-program"]);
        Assert.False(windows.CanControl(target.Handle));

        windows.SetIncluded(["winscreen.testtarget"]);
        Assert.True(windows.CanControl(target.Handle));

        windows.SetExcluded(["WinScreen.TestTarget"]);
        Assert.False(windows.CanControl(target.Handle));
    }

    [Fact]
    public void Opacity_is_really_visible_on_screen()
    {
        using var back = new TargetProcess(Spot, "000000");
        using var front = new TargetProcess(Spot, "FFFFFF");
        Desktop.GainForegroundRights();
        Desktop.Activate(back.Handle);
        Desktop.Activate(front.Handle);
        var windows = NewController();
        var sample = new Rectangle(front.Center.X - 20, front.Center.Y - 20, 40, 40);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)));

        windows.ChangeOpacity(front.Handle, -127);
        bool half = Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.FromArgb(128, 128, 128)));

        Color seen = Desktop.ColorOnScreen(sample);
        windows.RestoreAll();
        Assert.True(half, $"Expected mid grey through a half-transparent white window, saw {seen}.");
    }

    [Fact]
    public void Click_through_lets_the_window_underneath_receive_the_mouse()
    {
        using var back = new TargetProcess(Spot);
        using var front = new TargetProcess(Spot, "1E64C8");
        Desktop.GainForegroundRights();
        Desktop.Activate(back.Handle);
        Desktop.Activate(front.Handle);
        var windows = NewController();
        Assert.Equal(front.Handle, Desktop.TopWindowAt(front.Center));

        Assert.True(windows.ToggleClickThrough(front.Handle));
        Assert.Equal(WindowController.ClickThroughOpacity, Desktop.Alpha(front.Handle));
        Assert.True(Desktop.WaitFor(() => Desktop.TopWindowAt(front.Center) == back.Handle));

        Assert.False(windows.ToggleClickThrough(front.Handle));
        Assert.True(Desktop.WaitFor(() => Desktop.TopWindowAt(front.Center) == front.Handle));
        windows.RestoreAll();
    }

    [Fact]
    public void Restore_all_undoes_every_change()
    {
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Desktop.GainForegroundRights();
        var windows = NewController();
        windows.TogglePin(target.Handle);
        windows.ChangeOpacity(target.Handle, -100);
        windows.ToggleClickThrough(target.Handle);

        Assert.Equal(0, windows.RestoreAll());

        Assert.Equal(before, Desktop.ExStyle(target.Handle));
        Assert.Null(Desktop.Alpha(target.Handle));
    }

    [Fact]
    public void Restore_all_pins_a_window_that_started_out_pinned()
    {
        using var target = new TargetProcess(Spot);
        using var other = new TargetProcess(new Rectangle(560, 220, 200, 200), "1E64C8");
        Desktop.GainForegroundRights();
        WindowController.Pin(target.Handle);
        var windows = NewController();

        Assert.False(windows.TogglePin(target.Handle));
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
        Desktop.Activate(other.Handle);

        Assert.Equal(0, windows.RestoreAll());

        Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void Restore_all_skips_windows_that_have_closed()
    {
        var windows = NewController();
        using (var target = new TargetProcess(Spot))
            windows.ChangeOpacity(target.Handle, -100);

        Assert.True(Desktop.WaitFor(() => windows.RestoreAll() == 0));
    }

    [Fact]
    public void A_new_copy_puts_back_what_a_killed_copy_left_changed()
    {
        string file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Desktop.GainForegroundRights();

        var killed = new WindowController([], new ChangeJournal(file));
        killed.TogglePin(target.Handle);
        killed.ChangeOpacity(target.Handle, -100);
        killed.Hide(target.Handle);
        killed.Park(target.Handle);
        Assert.NotEqual(Spot, Desktop.BoundsOf(target.Handle));

        // The first controller never gets to restore anything, as after a forced kill.
        new WindowController([], new ChangeJournal(file)).RecoverFromJournal();

        Assert.Equal(Spot, Desktop.BoundsOf(target.Handle));
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void See_through_windows_turn_solid_under_the_mouse_when_asked()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        windows.ChangeOpacity(target.Handle, -100);
        byte see = Desktop.Alpha(target.Handle)!.Value;

        windows.Refresh(target.Center, opaqueOnHover: true, pausePinWhenMaximized: false);
        Assert.Equal((byte?)255, Desktop.Alpha(target.Handle));

        windows.Refresh(new Point(0, 0), opaqueOnHover: true, pausePinWhenMaximized: false);
        Assert.Equal(see, Desktop.Alpha(target.Handle));

        windows.Refresh(target.Center, opaqueOnHover: false, pausePinWhenMaximized: false);
        Assert.Equal(see, Desktop.Alpha(target.Handle));
    }

    [Fact]
    public void Pin_pauses_while_maximized_and_returns_afterwards()
    {
        using var target = new TargetProcess(Spot);
        Desktop.GainForegroundRights();
        var windows = NewController();
        windows.TogglePin(target.Handle);

        const int maximize = 3;
        User32.ShowWindow(target.Handle, maximize);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle)));
        windows.Refresh(new Point(0, 0), opaqueOnHover: false, pausePinWhenMaximized: true);
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);

        User32.ShowWindow(target.Handle, User32.SW_RESTORE);
        Assert.True(Desktop.WaitFor(() => !User32.IsZoomed(target.Handle)));
        Desktop.Activate(target.Handle);
        windows.Refresh(new Point(0, 0), opaqueOnHover: false, pausePinWhenMaximized: true);
        Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void Excluded_list_can_change_while_running()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController();
        Assert.True(windows.CanControl(target.Handle));

        windows.SetExcluded(["WinScreen.TestTarget"]);

        Assert.False(windows.CanControl(target.Handle));
    }

    [Fact]
    public void A_full_screen_window_is_noticed()
    {
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        Assert.False(FullScreen.IsActive());
        using var target = new TargetProcess(screen);
        Desktop.GainForegroundRights();
        Desktop.Activate(target.Handle);
        Assert.True(Desktop.WaitFor(FullScreen.IsActive, 3000), "A full-screen window wasn't noticed.");
    }

    [Fact]
    public void Glass_shows_and_passes_clicks_through_the_background_only()
    {
        using var back = new TargetProcess(Spot, "1E64C8");
        using var front = new TargetProcess(Spot, "202020,202020,FFFFFF");
        Desktop.GainForegroundRights();
        Desktop.Activate(back.Handle);
        Desktop.Activate(front.Handle);
        var background = new Rectangle(Spot.Left + 40, Spot.Top + 80, 30, 30);
        var content = new Rectangle(Spot.Right - 60, Spot.Top + 80, 30, 30);
        Assert.True(Desktop.WaitFor(() =>
            Desktop.IsNear(Desktop.ColorOnScreen(background), Color.FromArgb(0x20, 0x20, 0x20))));

        var windows = NewController();
        Assert.True(windows.ToggleGlass(front.Handle));

        Color blue = Color.FromArgb(0x1E, 0x64, 0xC8);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(background), blue)),
            $"Background showed {Desktop.ColorOnScreen(background)}.");
        Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(content), Color.White),
            $"Content showed {Desktop.ColorOnScreen(content)}.");
        Assert.Equal(back.Handle, Desktop.TopWindowAt(new Point(background.X + 10, background.Y + 10)));
        Assert.Equal(front.Handle, Desktop.TopWindowAt(new Point(content.X + 10, content.Y + 10)));

        Assert.False(windows.ToggleGlass(front.Handle));
        Assert.True(
            Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(background), Color.FromArgb(0x20, 0x20, 0x20))),
            "Background didn't come back.");
    }

    [Fact]
    public void Restore_all_ends_glass()
    {
        using var target = new TargetProcess(Spot, "202020");
        long before = Desktop.ExStyle(target.Handle);
        Desktop.GainForegroundRights();
        Desktop.Activate(target.Handle);
        var windows = NewController();
        windows.ToggleGlass(target.Handle);

        Assert.Equal(0, windows.RestoreAll());

        Assert.Equal(before, Desktop.ExStyle(target.Handle));
    }

    [Fact]
    public void Excluded_programs_are_left_alone()
    {
        using var target = new TargetProcess(Spot);
        var windows = NewController("WinScreen.TestTarget");

        Assert.False(windows.CanControl(target.Handle));
        Assert.Throws<WindowOperationException>(() => windows.TogglePin(target.Handle));
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void Desktop_taskbar_and_own_windows_are_refused()
    {
        var windows = NewController();
        Assert.False(windows.CanControl(0));
        Assert.False(windows.CanControl(User32.GetDesktopWindow()));
        Assert.False(windows.CanControl(User32.GetShellWindow()));
        Assert.False(windows.CanControl(Desktop.FindWindow("Shell_TrayWnd", null)));
        using var own = new Form();
        Assert.False(windows.CanControl(own.Handle));
    }
}
