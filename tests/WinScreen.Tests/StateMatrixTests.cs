using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The cells of the feature compatibility tables that nothing else covers: each change on a window that is minimized,
/// in WinScreen's own Borderless, hidden behind a view, click-through or maximized.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class StateMatrixTests
{
    private static readonly Rectangle Spot = new(260, 200, 480, 340);
    private const int Minimize = 6;

    private static Rectangle FrameOf(nint window)
    {
        Assert.True(WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _));
        return frame;
    }

    private static void Near(Rectangle expected, Rectangle actual, int tolerance = 1, string what = "")
    {
        Assert.True(Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance &&
                    Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"{what}expected {expected}, got {actual}.");
    }

    private static void Minimized(nint window)
    {
        User32.ShowWindow(window, Minimize);
        Assert.True(Desktop.WaitFor(() => User32.IsIconic(window), 2000), "It didn't minimize.");
    }

    private static void Maximized(nint window)
    {
        User32.ShowWindow(window, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(window), 2000), "It didn't maximize.");
    }

    private static void Restored(nint window)
    {
        User32.ShowWindow(window, User32.SW_RESTORE);
        Assert.True(Desktop.WaitFor(() => !User32.IsIconic(window) && !User32.IsZoomed(window), 2000), "It didn't come back.");
    }

    private static bool Topmost(nint window) => (Desktop.ExStyle(window) & User32.WS_EX_TOPMOST) != 0;
    private static bool Transparent(nint window) => (Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT) != 0;

    // ---- Minimized ----

    /// <summary>A change made to a minimized window leaves it minimized, and is there when it comes back.</summary>
    [Theory]
    [InlineData("pin")]
    [InlineData("opacity")]
    [InlineData("clickthrough")]
    [InlineData("bottom")]
    [InlineData("darktitle")]
    public void A_change_to_a_minimized_window_keeps_it_minimized_and_holds_when_it_comes_back(string change)
    {
        if (change == "pin") Desktop.GainForegroundRights();
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        bool darkBefore = windows.IsDarkTitleBar(window);
        Minimized(window);
        try
        {
            switch (change)
            {
                case "pin": Assert.True(windows.TogglePin(window)); break;
                case "opacity": windows.SetOpacity(window, 128); break;
                case "clickthrough": Assert.True(windows.ToggleClickThrough(window)); break;
                case "bottom": windows.SendToBottom(window); break;
                case "darktitle": windows.ToggleDarkTitleBar(window); break;
            }
            Assert.True(User32.IsIconic(window), "It came out of minimized.");
            Restored(window);
            switch (change)
            {
                case "pin": Assert.True(Topmost(window), "Not on top after coming back."); break;
                case "opacity": Assert.Equal((byte?)128, Desktop.Alpha(window)); break;
                case "clickthrough":
                    Assert.True(Transparent(window));
                    Assert.Equal((byte?)windows.DefaultOpacity, Desktop.Alpha(window));
                    break;
                case "bottom": Assert.False(Topmost(window)); break;
                case "darktitle": Assert.NotEqual(darkBefore, windows.IsDarkTitleBar(window)); break;
            }
            Assert.Equal(0, windows.RestoreAll());
            Assert.False(Topmost(window));
            Assert.False(Transparent(window));
            Assert.Equal(darkBefore, windows.IsDarkTitleBar(window));
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>
    /// Crop on a minimized window (made through the window menu of its taskbar button, say) leaves it minimized, and
    /// only the part kept shows once it comes back. The Crop command itself brings a minimized window back first, so
    /// the part can be chosen over it.
    /// </summary>
    [Fact]
    public void A_minimized_window_cropped_comes_back_with_only_that_part()
    {
        using var target = new TargetProcess(Spot);
        // Not the form: a Windows Forms form with a frame puts its own shape back.
        nint window = target.CreatePlainWindow(Spot, framed: true);
        var windows = new WindowController([]);
        Minimized(window);
        var part = new Rectangle(30, 30, 200, 150);
        try
        {
            windows.Crop(window, part);
            Assert.True(User32.IsIconic(window), "It came out of minimized.");
            // This plain window comes back maximized, as the first window of its process; either way is fine here.
            User32.ShowWindow(window, User32.SW_RESTORE);
            Assert.True(Desktop.WaitFor(() => !User32.IsIconic(window), 2000), "It didn't come back.");
            Assert.Equal(part, WindowInfo.ShapeOf(window));
            Assert.True(windows.IsCropped(window));
        }
        finally { windows.RestoreAll(); }
        Assert.Null(WindowInfo.ShapeOf(window));
    }

    // ---- WinScreen's Borderless ----

    /// <summary>
    /// Borderless takes the title bar away, so there's none to darken. Hiding, which Shrink, a view's Keep updating,
    /// layered Glass and picture-safe Dark all need, says to turn Borderless off first. Opacity and colour Glass work.
    /// </summary>
    [Fact]
    public void A_borderless_window_takes_opacity_and_colour_glass_but_not_a_dark_title_bar_or_hiding()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        long style = User32.GetWindowLongPtr(window, User32.GWL_STYLE);
        try
        {
            Assert.True(windows.ToggleBorderless(window));
            Assert.Throws<WindowOperationException>(() => windows.ToggleDarkTitleBar(window));
            Assert.Throws<WindowOperationException>(() => windows.HideOwned(window, new object()));
            Assert.Throws<WindowOperationException>(() => windows.HideOwned(window, new object(), solid: true));
            Assert.False(windows.IsHidden(window));
            Assert.Equal((byte)128, windows.SetOpacity(window, 128));
            Assert.Equal((byte?)128, Desktop.Alpha(window));
            Assert.True(windows.ToggleGlass(window));
            Assert.False(windows.ToggleGlass(window));
            Assert.True(windows.IsBorderless(window));
        }
        finally { windows.RestoreAll(); }
        Assert.Equal(style, User32.GetWindowLongPtr(window, User32.GWL_STYLE));
        Assert.Equal((byte)255, Desktop.Alpha(window) ?? 255);
    }

    /// <summary>A borderless window can be aligned, placed exactly and put back, and stays borderless throughout.</summary>
    [Fact]
    public void A_borderless_window_can_be_aligned_placed_and_put_back()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        var placer = new WindowPlacer(windows);
        try
        {
            windows.ToggleBorderless(window);
            Rectangle start = FrameOf(window);
            Rectangle work = Screen.FromHandle(window).WorkingArea;
            placer.Align(window, WindowAnchor.BottomRight);
            Near(new Rectangle(PlacementGeometry.Align(start.Size, work, WindowAnchor.BottomRight), start.Size), FrameOf(window), 1, "Aligned: ");
            var exact = new Rectangle(work.Left + 120, work.Top + 90, 420, 260);
            placer.SetFrame(window, exact);
            Near(exact, FrameOf(window), 1, "Placed: ");
            Assert.True(placer.RestorePrior(window));
            Near(new Rectangle(PlacementGeometry.Align(start.Size, work, WindowAnchor.BottomRight), start.Size), FrameOf(window), 1, "Put back: ");
            Assert.True(windows.IsBorderless(window));
            Assert.Equal(0, User32.GetWindowLongPtr(window, User32.GWL_STYLE) & WindowController.BorderlessMask);
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>A borderless window moved to another display keeps its size there and stays borderless.</summary>
    [MultiDisplayFact]
    public void A_borderless_window_moves_to_the_next_display()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        var placer = new WindowPlacer(windows);
        try
        {
            windows.ToggleBorderless(window);
            string from = Screen.FromHandle(window).DeviceName;
            Size size = FrameOf(window).Size;
            placer.MoveToNextDisplay(window);
            Assert.NotEqual(from, Screen.FromHandle(window).DeviceName);
            Assert.True(windows.IsBorderless(window));
            Assert.Equal(size, FrameOf(window).Size);
        }
        finally { windows.RestoreAll(); }
    }

    // ---- Hidden by WinScreen ----

    /// <summary>
    /// A window hidden behind a view or tile is parked out of sight. A dark title bar still works (the view shows it),
    /// but Crop and colour Glass work from what is on screen, so they ask for the window to be shown first.
    /// </summary>
    [Fact]
    public void A_hidden_window_takes_a_dark_title_bar_but_crop_and_glass_ask_to_show_it_first()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        var owner = new object();
        bool darkBefore = windows.IsDarkTitleBar(window);
        try
        {
            windows.HideOwned(window, owner);
            windows.ToggleDarkTitleBar(window);
            Assert.NotEqual(darkBefore, windows.IsDarkTitleBar(window));
            Assert.True(windows.IsHidden(window));
            Assert.Throws<WindowOperationException>(() => windows.Crop(window, new Rectangle(0, 0, 100, 100)));
            Assert.False(windows.IsCropped(window));
            Assert.Null(WindowInfo.ShapeOf(window));
            Assert.Throws<WindowOperationException>(() => windows.ToggleGlass(window));
            Assert.False(windows.IsGlass(window));
            Assert.Equal((byte?)0, Desktop.Alpha(window));
            windows.ShowOwned(window, owner);
            Assert.NotEqual(darkBefore, windows.IsDarkTitleBar(window));
            Assert.Equal((byte)255, Desktop.Alpha(window) ?? 255);
        }
        finally { windows.RestoreAll(); }
        Assert.Equal(darkBefore, windows.IsDarkTitleBar(window));
    }

    // ---- Click-through ----

    /// <summary>Borderless, Roll up and Crop work on a click-through window, and it's still click-through after each.</summary>
    [Theory]
    [InlineData("borderless")]
    [InlineData("rollup")]
    [InlineData("crop")]
    public void A_click_through_window_takes_borderless_roll_up_and_crop_and_stays_click_through(string change)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        // A Windows Forms form with a frame puts its own shape back, so the crop is tried on a window that keeps it.
        nint window = change == "crop" ? target.CreatePlainWindow(Spot, framed: true) : target.Handle;
        var windows = new WindowController([]);
        try
        {
            Assert.True(windows.ToggleClickThrough(window));
            switch (change)
            {
                case "borderless": Assert.True(windows.ToggleBorderless(window)); break;
                case "rollup": Assert.True(windows.ToggleRollUp(window)); break;
                case "crop": windows.Crop(window, new Rectangle(0, 0, 200, 150)); break;
            }
            Assert.True(Transparent(window), "No longer click-through.");
            Assert.True(windows.IsClickThrough(window));
            switch (change)
            {
                case "borderless": Assert.False(windows.ToggleBorderless(window)); break;
                case "rollup": Assert.False(windows.ToggleRollUp(window)); break;
                case "crop": windows.RemoveCrop(window); break;
            }
            Assert.True(Transparent(window), "Undoing it took click-through away.");
        }
        finally { windows.RestoreAll(); }
        Assert.False(Transparent(window));
    }

    /// <summary>
    /// A click-through window shrunk to a tile (or hidden for a view) stays click-through while hidden, and is
    /// click-through and as see-through as before once it's back.
    /// </summary>
    [Fact]
    public void A_click_through_window_hidden_and_shown_again_is_still_click_through()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        var owner = new object();
        try
        {
            windows.ToggleClickThrough(window);
            byte level = windows.OpacityOf(window);
            windows.HideOwned(window, owner);
            Assert.Equal((byte?)0, Desktop.Alpha(window));
            windows.ShowOwned(window, owner);
            Assert.True(Transparent(window));
            Assert.True(windows.IsClickThrough(window));
            Assert.Equal((byte?)level, Desktop.Alpha(window));
        }
        finally { windows.RestoreAll(); }
    }

    // ---- Maximized ----

    /// <summary>
    /// A maximized window can be cropped, and stays maximized when the crop comes off. An app that puts its own shape
    /// straight back (a Windows Forms form with a frame) is told apart: Crop says it can't, instead of showing as on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_maximized_window_can_be_cropped_unless_the_app_puts_its_shape_back(bool appResetsShape)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = appResetsShape ? target.Handle : target.CreatePlainWindow(Spot, framed: true);
        var windows = new WindowController([]);
        Maximized(window);
        try
        {
            var part = new Rectangle(40, 40, 300, 200);
            if (appResetsShape)
            {
                Assert.Throws<WindowOperationException>(() => windows.Crop(window, part));
                Assert.False(windows.IsCropped(window));
                Assert.Null(WindowInfo.ShapeOf(window));
            }
            else
            {
                windows.Crop(window, part);
                Assert.Equal(part, WindowInfo.ShapeOf(window));
                windows.RemoveCrop(window);
                Assert.Null(WindowInfo.ShapeOf(window));
            }
            Assert.True(User32.IsZoomed(window));
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>A maximized window hidden behind a tile comes back maximized, in the same place.</summary>
    [Fact]
    public void A_maximized_window_hidden_and_shown_again_is_still_maximized()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        var owner = new object();
        Maximized(window);
        Rectangle before = Desktop.BoundsOf(window);
        try
        {
            windows.HideOwned(window, owner);
            Assert.True(User32.IsZoomed(window));
            windows.ShowOwned(window, owner);
            Assert.True(User32.IsZoomed(window));
            Assert.Equal(before, Desktop.BoundsOf(window));
            Assert.Equal((byte)255, Desktop.Alpha(window) ?? 255);
        }
        finally { windows.RestoreAll(); }
    }

    // ---- Pinned and maximized ----

    /// <summary>
    /// With Pause pin when maximized, a pinned window leaves the top while maximized, and is back on top at the next
    /// check once it's restored and in use, as it is after being dragged out of maximized.
    /// </summary>
    [Fact]
    public void A_paused_pin_comes_back_at_the_next_check_once_the_window_is_restored()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.Handle;
        var windows = new WindowController([]);
        try
        {
            Desktop.Activate(window);
            Assert.True(windows.TogglePin(window));
            Maximized(window);
            windows.Refresh(new Point(-30000, -30000), false, pausePinWhenMaximized: true);
            Assert.False(Topmost(window), "Still on top while maximized.");
            Restored(window);
            Desktop.Activate(window);
            windows.Refresh(new Point(-30000, -30000), false, pausePinWhenMaximized: true);
            Assert.True(Topmost(window), "Not back on top after coming out of maximized.");
            Assert.True(windows.IsPinned(window));
        }
        finally { windows.RestoreAll(); }
    }
}
