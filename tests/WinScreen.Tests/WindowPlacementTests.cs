using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PlacementGeometryTests
{
    private static readonly Rectangle Work = new(-1920, 40, 1920, 1000);
    private static readonly Size Frame = new(400, 300);

    [Theory]
    [InlineData(WindowAnchor.TopLeft, -1920, 40)]
    [InlineData(WindowAnchor.Top, -1160, 40)]
    [InlineData(WindowAnchor.TopRight, -400, 40)]
    [InlineData(WindowAnchor.Left, -1920, 390)]
    [InlineData(WindowAnchor.Center, -1160, 390)]
    [InlineData(WindowAnchor.Right, -400, 390)]
    [InlineData(WindowAnchor.BottomLeft, -1920, 740)]
    [InlineData(WindowAnchor.Bottom, -1160, 740)]
    [InlineData(WindowAnchor.BottomRight, -400, 740)]
    public void Nine_anchors_work_on_a_monitor_with_negative_coordinates(WindowAnchor anchor, int x, int y) =>
        Assert.Equal(new Point(x, y), PlacementGeometry.Align(Frame, Work, anchor));

    [Fact]
    public void A_frame_bigger_than_the_screen_starts_at_the_corner() =>
        Assert.Equal(Work.Location, PlacementGeometry.Align(new Size(3000, 2000), Work, WindowAnchor.BottomRight));

    [Fact]
    public void Invisible_margins_are_added_back_so_the_visible_frame_lands_on_target()
    {
        var outer = new Rectangle(93, 100, 414, 307);
        var frame = new Rectangle(100, 100, 400, 300);
        Rectangle wanted = PlacementGeometry.OuterFor(new Rectangle(0, 0, 400, 300), frame, outer);
        Assert.Equal(new Rectangle(-7, 0, 414, 307), wanted);
    }

    [Fact]
    public void Moving_between_screens_keeps_the_relative_place()
    {
        var left = new Rectangle(0, 0, 1000, 1000);
        var right = new Rectangle(1000, 0, 2000, 2000);
        Assert.Equal(new Point(2350, 1350), PlacementGeometry.SamePlaceOn(new Rectangle(600, 600, 200, 200), left, right));
        Assert.Equal(right.Location, PlacementGeometry.SamePlaceOn(new Rectangle(0, 0, 100, 100), left, right));
    }

    [Theory]
    [InlineData(0, 1, -1)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]
    [InlineData(-1, 3, 1)]
    public void The_next_display_wraps_round(int current, int count, int expected) =>
        Assert.Equal(expected, PlacementGeometry.NextDisplay(current, count));

    [Fact]
    public void Every_alignment_command_maps_to_its_anchor()
    {
        Assert.Equal(WindowAnchor.TopLeft, CommandCatalog.AnchorOf(AppCommand.AlignTopLeft));
        Assert.Equal(WindowAnchor.Center, CommandCatalog.AnchorOf(AppCommand.AlignCenter));
        Assert.Equal(WindowAnchor.BottomRight, CommandCatalog.AnchorOf(AppCommand.AlignBottomRight));
        Assert.Null(CommandCatalog.AnchorOf(AppCommand.Arrange));
        Assert.Null(CommandCatalog.AnchorOf(AppCommand.TogglePin));
    }

    [Fact]
    public void New_commands_start_without_a_hotkey_so_they_cannot_clash_on_upgrade()
    {
        var settings = new AppSettings();
        Assert.All(CommandCatalog.Extra.Append(AppCommand.Arrange), command => Assert.False(settings.Hotkeys.ContainsKey(command)));
    }
}

[Trait("Category", "RealWindows")]
public sealed class WindowPlacementTests
{
    private static readonly Rectangle Spot = new(240, 200, 400, 300);

    [Fact]
    public void Minimum_size_refusals_are_reported_and_do_not_consume_undo()
    {
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        placer.SetFrame(target.Handle, new Rectangle(500, 200, 700, 500));
        target.SetMinimumSize(new Size(600, 400));
        Assert.Throws<WindowOperationException>(() => placer.SetFrame(target.Handle, new Rectangle(500, 200, 200, 100)));
        Assert.True(placer.RestorePrior(target.Handle)); // Undo the refused exact placement.
        Assert.Throws<WindowOperationException>(() => placer.RestorePrior(target.Handle)); // Original is below the new minimum.
        target.SetMinimumSize(new Size(100, 80));
        Assert.True(placer.RestorePrior(target.Handle));
        Assert.Equal(Spot, FrameOf(target.Handle));
    }

