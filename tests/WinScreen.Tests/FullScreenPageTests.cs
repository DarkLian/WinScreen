using System.Runtime.InteropServices;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A page that goes full screen, as a video does, while its browser is used through a view. While the view is open
/// the browser sits past the edge of the screen, and a browser remembers where it was when it went full screen so it
/// can go back there afterwards. It must still end up where it was before the view.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class FullScreenPageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Edge in the Windows 10 VM, Chrome on the Windows 11 PC.</summary>
    private static string? Browser => new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
    }.FirstOrDefault(File.Exists);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private const string Page = """
        <!doctype html><title>Movie fs=0</title>
        <style>html,body{margin:0;height:100%;background:#246}</style>
        <script>const r=()=>document.title=`Movie fs=${document.fullscreenElement?1:0}`;
        document.ondblclick=()=>document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen();
        document.onfullscreenchange=r;document.onkeydown=e=>{if(e.key==='x'&&document.fullscreenElement)document.exitFullscreen()};</script>
        """;

    public enum When { BeforeTheView, InTheView, AfterTheView }

    [Theory]
    [InlineData(When.InTheView, When.AfterTheView)] // Full screen from the view, Esc once the view is closed.
    [InlineData(When.InTheView, When.InTheView)]
    [InlineData(When.BeforeTheView, When.AfterTheView)] // A video already full screen when the view opens.
    [InlineData(When.BeforeTheView, When.InTheView)]
    public void A_page_that_goes_full_screen_around_a_view_ends_up_where_the_window_was(When enter, When leave)
    {
        if (Browser is not { } path) return;
        if (enter == When.BeforeTheView) Desktop.GainForegroundRights(); // The page is put in full screen from here first.
        var (browser, window, root) = BrowserInputTests.Open(Page, "Movie", app: true, path);
        output.WriteLine(path);
        var log = new StringBuilder();
        try
        {
            Assert.True(window != 0, "The browser didn't open the page.");
            string Title() => WindowInfo.Title(window);
            Rectangle home = Desktop.BoundsOf(window);
            Rectangle screen = Screen.FromHandle(window).Bounds;
            log.AppendLine($"home {home} screen {screen}");
            if (enter == When.BeforeTheView)
            {
                Desktop.Activate(window);
                Desktop.DoubleClick(new Point(home.Left + home.Width / 2, home.Top + home.Height / 2), window);
                Assert.True(Desktop.WaitFor(() => Title().Contains("fs=1"), 4000), "The page didn't go full screen: " + Title());
                Thread.Sleep(800);
                log.AppendLine($"full screen before the view at {Desktop.BoundsOf(window)}");
            }

            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]) { ParkedPlaceSteps = new() };
                Point pointer = Desktop.CursorPosition();
                Desktop.MoveMouse(new Point(screen.Left + 40, screen.Bottom - 120));
                using var view = new PipWindow(window, windows) { AllowInteractive = true };
                view.Problem += problem => log.AppendLine("problem: " + problem);
                view.Notice += notice => log.AppendLine("notice: " + notice);
                view.ShowOnTop();
                view.Location = new Point(screen.Right - 560, screen.Top + 200);
                view.SetInteractive(true);
                Exception? failure = null;
                var driver = new Thread(() =>
                {
                    try
                    {
                        Point middle = new(view.Left + view.Width / 2, view.Top + view.Height / 2);
                        Desktop.SlideMouse(middle);
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No direct-input visit started.");
                        Assert.True(windows.IsParked(window), "The original wasn't parked.");
                        Rectangle[] monitors = [.. Screen.AllScreens.Select(s => s.Bounds)];
                        // While the pointer is over the view the parked original is at opacity 1. A page in full screen
                        // makes the browser cover its monitor again (and move straight back if moved), so there it
                        // must be wholly see-through and let clicks pass, or it shows as a faint layer over the screen.
                        bool Unseen() => WindowController.OutOfReach(Desktop.BoundsOf(window), monitors) ||
                            (Desktop.Alpha(window) == 0 && (Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT) != 0);
                        void StaysOff(string when)
                        {
                            Thread.Sleep(600);
                            Assert.True(Desktop.WaitFor(Unseen, 2000),
                                $"{when}: the original shows at {Desktop.BoundsOf(window)}, opacity {Desktop.Alpha(window)}, ex style 0x{Desktop.ExStyle(window):X}; " +
                                $"parked {windows.IsParked(window)}, visit {view.LiveInteraction is not null}, last visit end '{view.LastVisitEnd}', revealed at {view.LastReveal}, now {Environment.TickCount64}, state {view.StateDescription}.\n{log}");
                            Assert.NotNull(view.LiveInteraction); // Still in use, so this was checked during a visit.
                        }
                        log.AppendLine($"parked at {Desktop.BoundsOf(window)}");
                        StaysOff("Once parked");
                        Thread.Sleep(300);
                        if (enter == When.InTheView)
                        {
                            DoubleClick(middle);
                            Assert.True(Desktop.WaitFor(() => Title().Contains("fs=1"), 4000), "The page didn't go full screen through the view: " + Title());
                            Thread.Sleep(1000);
                            log.AppendLine($"full screen while parked at {Desktop.BoundsOf(window)}");
                            StaysOff("After going full screen in the view");
                        }
                        if (leave == When.InTheView)
                        {
                            Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit to leave full screen.");
                            Thread.Sleep(300);
                            DoubleClick(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                            Assert.True(Desktop.WaitFor(() => Title().Contains("fs=0"), 4000), "The page didn't leave full screen through the view: " + Title());
                            Thread.Sleep(1000);
                            log.AppendLine($"left full screen while parked at {Desktop.BoundsOf(window)}");
                            StaysOff("After leaving full screen in the view");
                        }
                    }
                    catch (Exception e) { failure = e; }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(pointer); }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
                foreach (string step in windows.ParkedPlaceSteps!) log.AppendLine("step: " + step);
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure + Environment.NewLine + log);
                for (int i = 0; i < 4; i++) { Thread.Sleep(200); Application.DoEvents(); log.AppendLine($"closed +{(i + 1) * 200} ms: {Desktop.BoundsOf(window)} ex 0x{Desktop.ExStyle(window):X} opacity {Desktop.Alpha(window)}"); }
                log.AppendLine($"after the view at {Desktop.BoundsOf(window)} ({Title()})");
                Assert.Equal(leave == When.AfterTheView, windows.IsWatchingReturn(window));
                if (leave != When.AfterTheView) return;

                // Leaving full screen long after the view closed: this thread keeps taking messages, as the app's does.
                var leaver = new Thread(() =>
                {
                    try
                    {
                        // Still full screen: it covers its screen, as it did before it was parked.
                        Assert.True(Title().Contains("fs=1"), "The page left full screen by itself: " + Title());
                        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window) == screen, 3000),
                            $"Still full screen, but at {Desktop.BoundsOf(window)} instead of covering {screen}.");
                        Thread.Sleep(1500);
                        Desktop.Activate(window);
                        Desktop.Press(Keys.Escape);
                        if (!Desktop.WaitFor(() => Title().Contains("fs=0"), 2000)) Desktop.Press(Keys.X);
                        Assert.True(Desktop.WaitFor(() => Title().Contains("fs=0"), 4000), "The page didn't leave full screen: " + Title());
                        Thread.Sleep(1000);
                        log.AppendLine($"left full screen at {Desktop.BoundsOf(window)}");
                    }
                    catch (Exception e) { failure = e; }
                });
                leaver.Start();
                while (leaver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure + Environment.NewLine + log);
                Assert.False(windows.IsWatchingReturn(window), "Still watching after it left full screen.");
            });

            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window) == home, 3000),
                $"The window is at {Desktop.BoundsOf(window)}, not back at {home}.\n{log}");
            Assert.False(WindowInfo.IsConcealed(window));
        }
        finally
        {
            output.WriteLine(log.ToString());
            BrowserInputTests.Close(browser, root);
        }
    }

    private static void DoubleClick(Point at)
    {
        Desktop.SlideMouse(at);
        Thread.Sleep(150);
        for (int i = 0; i < 2; i++) { mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(50); }
    }
}
