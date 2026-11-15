using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A view of a window on another virtual desktop. Chrome stops drawing while its desktop isn't shown, and the view
/// froze with it (measured 15 Nov, both view kinds). These make a desktop of their own with Ctrl+Win+D, so they run
/// only with WINSCREEN_TEST_DESKTOPS=1, on Windows 11 (a VM here can't make one).
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class VirtualDesktopViewTests
{
    private static bool Skip()
    {
        if (Environment.GetEnvironmentVariable("WINSCREEN_TEST_DESKTOPS") == "1") return false;
        Skipped.Because("Makes a virtual desktop, so it runs only with WINSCREEN_TEST_DESKTOPS=1.");
        return true;
    }

    private const string Page = "<!doctype html><title>DeskView</title><body style='margin:0'><script>" +
        "const c=['#c81e1e','#1ec81e','#1e1ec8','#c8c81e'];let i=0;setInterval(()=>{document.body.style.background=c[i++%4]},300);</script>";

    private static Color At(Point p)
    {
        using var bitmap = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(p, Point.Empty, new Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }

    /// <summary>A picture of the screen for the person checking the run, in the temp folder's winscreen-desktops.</summary>
    private static void Shot(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "winscreen-desktops");
        Directory.CreateDirectory(folder);
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
        bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>How many of ten looks, a third of a second apart, found the view's picture changed.</summary>
    private static int Changes(PipWindow view)
    {
        Point spot = new(view.Bounds.Left + view.Bounds.Width / 2, view.Bounds.Top + view.Bounds.Height * 2 / 3);
        int changes = 0;
        Color last = At(spot);
        for (int look = 0; look < 10; look++)
        {
            TestDesktop.Pump(350);
            Color now = At(spot);
            if (now != last) changes++;
            last = now;
        }
        return changes;
    }

    private static readonly Rectangle Placed = new(160, 100, 900, 640);

    /// <summary>Where the window was, as measured, before the test left its desktop.</summary>
    private static Rectangle _before;

    private static void WithChromeView(Action<nint, PipWindow, TestDesktop> test) => WithChromeView(null, test);

    /// <param name="first">Done on the window's own desktop, before the new one is made.</param>
    /// <param name="atSwitch">Done with the window the moment the new desktop is in front.</param>
    private static void WithChromeView(Action<PipWindow>? first, Action<nint, PipWindow, TestDesktop> test, Action<nint>? atSwitch = null)
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        var (browser, window, root) = BrowserInputTests.Open(Page, "DeskView", app: true, chrome);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]);
                using var view = new PipWindow(window, windows);
                view.ShowOnTop();
                // A window of its own size, not maximized, is the one that could come back somewhere else.
                if (User32.IsZoomed(window)) User32.ShowWindow(window, User32.SW_RESTORE);
                User32.SetWindowPos(window, 0, Placed.Left, Placed.Top, Placed.Width, Placed.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                view.Bounds = new Rectangle(1100, 160, 320, 240);
                TestDesktop.Pump(2000);
                _before = Desktop.BoundsOf(window);
                Assert.True(VirtualDesktops.DesktopOf(window) is { } own && own == VirtualDesktops.CurrentDesktop(), "Chrome didn't open on the desktop in front.");
                try
                {
                    if (first is not null) { first(view); TestDesktop.Pump(1000); }
                    using var desktop = new TestDesktop(atSwitch is null ? null : () => atSwitch(window));
                    test(window, view, desktop);
                }
                finally { windows.RestoreAll(); }
            });
        }
        finally { BrowserInputTests.Close(browser, root); }
    }

    [Fact]
    public void A_view_keeps_following_its_window_from_another_desktop_and_the_window_goes_back_to_its_own()
    {
        if (Skip()) return;
        WithChromeView((window, view, desktop) =>
        {
            Rectangle before = _before;
            TestDesktop.Pump(1500);
            Assert.True(view.HiddenWhileAway, "The view didn't hide its original while the person is on another desktop.");
            Assert.False(view.KeepsUpdating, "Hidden for now reads as the person's own Keep hidden, which a saved setup would keep.");
            // Not the person's Keep hidden, but said for what it is, so a hidden window never looks shown.
            Assert.DoesNotContain(Localization.UiText.T("pip.state.originalHidden"), view.StateDescription.Split(", "));
            Assert.Contains(Localization.UiText.T("pip.state.awayHidden"), view.StateDescription.Split(", "));
            int changes = Changes(view);
            Shot("view-on-another-desktop");
            Assert.True(changes >= 5, $"The view froze on the other desktop: it changed {changes} times in 10 looks.");

            desktop.GoHome();
            Assert.True(Desktop.WaitFor(() => !view.HiddenWhileAway, 3000, Application.DoEvents), "The original wasn't shown again back on its desktop.");
            Assert.True(VirtualDesktops.DesktopOf(window) == desktop.Home, "The original came back on another desktop.");
            Assert.True(VirtualDesktops.OnCurrentDesktop(window));
            TestDesktop.Pump(500);
            Shot("original-back-home");
            Assert.True(Desktop.BoundsOf(window) == before && !User32.IsZoomed(window),
                $"The original came back at {Desktop.BoundsOf(window)} maximized={User32.IsZoomed(window)}, not {before}.");
        });
    }

    [Fact]
    public void A_window_left_on_another_desktop_is_not_judged_by_the_look_watchdog()
    {
        if (Skip()) return;
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(PressWatch.TakesPresses(target.Handle, 0));
            using var desktop = new TestDesktop();
            Assert.Null(PressWatch.TakesPresses(target.Handle, 0));
        });
    }

    [Fact]
    public void A_view_of_a_window_something_else_hid_leaves_it_alone_on_another_desktop()
    {
        if (Skip()) return;
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            try
            {
                using var view = new PipWindow(target.Handle, windows);
                var problems = new List<string>();
                view.Problem += problems.Add;
                view.ShowOnTop();
                view.Bounds = new Rectangle(1100, 160, 320, 240);
                TestDesktop.Pump(1500);
                windows.ConcealSolid(target.Handle); // As a look does.
                using var desktop = new TestDesktop();
                TestDesktop.Pump(2000);
                Assert.False(view.HiddenWhileAway);
                Assert.Empty(problems);
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void Closing_the_view_on_another_desktop_puts_the_original_back_on_its_own()
    {
        if (Skip()) return;
        WithChromeView((window, view, desktop) =>
        {
            TestDesktop.Pump(1500);
            Assert.True(view.HiddenWhileAway);
            view.Close();
            TestDesktop.Pump(500);
            Assert.True(User32.IsWindowVisible(window));
            Assert.True(VirtualDesktops.DesktopOf(window) is not null, "The original was left showing on every desktop.");
            Assert.True(VirtualDesktopsInternal.Supported, "This Windows's desktop interface isn't in the table.");
            Assert.Equal(desktop.Home, VirtualDesktops.DesktopOf(window));
        });
    }

    [Fact]
    public void Keep_hidden_turned_off_on_another_desktop_puts_the_original_back_on_its_own()
    {
        if (Skip()) return;
        WithChromeView((window, view, desktop) =>
        {
            desktop.GoHome();
            view.SetOriginalHidden(true); // The person's own choice, made at home.
            desktop.GoToMade();
            TestDesktop.Pump(800);
            bool keptBefore = view.KeepsUpdating;
            view.SetOriginalHidden(false);
            string seen = "";
            for (int look = 0; look < 6 && VirtualDesktops.DesktopOf(window) != desktop.Home; look++)
            {
                TestDesktop.Pump(500);
                seen += $" [{VirtualDesktops.DesktopOf(window)?.ToString()[..8] ?? "none"} keeps={view.KeepsUpdating} visible={User32.IsWindowVisible(window)}]";
            }
            Assert.True(VirtualDesktops.DesktopOf(window) == desktop.Home, $"Kept hidden before: {keptBefore}. Then:{seen}");
        });
    }

    private static ToolStripMenuItem Item(PipWindow view, string key) =>
        view.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == Localization.UiText.T(key));

    /// <summary>
    /// The owner's report (15 Nov): Interactive, then Ctrl+Win+D, then Go to it broke. On the new desktop Windows gave
    /// the hidden original the focus (it is on every desktop), the view took that for the person asking for it and
    /// showed it, ending Interactive, and a moment later hid it again as "away".
    /// </summary>
    [Fact]
    public void An_interactive_view_stays_interactive_on_a_new_desktop_and_Go_to_it_still_works()
    {
        if (Skip()) return;
        WithChromeView(view =>
        {
            view.SetInteractive(true);
            Assert.True(Item(view, "menu.view.interactive").Checked, "Chrome's view didn't become interactive.");
            // The person has just used the view's menu: the pointer rests on it, so the view is in use when they switch.
            Cursor.Position = new Point(view.Bounds.Left + view.Bounds.Width / 2, view.Bounds.Top + view.Bounds.Height / 2);
            TestDesktop.Pump(1200);
        },
        (window, view, desktop) =>
        {
            TestDesktop.Pump(2000);
            Shot("interactive-on-new-desktop");
            Assert.True(Item(view, "menu.view.interactive").Checked, "Making a desktop turned Interactive off.");
            Assert.True(Item(view, "menu.original.keepHidden").Checked, "The person's hide became something else.");
            Assert.False(view.HiddenWhileAway, "The person's hide was swapped for the temporary one.");
            Assert.NotEqual(window, User32.GetForegroundWindow());

            view.GoToSource();
            string State() => $"in front: {(VirtualDesktops.CurrentDesktop() == desktop.Home ? "home" : "made")}, window on: " +
                $"{(VirtualDesktops.DesktopOf(window) is not { } d ? "none" : d == desktop.Home ? "home" : "made")}, focused: " +
                $"{User32.GetForegroundWindow() == window} (in front: {(User32.GetForegroundWindow() == view.Handle ? "the view" : WindowInfo.ClassName(User32.GetForegroundWindow()))}), visible: {User32.IsWindowVisible(window)}, away: {view.HiddenWhileAway}, " +
                $"keep hidden: {Item(view, "menu.original.keepHidden").Checked}, interactive: {Item(view, "menu.view.interactive").Checked}, " +
                $"going to: {view.GoingTo}, taken back: {view.GoingToTakes}";
            string seen = State();
            // A timeline of the focus and the desktop in front, for a failure.
            var timeline = new System.Text.StringBuilder();
            string last = "";
            long start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 7000)
            {
                nint f = User32.GetForegroundWindow();
                string now = $"{(VirtualDesktops.CurrentDesktop() == desktop.Home ? "home" : "made")}/" +
                    $"{(f == window ? "window" : f == view.Handle ? "view" : WindowInfo.ClassName(f))}/going={view.GoingTo}";
                if (now != last) timeline.Append($" {Environment.TickCount64 - start}ms:{now}");
                last = now;
                TestDesktop.Pump(100);
            }
            bool went = VirtualDesktops.CurrentDesktop() == desktop.Home && User32.GetForegroundWindow() == window;
            seen += ". Timeline:" + timeline;
            Shot("after-go-to-it");
            Assert.True(went, $"Go to it didn't take the person to the window on its own desktop. Right after: {seen}. Later: {State()}");
            TestDesktop.Pump(1500);
            Assert.True(User32.IsWindowVisible(window) && VirtualDesktops.DesktopOf(window) == desktop.Home);
            Assert.False(view.HiddenWhileAway);
            Assert.False(Item(view, "menu.original.keepHidden").Checked);
        });
    }

    /// <summary>
    /// What Windows did on the owner's PC (15 Nov log: view.awayHidden seconds after an Interactive view was made, which
    /// can't happen while Interactive holds): on the new desktop it gave the hidden original the focus. That is the
    /// switch, not the person asking for the window.
    /// </summary>
    [Fact]
    public void The_focus_Windows_gives_a_hidden_original_on_a_new_desktop_changes_nothing()
    {
        if (Skip()) return;
        WithChromeView(view =>
        {
            Cursor.Position = new Point(40, 40); // Not resting on the view.
            view.SetInteractive(true);
            Assert.True(Item(view, "menu.view.interactive").Checked, "Chrome's view didn't become interactive.");
        },
        (window, view, desktop) =>
        {
            TestDesktop.Pump(1000);
            Shot("focus-after-switch");
            Assert.True(Item(view, "menu.view.interactive").Checked, "The focus Windows gave on the new desktop turned Interactive off.");
            Assert.True(Item(view, "menu.original.keepHidden").Checked, "The person's hide became something else.");
            Assert.False(view.HiddenWhileAway, "The person's hide was swapped for the temporary one.");
            Assert.NotEqual(window, User32.GetForegroundWindow());
        },
        atSwitch: Desktop.Activate); // As Windows does the moment the new desktop is in front.
    }

    [Fact]
    public void Keep_hidden_chosen_on_another_desktop_is_the_persons_own()
    {
        if (Skip()) return;
        WithChromeView((window, view, desktop) =>
        {
            TestDesktop.Pump(1500);
            Assert.True(view.HiddenWhileAway);
            view.SetOriginalHidden(true);
            Assert.False(view.HiddenWhileAway);
            Assert.True(view.KeepsUpdating);
            desktop.GoHome();
            TestDesktop.Pump(800);
            Assert.True(view.KeepsUpdating, "Coming back showed an original the person chose to keep hidden.");
        });
    }
}