    private static Rectangle FrameOf(nint window)
    {
        Assert.True(WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _));
        return frame;
    }

    [Theory]
    [InlineData(WindowAnchor.TopLeft)]
    [InlineData(WindowAnchor.Center)]
    [InlineData(WindowAnchor.BottomRight)]
    public void The_visible_frame_lands_on_the_anchor(WindowAnchor anchor)
    {
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        Rectangle before = FrameOf(target.Handle);
        Rectangle area = Screen.FromHandle(target.Handle).WorkingArea;
        placer.Align(target.Handle, anchor);
        Point expected = PlacementGeometry.Align(before.Size, area, anchor);
        Rectangle after = FrameOf(target.Handle);
        Assert.InRange(Math.Abs(after.X - expected.X), 0, 1);
        Assert.InRange(Math.Abs(after.Y - expected.Y), 0, 1);
        Assert.Equal(before.Size, after.Size);
    }

    [Fact]
    public void Restore_prior_geometry_goes_back_and_restore_all_leaves_placement_alone()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        var placer = new WindowPlacer(windows);
        Rectangle before = FrameOf(target.Handle);
        placer.SetFrame(target.Handle, new Rectangle(500, 300, 500, 350));
        Rectangle moved = FrameOf(target.Handle);
        Assert.Equal(new Rectangle(500, 300, 500, 350), moved);
        Assert.Equal(0, windows.RestoreAll());
        Assert.Equal(moved, FrameOf(target.Handle));
        Assert.True(placer.RestorePrior(target.Handle));
        Assert.Equal(before, FrameOf(target.Handle));
        Assert.False(placer.RestorePrior(target.Handle));
    }

    [Fact]
    public void A_minimized_window_stays_minimized_with_a_new_place_to_return_to()
    {
        using var target = new TargetProcess(new Rectangle(900, 600, 200, 150));
        // Not the form itself: on Windows 10, WinForms puts a restored form back where its own code placed it.
        nint window = target.CreatePlainWindow(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        User32.ShowWindow(window, 6);
        Assert.True(Desktop.WaitFor(() => User32.IsIconic(window), 2000));
        placer.SetFrame(window, new Rectangle(500, 300, 400, 300));
        Assert.True(User32.IsIconic(window));
        User32.ShowWindow(window, User32.SW_SHOWNOACTIVATE);
        Assert.True(Desktop.WaitFor(() => !User32.IsIconic(window), 2000));
        Rectangle frame = FrameOf(window);
        Assert.InRange(Math.Abs(frame.X - 500), 0, 12);
        Assert.InRange(Math.Abs(frame.Y - 300), 0, 12);
    }

    [Fact]
    public void One_display_says_so_rather_than_doing_nothing()
    {
        if (Screen.AllScreens.Length > 1) return;
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        Assert.Throws<WindowOperationException>(() => placer.MoveToNextDisplay(target.Handle));
    }

    [Fact]
    public void Refused_windows_are_not_moved()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController(["WinScreen.TestTarget"]);
        var placer = new WindowPlacer(windows);
        Rectangle before = FrameOf(target.Handle);
        Assert.Throws<WindowOperationException>(() => placer.Align(target.Handle, WindowAnchor.TopLeft));
        Assert.Equal(before, FrameOf(target.Handle));
    }

    [Fact]
    public void A_pinned_window_refuses_to_go_to_the_bottom()
    {
        using var target = new TargetProcess(Spot);
        Desktop.GainForegroundRights();
        Desktop.Activate(target.Handle);
        var windows = new WindowController([]);
        windows.SendToBottom(target.Handle);
        windows.TogglePin(target.Handle);
        Assert.Throws<WindowOperationException>(() => windows.SendToBottom(target.Handle));
        windows.RestoreAll();
    }

    [Fact]
    public void Window_information_describes_the_window()
    {
        using var target = new TargetProcess(Spot, title: "Facts target");
        var facts = WindowInfo.Facts(target.Handle).ToDictionary(fact => fact.Label, fact => fact.Value);
        Assert.Equal("Facts target", facts["Title"]);
        Assert.Equal("WinScreen.TestTarget", facts["Program"]);
        Assert.Equal("No", facts["Runs as administrator"]);
    }
}
