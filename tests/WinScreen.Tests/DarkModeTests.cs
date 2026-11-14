using System.Collections.Concurrent;
using WinScreen.Dark;
using WinScreen.Anchors;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Reads the pixels really on screen over a window in dark mode.</summary>
[Trait("Category", "RealWindows")]
public class DarkModeTests
{
    private static readonly Rectangle Spot = new(300, 240, 400, 300);
    private static readonly Color White = Color.White;

    /// <summary>
    /// The Magnification API stops updating on any thread after the first that used it, so these tests
    /// all share one thread, as the app does.
    /// </summary>
    private static readonly BlockingCollection<Action> UiWork = StartUiThread();

    private static BlockingCollection<Action> StartUiThread()
    {
        var work = new BlockingCollection<Action>();
        // Its windows outlive each test, so it keeps answering their messages between tests. A thread that only waited
        // left them unanswered, and anything that writes to every window of the process waited on them for good:
        // changing between light and dark in another test (WinForms' colour mode) stopped there.
        var thread = new Thread(() =>
        {
            while (!work.IsCompleted)
            {
                if (work.TryTake(out Action? action, 10)) action();
                Application.DoEvents();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return work;
    }

    /// <summary>Runs on the one thread that may use Dark mode in this process. Other tests that turn Dark mode on use it too.</summary>
    internal static void OnUiThread(Action<Action<int>> test)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        UiWork.Add(() =>
        {
            try
            {
                test(milliseconds =>
                {
                    DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
                    while (DateTime.UtcNow < end)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                });
            }
            catch (Exception e)
            {
                failure = e;
            }

            finished.Set();
        });
        finished.Wait();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Rectangle Sample(TargetProcess target) => new(target.Center.X - 20, target.Center.Y - 20, 40, 40);

    private static bool Shows(TargetProcess target, Color color, Action<int> pump)
    {
        return Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Sample(target)), color), 3000,
            () => pump(20));
    }

    [Fact]
    public void Turns_a_white_window_dark_and_back()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(Shows(target, White, pump));

            using var dark = new DarkMode();
            Assert.True(dark.Toggle(target.Handle));
            Assert.True(Shows(target, Desktop.DarkPage, pump), $"Dark mode showed {Desktop.ColorOnScreen(Sample(target))}.");

