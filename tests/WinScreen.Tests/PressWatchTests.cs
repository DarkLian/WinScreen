using WinScreen.Dark;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>The watchdog for looks (11.1, INV-18): a window that stops taking presses for a second loses its look.</summary>
public sealed class PressWatchTests
{
    private const nint A = 101, B = 202;

    [Fact]
    public void A_look_goes_only_after_a_whole_second_of_failing()
    {
        var watch = new PressWatch();
        Assert.Empty(watch.Check([(A, false)], 0));
        Assert.Empty(watch.Check([(A, false)], 500));
        Assert.Equal([A], watch.Check([(A, false)], PressWatch.FailingFor));
    }

    [Fact]
    public void One_press_that_arrives_starts_the_count_again()
    {
        var watch = new PressWatch();
        watch.Check([(A, false)], 0);
        watch.Check([(A, true)], 600);
        Assert.Empty(watch.Check([(A, false)], 1100));
        Assert.Empty(watch.Check([(A, false)], 1900));
        Assert.Equal([A], watch.Check([(A, false)], 2100));
    }

    [Fact]
    public void Not_knowing_counts_neither_way()
    {
        var watch = new PressWatch();
        watch.Check([(A, false)], 0);
        watch.Check([(A, null)], 500);
        Assert.Equal([A], watch.Check([(A, false)], 1000));
        Assert.Empty(watch.Check([(B, null)], 0));
        Assert.Empty(watch.Check([(B, null)], 5000));
    }

    [Fact]
    public void A_window_whose_look_went_another_way_is_forgotten()
    {
        var watch = new PressWatch();
        watch.Check([(A, false)], 0);
        watch.Check([], 500); // Turned off by the person in between.
        Assert.Empty(watch.Check([(A, false)], 1200));
    }

    [Trait("Category", "RealWindows")]
    [Fact]
    public void A_window_under_its_look_takes_presses_until_the_look_catches_them()
    {
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(PressWatch.TakesPresses(target.Handle, 0));

            using var dark = new DarkMode();
            Assert.True(dark.Toggle(target.Handle));
            Desktop.WaitFor(() => dark.OverlayOf(target.Handle) != 0, 2000, Application.DoEvents);
            nint overlay = dark.OverlayOf(target.Handle);
            Assert.NotEqual(0, overlay);
            Assert.True(Desktop.WaitFor(() => PressWatch.TakesPresses(target.Handle, overlay) == true, 2000, Application.DoEvents),
                "Under filter Dark the window should still take presses.");

            // The look starts catching presses itself, the kind of break F3 was.
            long style = (long)User32.GetWindowLongPtr(overlay, User32.GWL_EXSTYLE);
            User32.SetWindowLongPtr(overlay, User32.GWL_EXSTYLE, (nint)(style & ~User32.WS_EX_TRANSPARENT));
            Assert.False(PressWatch.TakesPresses(target.Handle, overlay));
            dark.TurnAllOff();
        });
    }

    [Trait("Category", "RealWindows")]
    [Fact]
    public void A_window_that_lets_presses_through_to_the_one_below_does_not_take_them()
    {
        using var below = new TargetProcess(new Rectangle(280, 200, 500, 360), "1E64C8", title: "Below");
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(PressWatch.TakesPresses(target.Handle, 0));
            long style = (long)User32.GetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE);
            User32.SetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE, (nint)(style | User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT));
            User32.SetLayeredWindowAttributes(target.Handle, 0, 255, User32.LWA_ALPHA);
            Assert.False(PressWatch.TakesPresses(target.Handle, 0));

            // Something of the person's in front of it is their arrangement, not a fault.
            User32.SetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE, (nint)style);
            Desktop.Activate(below.Handle);
            User32.SetWindowPos(below.Handle, 0, 200, 150, 700, 500, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Assert.Null(PressWatch.TakesPresses(target.Handle, 0));
        });
    }

    /// <summary>
    /// The person's own Click-through, and a crop that keeps a corner, send presses at the window's middle below by
    /// choice. Neither may count against a look on the window, or the watchdog would turn it off for nothing.
    /// </summary>
    [Trait("Category", "RealWindows")]
    [Fact]
    public void Click_through_and_a_crop_chosen_by_the_person_do_not_count_against_a_look()
    {
        using var below = new TargetProcess(new Rectangle(280, 200, 500, 360), "1E64C8", title: "Below");
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8");
        nint plain = target.CreatePlainWindow(new Rectangle(300, 220, 460, 320), framed: true);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Desktop.Activate(target.Handle);
                Assert.True(windows.ToggleClickThrough(target.Handle));
                Assert.Null(PressWatch.TakesPresses(target.Handle, 0, windows));
                windows.RestoreAll();

                // A crop that keeps the window's top-left corner: its middle is the window below's.
                User32.ShowWindow(target.Handle, 6);
                Desktop.Activate(plain);
                windows.Crop(plain, new Rectangle(10, 10, 160, 110));
                Assert.True(Desktop.WaitFor(() => PressWatch.TakesPresses(plain, 0, windows) == true, 2000, Application.DoEvents),
                    "A cropped window should be asked where it still is.");
            }
            finally { windows.RestoreAll(); }
        });
    }
}
