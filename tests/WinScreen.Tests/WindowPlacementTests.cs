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
    [InlineData(WindowAnchor.Top)]
    [InlineData(WindowAnchor.TopRight)]
    [InlineData(WindowAnchor.Left)]
    [InlineData(WindowAnchor.Center)]
    [InlineData(WindowAnchor.Right)]
    [InlineData(WindowAnchor.BottomLeft)]
    [InlineData(WindowAnchor.Bottom)]
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

    /// <summary>
    /// A maximized window fills its display wherever its place is, so a place chosen for it, or an exact position,
    /// brings it back to its usual size there. Back to previous position maximizes it again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_maximized_window_comes_back_to_its_size_in_the_place_chosen(bool exact)
    {
        using var target = new TargetProcess(Spot);
        target.UseTitleBar();
        var placer = new WindowPlacer(new WindowController([]));
        Rectangle usual = FrameOf(target.Handle);
        User32.ShowWindow(target.Handle, 3); // SW_MAXIMIZE
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000));
        Rectangle area = Screen.FromHandle(target.Handle).WorkingArea;
        Rectangle wanted = exact ? new Rectangle(area.Left + 100, area.Top + 80, 500, 320) : new Rectangle(area.Location, usual.Size);
        if (exact) placer.SetFrame(target.Handle, wanted);
        else placer.Align(target.Handle, WindowAnchor.TopLeft);
        Assert.False(User32.IsZoomed(target.Handle), "Still maximized.");
        Rectangle after = FrameOf(target.Handle);
        Assert.InRange(Math.Abs(after.X - wanted.X), 0, 1);
        Assert.InRange(Math.Abs(after.Y - wanted.Y), 0, 1);
        Assert.Equal(wanted.Size, after.Size);
        Assert.True(placer.RestorePrior(target.Handle));
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000), "Back to previous position didn't maximize it again.");
    }

    /// <summary>Exact position and size starts from where the window is, and what is typed in is where it goes.</summary>
    [Fact]
    public void Exact_position_and_size_starts_from_the_window_and_puts_it_where_typed()
    {
        using var target = new TargetProcess(Spot);
        Rectangle frame = FrameOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var dialog = new PlacementDialog("Exact", frame);
            Assert.Equal(frame, dialog.Frame);
            var boxes = Descendants(dialog).OfType<NumericUpDown>().ToArray();
            Assert.Equal(4, boxes.Length);
            (boxes[0].Value, boxes[1].Value, boxes[2].Value, boxes[3].Value) = (300, 260, 480, 330);
            Assert.Equal(new Rectangle(300, 260, 480, 330), dialog.Frame);
            new WindowPlacer(new WindowController([])).SetFrame(target.Handle, dialog.Frame);
        });
        Assert.Equal(new Rectangle(300, 260, 480, 330), FrameOf(target.Handle));

        static IEnumerable<Control> Descendants(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    }

    /// <summary>Send to bottom puts the window under the others, so one it covered is in front of it.</summary>
    [Fact]
    public void Send_to_bottom_puts_the_window_under_the_others()
    {
        using var under = new TargetProcess(new Rectangle(300, 260, 400, 300), "1E64C8", "Under");
        using var front = new TargetProcess(Spot, "C81E1E", "Front");
        Desktop.GainForegroundRights();
        Desktop.Activate(front.Handle);
        var overlap = new Point(350, 300);
        Assert.True(Desktop.WaitFor(() => User32.GetAncestor(Desktop.TopWindowAt(overlap), User32.GA_ROOT) == front.Handle, 2000));
        new WindowController([]).SendToBottom(front.Handle);
        Assert.True(Desktop.WaitFor(() => User32.GetAncestor(Desktop.TopWindowAt(overlap), User32.GA_ROOT) == under.Handle, 2000),
            "The window covered is still behind.");
    }

    /// <summary>Move to display, with the display the window is on, leaves it where it is.</summary>
    [Fact]
    public void Moving_to_its_own_display_leaves_the_window_where_it_is()
    {
        using var target = new TargetProcess(Spot);
        Rectangle before = FrameOf(target.Handle);
        new WindowPlacer(new WindowController([])).MoveToDisplay(target.Handle, Screen.FromHandle(target.Handle).DeviceName);
        Rectangle after = FrameOf(target.Handle);
        Assert.InRange(Math.Abs(after.X - before.X), 0, 1);
        Assert.InRange(Math.Abs(after.Y - before.Y), 0, 1);
        Assert.Equal(before.Size, after.Size);
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

    // Between displays. These run where there is more than one display: in the test VM, its "two-displays" checkpoint
    // has a second, virtual display to the left of the first, smaller, with no taskbar, while the first has its
    // taskbar at the top, so the two work areas start at different offsets from their displays.

    private static Screen OtherDisplay(nint window)
    {
        string current = Screen.FromHandle(window).DeviceName;
        return Screen.AllScreens.First(screen => screen.DeviceName != current);
    }

    private static string DisplayOf(nint window) => Screen.FromHandle(window).DeviceName;

    private static Rectangle OuterOf(nint window)
    {
        Assert.True(User32.GetWindowRect(window, out Rect rect));
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static void Near(Rectangle expected, Rectangle actual, int tolerance = 1, string what = "") =>
        Assert.True(Math.Abs(actual.Left - expected.Left) <= tolerance && Math.Abs(actual.Top - expected.Top) <= tolerance &&
            Math.Abs(actual.Width - expected.Width) <= tolerance && Math.Abs(actual.Height - expected.Height) <= tolerance,
            $"{what}{actual}, not {expected}.");

    /// <summary>Move to next display keeps the window's size and its place relative to the work area.</summary>
    [MultiDisplayFact]
    public void Move_to_next_display_keeps_the_size_and_relative_place()
    {
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        Screen[] screens = Screen.AllScreens;
        Screen from = Screen.FromHandle(target.Handle);
        Screen to = screens[PlacementGeometry.NextDisplay(Array.FindIndex(screens, screen => screen.DeviceName == from.DeviceName), screens.Length)];
        Rectangle before = FrameOf(target.Handle);
        placer.MoveToNextDisplay(target.Handle);
        Assert.Equal(to.DeviceName, DisplayOf(target.Handle));
        Near(PlacementGeometry.FrameOnDisplay(before, from.WorkingArea, to.WorkingArea, before.Size), FrameOf(target.Handle));
        Assert.True(placer.RestorePrior(target.Handle));
        Near(before, FrameOf(target.Handle));
    }

    /// <summary>Move to display takes the window to the display chosen, and back again.</summary>
    [MultiDisplayFact]
    public void Move_to_display_takes_the_window_to_the_display_chosen_and_back()
    {
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        Screen home = Screen.FromHandle(target.Handle), other = OtherDisplay(target.Handle);
        Rectangle before = FrameOf(target.Handle);
        placer.MoveToDisplay(target.Handle, other.DeviceName);
        Assert.Equal(other.DeviceName, DisplayOf(target.Handle));
        Rectangle there = FrameOf(target.Handle);
        Assert.Equal(before.Size, there.Size);
        Assert.True(other.WorkingArea.Contains(there), $"{there} isn't inside {other.WorkingArea}.");
        placer.MoveToDisplay(target.Handle, home.DeviceName);
        Assert.Equal(home.DeviceName, DisplayOf(target.Handle));
        Near(before, FrameOf(target.Handle), 2, "Back: "); // The relative place is rounded each way.
    }

    /// <summary>Fill display, on a display the window isn't on, covers that display and comes back from it.</summary>
    [MultiDisplayFact]
    public void Fill_display_covers_the_display_chosen_and_comes_back()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string journal = Path.Combine(Directory.CreateTempSubdirectory("winscreen-fill").FullName, "recovery.json");
        var windows = new WindowController([], new ChangeJournal(journal));
        Screen other = OtherDisplay(target.Handle);
        Rectangle before = OuterOf(target.Handle);
        try
        {
            Assert.True(windows.ToggleBorderless(target.Handle, fill: true, other.Bounds));
            Assert.Equal(other.Bounds, OuterOf(target.Handle));
            Assert.Equal(other.DeviceName, DisplayOf(target.Handle));
            Assert.False(windows.ToggleBorderless(target.Handle, fill: true, other.Bounds));
            Assert.Equal(before, OuterOf(target.Handle));
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>
    /// A maximized window moved to another display is maximized there, and un-maximizing it puts it at the same place
    /// on that display. Its stored place is measured from the work area, which starts at a different offset on each.
    /// </summary>
    [MultiDisplayTheory(Skip = "Known bug (12 Nov 2026): Move to display leaves a maximized window maximized on its own display.")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_maximized_window_moved_to_another_display_is_maximized_there_and_comes_back_there(bool startOnOther)
    {
        using var target = new TargetProcess(Spot);
        target.UseTitleBar();
        var placer = new WindowPlacer(new WindowController([]));
        if (startOnOther) placer.MoveToDisplay(target.Handle, OtherDisplay(target.Handle).DeviceName);
        Rectangle usual = FrameOf(target.Handle);
        Screen from = Screen.FromHandle(target.Handle), to = OtherDisplay(target.Handle);
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000));
        placer.MoveToDisplay(target.Handle, to.DeviceName);
        Assert.True(User32.IsZoomed(target.Handle), "No longer maximized.");
        Assert.Equal(to.DeviceName, DisplayOf(target.Handle));
        Near(to.WorkingArea, FrameOf(target.Handle), 1, "Maximized: ");
        User32.ShowWindow(target.Handle, User32.SW_RESTORE);
        Assert.True(Desktop.WaitFor(() => !User32.IsZoomed(target.Handle), 2000));
        Near(PlacementGeometry.FrameOnDisplay(usual, from.WorkingArea, to.WorkingArea, usual.Size), FrameOf(target.Handle), 1, "Restored: ");
    }

    /// <summary>Arrange on a window maximized on a display places it on that display, and Back maximizes it there again.</summary>
    [MultiDisplayTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arranging_a_maximized_window_places_it_on_its_own_display(bool onOther)
    {
        using var target = new TargetProcess(Spot);
        target.UseTitleBar();
        var placer = new WindowPlacer(new WindowController([]));
        if (onOther) placer.MoveToDisplay(target.Handle, OtherDisplay(target.Handle).DeviceName);
        Screen display = Screen.FromHandle(target.Handle);
        Rectangle usual = FrameOf(target.Handle);
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000));
        Assert.Equal(display.DeviceName, DisplayOf(target.Handle));
        placer.Align(target.Handle, WindowAnchor.BottomRight);
        Assert.False(User32.IsZoomed(target.Handle), "Still maximized.");
        Assert.Equal(display.DeviceName, DisplayOf(target.Handle));
        Near(new Rectangle(PlacementGeometry.Align(usual.Size, display.WorkingArea, WindowAnchor.BottomRight), usual.Size), FrameOf(target.Handle));
        Assert.True(placer.RestorePrior(target.Handle));
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000), "Back to previous position didn't maximize it again.");
        Assert.Equal(display.DeviceName, DisplayOf(target.Handle));
    }

    /// <summary>
    /// Between displays at different scales the app resizes itself on the way. The window still lands at the same
    /// relative place, and comes back at its old size. Only in the test VM, the only place a scale is ever changed.
    /// </summary>
    [MultiDisplayFact]
    public void Moving_between_displays_at_different_scales_lands_where_expected()
    {
        if (!DisplayScale.Allowed) return; // Never on someone's own screen.
        using var target = new TargetProcess(Spot);
        var placer = new WindowPlacer(new WindowController([]));
        Screen home = Screen.FromHandle(target.Handle), other = OtherDisplay(target.Handle);
        int homeScale = DisplayScale.CurrentOf(home.DeviceName), otherScale = DisplayScale.CurrentOf(other.DeviceName);
        try
        {
            DisplayScale.Set(homeScale == 150 ? 100 : 150, other.DeviceName);
            Rectangle start = FrameOf(target.Handle);
            placer.MoveToDisplay(target.Handle, other.DeviceName);
            Assert.Equal(other.DeviceName, DisplayOf(target.Handle));
            Rectangle there = FrameOf(target.Handle);
            Assert.NotEqual(start.Size, there.Size); // The app took the other display's scale.
            Near(PlacementGeometry.FrameOnDisplay(start, home.WorkingArea, other.WorkingArea, there.Size), there);
            placer.MoveToDisplay(target.Handle, home.DeviceName);
            Assert.Equal(home.DeviceName, DisplayOf(target.Handle));
            Near(start, FrameOf(target.Handle), 2, "Back: ");
        }
        finally { DisplayScale.Set(otherScale, other.DeviceName); }
    }
}