            Assert.False(dark.Toggle(target.Handle));
            Assert.True(Shows(target, White, pump), $"Turning it off left {Desktop.ColorOnScreen(Sample(target))}.");
        });
    }

    [Fact]
    public void Keeps_colours_recognisable()
    {
        using var target = new TargetProcess(Spot, "C81E1E");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            pump(400);
            Color seen = Desktop.ColorOnScreen(Sample(target));
            Assert.True(seen.R > seen.G + 40 && seen.R > seen.B + 40, $"Red came out as {seen}.");
        });
    }

    [Fact]
    public void Follows_what_the_window_draws()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump));

            target.SetColors("000000");
            Assert.True(Shows(target, White, pump),
                $"Dark mode didn't follow: {Desktop.ColorOnScreen(Sample(target))}.");
        });
    }

    [Fact]
    public void Keeps_working_when_turned_on_again_and_on_other_windows()
    {
        using var first = new TargetProcess(Spot, "FFFFFF");
        using var second = new TargetProcess(new Rectangle(760, 240, 300, 300), "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            using var dark = new DarkMode();
            for (int round = 0; round < 3; round++)
            {
                Desktop.Activate(first.Handle);
                dark.Toggle(first.Handle);
                Assert.True(Shows(first, Desktop.DarkPage, pump), $"Round {round}: {Desktop.ColorOnScreen(Sample(first))}.");

                first.SetColors("000000");
                Assert.True(Shows(first, White, pump),
                    $"Round {round} didn't follow: {Desktop.ColorOnScreen(Sample(first))}.");
                first.SetColors("FFFFFF");
                Assert.True(Shows(first, Desktop.DarkPage, pump),
                    $"Round {round} didn't follow back: {Desktop.ColorOnScreen(Sample(first))}.");

                Desktop.Activate(second.Handle);
                dark.Toggle(second.Handle);
                Assert.True(Shows(second, Desktop.DarkPage, pump),
                    $"Round {round}, second window: {Desktop.ColorOnScreen(Sample(second))}.");

                dark.TurnAllOff();
                Assert.True(Shows(second, White, pump));
            }
        });
    }

    [Fact]
    public void Works_on_a_pinned_window_and_after_unpinning()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            windows.TogglePin(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump),
                $"A pinned window showed {Desktop.ColorOnScreen(Sample(target))}.");

            target.SetColors("000000");
            Assert.True(Shows(target, White, pump), "Dark mode didn't follow a pinned window.");
            target.SetColors("FFFFFF");
            Assert.True(Shows(target, Desktop.DarkPage, pump));

            windows.TogglePin(target.Handle);
            Desktop.Activate(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump),
                $"After unpinning it showed {Desktop.ColorOnScreen(Sample(target))}.");

            dark.Toggle(target.Handle);
            windows.TogglePin(target.Handle);
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump),
                $"Pinned first, dark on later showed {Desktop.ColorOnScreen(Sample(target))}.");
            windows.RestoreAll();
        });
    }

    [Fact]
    public void Clicks_still_reach_the_window()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump));
            Assert.Equal(target.Handle, Desktop.TopWindowAt(target.Center));
        });
    }

    [Fact]
    public void A_window_on_top_is_not_darkened()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        using var other = new TargetProcess(new Rectangle(420, 300, 160, 120), "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump));
            Desktop.Activate(other.Handle);
            Assert.True(Shows(target, White, pump),
                $"The window on top came out {Desktop.ColorOnScreen(Sample(target))}.");

            var uncovered = new Rectangle(target.Bounds.Left + 20, target.Bounds.Top + 20, 40, 40);
            Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(uncovered), Desktop.DarkPage),
                $"The rest of the window came out {Desktop.ColorOnScreen(uncovered)}.");
        });
    }

    [Fact]
    public void Goes_away_when_the_window_closes()
    {
        var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            dark.Toggle(target.Handle);
            pump(200);
            nint window = target.Handle;
            target.Dispose();
            Assert.True(Desktop.WaitFor(() => !dark.IsDark(window), 3000, () => pump(20)));
        });
    }

    [Fact]
    public void Anchors_are_excluded_before_they_appear_over_a_dark_window()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        OnUiThread(pump =>
        {
            Desktop.Activate(target.Handle);
            using var dark = new DarkMode();
            using var anchors = new AnchorSet();
            var created = new List<nint>();
            anchors.Created += window =>
            {
                Assert.False(Native.User32.IsWindowVisible(window));
                created.Add(window);
                dark.Ignore(window);
            };
            dark.Toggle(target.Handle);
            Assert.True(Shows(target, Desktop.DarkPage, pump));
            anchors.Toggle(target.Handle);
            var anchor = Assert.Single(Application.OpenForms.OfType<AnchorButton>());
            Assert.Equal(anchor.Handle, Assert.Single(created));
            anchor.Location = new Point(Spot.Left + 30, Spot.Top + 30);
            var edge = new Rectangle(anchor.Left + 2, anchor.Top + 2, 3, 3);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(edge),
                Color.FromArgb(32, 32, 32)), 3000, () => pump(20)));
        });
    }

    [Theory]
    [InlineData("101820", false)]
    [InlineData("FFFFFF", true)]
    public void Detection_leaves_dark_windows_alone_and_still_darkens_light_ones(string color, bool expected)
    {
        using var target = new TargetProcess(Spot, color);
        OnUiThread(pump =>
        {
            Native.User32.SetWindowPos(target.Handle, Native.User32.HWND_TOPMOST, 0, 0, 0, 0,
                Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
            Assert.True(Shows(target, ColorTranslator.FromHtml("#" + color), pump),
                $"Expected #{color}; saw {Desktop.ColorOnScreen(Sample(target))}. Target {target.Handle:X}, top {Desktop.TopWindowAt(target.Center):X}, " +
                $"top title {WindowInfo.Title(Desktop.TopWindowAt(target.Center))}, target bounds {Desktop.BoundsOf(target.Handle)}, style {Desktop.ExStyle(target.Handle):X}.");
            using var dark = new DarkMode { DetectAlreadyDark = true };
            var notices = new List<string>();
            dark.Notice += notices.Add;
            Assert.Equal(expected, dark.Toggle(target.Handle));
            Assert.Equal(expected, dark.IsDark(target.Handle));
            Assert.Equal(expected ? 0 : 1, notices.Count);
            if (expected) Assert.True(Shows(target, Desktop.DarkPage, pump));
        });
    }

    /// <summary>
    /// Dark mode's filter works on a window in WinScreen's Borderless, follows it when it's moved, and the window is
    /// still borderless once Dark is off.
    /// </summary>
    [Fact]
    public void Turns_a_borderless_window_dark_and_follows_it()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.UseFrame();
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Assert.True(windows.ToggleBorderless(target.Handle));
                Desktop.Activate(target.Handle);
                Rectangle Middle()
                {
                    Rectangle frame = Desktop.BoundsOf(target.Handle);
                    return new Rectangle(frame.Left + frame.Width / 2 - 20, frame.Top + frame.Height / 2 - 20, 40, 40);
                }
                bool ShowsColor(Color color) => Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle()), color), 3000, () => pump(20));
                Assert.True(ShowsColor(White), $"Borderless showed {Desktop.ColorOnScreen(Middle())}.");
                using var dark = new DarkMode();
                Assert.True(dark.Toggle(target.Handle));
                Assert.True(ShowsColor(Desktop.DarkPage), $"Dark on a borderless window showed {Desktop.ColorOnScreen(Middle())}.");
                Rectangle frame = Desktop.BoundsOf(target.Handle);
                Native.User32.SetWindowPos(target.Handle, 0, frame.Left + 120, frame.Top + 60, 0, 0,
                    Native.User32.SWP_NOSIZE | Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
                Assert.True(ShowsColor(Desktop.DarkPage), $"Dark didn't follow the move: {Desktop.ColorOnScreen(Middle())}.");
                Assert.False(dark.Toggle(target.Handle));
                Assert.True(ShowsColor(White));
                Assert.True(windows.IsBorderless(target.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }
}
