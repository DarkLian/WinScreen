using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Owner, 22 Nov: with Keep hidden off, a view froze once another window covered its original, as browsers stop
/// drawing while nothing of them shows. While wholly covered, the original goes see-through and click-through above
/// the rest, where nobody sees it either, and goes back under whatever is still over it once a part of it would show.
/// Keep hidden stays off throughout, and the window keeps its place, taskbar button and Alt+Tab entry.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class CoverLiftTests
{
    private static readonly Rectangle Spot = new(200, 160, 520, 380);

    private const string Page = "<!doctype html><title>CoveredLift</title><body style='margin:0'><script>" +
        "const c=['#c81e1e','#1ec81e','#1e1ec8','#c8c81e'];let i=0;setInterval(()=>{document.body.style.background=c[i++%4]},300);</script>";

    private const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020; // What a minimize button sends.

    private static bool Until(Func<bool> condition, int milliseconds = 2500)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            TestDesktop.Pump(25);
        }
        return condition();
    }

    private static PipWindow OpenView(nint window, WindowController windows)
    {
        var view = new PipWindow(window, windows);
        view.ShowOnTop();
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        view.Bounds = new Rectangle(area.Right - 360, area.Top + 120, 320, 240);
        TestDesktop.Pump(300);
        return view;
    }

    // Windows 11 ignores putting a window that was never in use on top; a window someone watches has been used.
    private static void UsedOnce(nint window)
    {
        Desktop.Activate(window);
        TestDesktop.Pump(200);
    }

    private static bool IsTopmost(nint window) => (Desktop.ExStyle(window) & User32.WS_EX_TOPMOST) != 0;

    private static string Describe(nint window) =>
        $"alpha {Desktop.Alpha(window)?.ToString() ?? "none"}, topmost {IsTopmost(window)}, click-through {(Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT) != 0}";

    /// <summary>Solid again, not on top of everything and taking clicks: as before the lift.</summary>
    private static void AssertSettled(nint window, string when)
    {
        Assert.True(Desktop.Alpha(window) is null or 255 && !IsTopmost(window) && (Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT) == 0,
            $"{when}, the original wasn't put back as it was: {Describe(window)}.");
    }

    private static int Changes(PipWindow view)
    {
        Point spot = new(view.Bounds.Left + view.Bounds.Width / 2, view.Bounds.Top + view.Bounds.Height * 2 / 3);
        int changes = 0;
        Color last = Desktop.ColorOnScreen(new Rectangle(spot, new Size(1, 1)));
        for (int look = 0; look < 10; look++)
        {
            TestDesktop.Pump(350);
            Color now = Desktop.ColorOnScreen(new Rectangle(spot, new Size(1, 1)));
            if (now != last) changes++;
            last = now;
        }
        return changes;
    }

    [Fact]
    public void A_covered_browser_keeps_drawing_with_keep_hidden_off_and_is_itself_again_once_uncovered()
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        var (browser, window, root) = BrowserInputTests.Open(Page, "CoveredLift", app: true, chrome);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                Desktop.GainForegroundRights();
                var windows = new WindowController([]);
                if (User32.IsZoomed(window)) User32.ShowWindow(window, User32.SW_RESTORE);
                User32.SetWindowPos(window, 0, Spot.Left, Spot.Top, Spot.Width, Spot.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                using var view = OpenView(window, windows);
                view.LiftsWhenCovered = false;
                try
                {
                    TestDesktop.Pump(1500);
                    Assert.True(Changes(view) >= 5, "The view didn't follow the page before anything covered it.");
                    Rectangle browserBounds = Desktop.BoundsOf(window);
                    browserBounds.Inflate(40, 40);
                    using var cover = new TargetProcess(browserBounds, "E8E8E8", "Covering window");
                    Desktop.Activate(cover.Handle);
                    TestDesktop.Pump(2000);
                    int frozen = Changes(view);
                    Assert.True(frozen <= 2, $"The covered browser kept drawing without the lift ({frozen} changes in 10 looks), so this test shows nothing.");

                    view.LiftsWhenCovered = true;
                    Assert.True(Until(() => view.LiftedWhileCovered), "The covered original wasn't lifted.");
                    TestDesktop.Pump(800);
                    int lifted = Changes(view);
                    Assert.True(lifted >= 5, $"The view stayed frozen with its original lifted: {lifted} changes in 10 looks.");
                    Assert.False(view.KeepsUpdating, "Keep hidden came on.");
                    Assert.Equal(cover.Handle, User32.GetForegroundWindow());
                    Point middle = new(Spot.Left + Spot.Width / 2, Spot.Top + Spot.Height / 2);
                    Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(new Rectangle(middle, new Size(1, 1))), Color.FromArgb(0xE8, 0xE8, 0xE8), 4),
                        "Something of the lifted original showed over the window covering it.");
                    Assert.Equal(cover.Handle, User32.GetAncestor(Desktop.TopWindowAt(middle), User32.GA_ROOT));

                    Desktop.PostMessage(cover.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
                    Assert.True(Until(() => !view.LiftedWhileCovered), "Uncovered, the original stayed lifted: " + view.DescribeCover());
                    AssertSettled(window, "Uncovered");
                    TestDesktop.Pump(500);
                    Assert.True(Changes(view) >= 5, "The uncovered page didn't go on drawing.");
                }
                finally { windows.RestoreAll(); }
            });
        }
        finally { BrowserInputTests.Close(browser, root); }
    }

    [Fact]
    public void A_lifted_original_goes_back_under_the_window_over_it_once_part_of_it_shows()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Covered source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Rectangle over = Spot;
                over.Inflate(30, 30);
                using var cover = new TargetProcess(over, "1E64C8", "Covering window");
                UsedOnce(source.Handle);
                using var view = OpenView(source.Handle, windows);
                Desktop.Activate(cover.Handle);
                Assert.True(Until(() => view.LiftedWhileCovered), "The covered original wasn't lifted.");
                Assert.True(Desktop.Alpha(source.Handle) == 0 && IsTopmost(source.Handle), "Lifted: " + Describe(source.Handle));
                Assert.True(windows.NeedsRecoveryWatcher, "A lifted window has no recovery watcher behind it.");
                Assert.False(windows.IsHidden(source.Handle), "The lift counts as hiding.");
                Assert.False(view.KeepsUpdating);

                User32.SetWindowPos(cover.Handle, 0, over.Left + 300, over.Top, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                Assert.True(Until(() => !view.LiftedWhileCovered), "Partly uncovered, the original stayed lifted: " + view.DescribeCover());
                Assert.Equal(CoverEnd.Uncovered, view.LastCoverEnd);
                AssertSettled(source.Handle, "Partly uncovered");
                Assert.True(Desktop.IsAbove(cover.Handle, source.Handle), "The original came back over the window still covering part of it.");
                Assert.Equal(cover.Handle, User32.GetForegroundWindow());

                // Covered again, it is lifted again.
                User32.SetWindowPos(cover.Handle, 0, over.Left, over.Top, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                Assert.True(Until(() => view.LiftedWhileCovered), "Covered again, the original wasn't lifted again.");
            }
            finally { windows.RestoreAll(); }
            AssertSettled(source.Handle, "After everything was put back");
        });
    }

    /// <summary>
    /// The window in front minimized, Windows hands the focus to the highest window, which is the lifted original.
    /// While another window still covers it, the focus goes on to that window and the original stays lifted; once
    /// nothing covers it, it is itself again.
    /// </summary>
    [Fact]
    public void Minimizing_the_window_in_front_passes_the_focus_on_while_another_still_covers_the_original()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Covered source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Rectangle middleBounds = Spot, frontBounds = Spot;
                middleBounds.Inflate(20, 20);
                frontBounds.Inflate(40, 40);
                using var middle = new TargetProcess(middleBounds, "1EC81E", "Middle window");
                using var front = new TargetProcess(frontBounds, "1E64C8", "Front window");
                UsedOnce(source.Handle);
                using var view = OpenView(source.Handle, windows);
                Desktop.Activate(middle.Handle);
                Desktop.Activate(front.Handle);
                Assert.True(Until(() => view.LiftedWhileCovered), "The covered original wasn't lifted.");

                Desktop.PostMessage(front.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
                TestDesktop.Pump(1500);
                nint focused = User32.GetForegroundWindow();
                Assert.NotEqual(source.Handle, focused);
                Assert.True(view.LiftedWhileCovered, $"Still covered, the original was put back ({view.LastCoverEnd}); focus: {WindowInfo.Title(focused)}.");

                Desktop.PostMessage(middle.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
                Assert.True(Until(() => !view.LiftedWhileCovered), "With nothing over it, the original stayed lifted: " + view.DescribeCover());
                AssertSettled(source.Handle, "With nothing over it");
                Assert.False(view.KeepsUpdating);
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void Brought_forward_while_lifted_it_comes_to_the_front_as_itself()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Covered source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Rectangle over = Spot;
                over.Inflate(30, 30);
                using var cover = new TargetProcess(over, "1E64C8", "Covering window");
                UsedOnce(source.Handle);
                using var view = OpenView(source.Handle, windows);
                Desktop.Activate(cover.Handle);
                Assert.True(Until(() => view.LiftedWhileCovered), "The covered original wasn't lifted.");

                User32.SetForegroundWindow(source.Handle); // As its app or an Anchor would.
                Assert.True(Until(() => !view.LiftedWhileCovered), "Brought forward, the original stayed lifted.");
                Assert.Equal(CoverEnd.Arrived, view.LastCoverEnd);
                AssertSettled(source.Handle, "Brought forward");
                Assert.True(Desktop.IsAbove(source.Handle, cover.Handle), "Brought forward, the original stayed under the cover.");
                Assert.Equal(source.Handle, User32.GetForegroundWindow());
                Assert.False(view.KeepsUpdating);
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Theory]
    [InlineData("view closed")]
    [InlineData("keep hidden on")]
    [InlineData("view minimized")]
    [InlineData("popup")]
    public void The_lift_gives_way_and_the_original_goes_back_under_its_cover(string what)
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Covered source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Rectangle over = Spot;
                over.Inflate(30, 30);
                using var cover = new TargetProcess(over, "1E64C8", "Covering window");
                UsedOnce(source.Handle);
                var view = OpenView(source.Handle, windows);
                try
                {
                    Desktop.Activate(cover.Handle);
                    Assert.True(Until(() => view.LiftedWhileCovered), "The covered original wasn't lifted.");
                    TargetProcess? popup = null;
                    try
                    {
                        switch (what)
                        {
                            case "view closed": view.Close(); break;
                            case "view minimized": view.SuspendView(); break;
                            case "keep hidden on": view.SetOriginalHidden(true); break;
                            case "popup":
                                // A popup of its own would go up with it, over the cover.
                                popup = new TargetProcess(new Rectangle(Spot.Left + 40, Spot.Top + 40, 200, 120), "1EC81E", "Its popup", owner: source.Handle);
                                break;
                        }
                        Assert.True(Until(() => !windows.IsLiftedWhileCovered(source.Handle)), $"{what}: the original stayed lifted.");
                        if (what == "keep hidden on")
                        {
                            Assert.True(view.KeepsUpdating && windows.IsParked(source.Handle), "Keep hidden didn't hide and park it.");
                            view.SetOriginalHidden(false);
                            Assert.False(windows.IsHidden(source.Handle));
                        }
                        else
                        {
                            AssertSettled(source.Handle, what);
                            Assert.True(Desktop.IsAbove(cover.Handle, source.Handle), $"{what}: the original came back over its cover.");
                        }
                        if (what == "popup") Assert.Equal(CoverEnd.Popup, view.LastCoverEnd);
                    }
                    finally { popup?.Dispose(); }
                }
                finally { view.Dispose(); }
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void A_window_that_refuses_to_be_see_through_stays_covered_and_nothing_is_said()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Covered source");
        source.RefuseToBeSeeThrough();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Rectangle over = Spot;
                over.Inflate(30, 30);
                using var cover = new TargetProcess(over, "1E64C8", "Covering window");
                UsedOnce(source.Handle);
                using var view = OpenView(source.Handle, windows);
                var problems = new List<string>();
                view.Problem += problems.Add;
                Desktop.Activate(cover.Handle);
                TestDesktop.Pump(1500);
                Assert.False(view.LiftedWhileCovered);
                Assert.Empty(problems);
                AssertSettled(source.Handle, "Refused");
                Assert.True(Desktop.IsAbove(cover.Handle, source.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }
}
