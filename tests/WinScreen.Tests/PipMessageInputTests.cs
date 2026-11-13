using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "Interactive")]
public sealed class PipMessageInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    /// <param name="restAt">Where the pointer rests when the view opens. By default just outside it: one resting
    /// where the view opens starts a visit before the test moves it.</param>
    private static void Drive(TargetProcess source, Action<PipWindow, Func<Action, bool>, WindowController> action, Point? restAt = null)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            Point home = Desktop.CursorPosition();
            Desktop.MoveMouse(restAt ?? new Point(860, 230));
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetCrop(new Rectangle(100, 80, 400, 300));
            view.SetInteractive(true);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Assert.True(windows.IsParked(source.Handle));
            Assert.Equal((byte)0, Desktop.Alpha(source.Handle));
            Exception? failure = null;
            bool Ui(Action step) { view.Invoke(step); return true; }
            var driver = new Thread(() =>
            {
                try { action(view, Ui, windows); }
                catch (Exception e) { failure = e; }
                finally { Desktop.MouseButton(false); Desktop.Key(Keys.ControlKey, false); Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            view.Close();
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            Assert.False(windows.IsParked(source.Handle));
        });
    }

    private static Point Hover(PipWindow view)
    {
        var pointer = new Point(view.Left + view.Width / 3, view.Top + view.Height / 3);
        Desktop.SlideMouse(pointer);
        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "The message visit did not start.");
        Thread.Sleep(100);
        return pointer;
    }

    [Theory]
    [InlineData(320, 20, false)]
    [InlineData(320, 20, true)]
    [InlineData(640, 220, false)]
    [InlineData(640, 220, true)]
    [InlineData(1200, 0, false)]
    [InlineData(1200, 0, true)]
    public void Right_click_opens_source_menu_across_the_entire_picture(int width, int top, bool framed)
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 1000, 700));
        if (framed) source.UseFrame();
        source.EnableContextMenu();
        PipInteraction.DiagnoseInput = true;
        try
        {
            Drive(source, (view, ui, _) =>
            {
                ui(() => { view.SetCrop(Rectangle.Empty); view.Bounds = new Rectangle(20, top, width, width * 7 / 10); });
                Hover(view);
                output.WriteLine($"sourceDpi={User32.GetDpiForWindow(source.Handle)} viewDpi={User32.GetDpiForWindow(view.Handle)} bounds={view.Bounds} backend={view.BackendKind}");
                Rectangle parked = Desktop.BoundsOf(source.Handle);
                Assert.True(User32.GetClientRect(source.Handle, out Rect client));
                var clientOrigin = new NativePoint();
                Assert.True(User32.ClientToScreen(source.Handle, ref clientOrigin));
                output.WriteLine($"clientOrigin={clientOrigin.X},{clientOrigin.Y} clientSize={client.Right},{client.Bottom} parked={parked}");
                // Sample the real client surface. A genuine native caption deliberately opens the view's menu.
                foreach (double y in new[] { .02, .06, .10, .15, .25, .50, .85 })
                foreach (double x in new[] { .25, .50, .75 })
                {
                    var live = view.LiveInteraction;
                    Assert.NotNull(live);
                    var (area, shown) = live.CurrentGeometry;
                    Point sourcePoint = new(clientOrigin.X - parked.Left + (int)(client.Right * x),
                        clientOrigin.Y - parked.Top + (int)(client.Bottom * y));
                    Point pointer = new(area.Left + (int)Math.Round((sourcePoint.X - shown.Left) * (double)area.Width / shown.Width),
                        area.Top + (int)Math.Round((sourcePoint.Y - shown.Top) * (double)area.Height / shown.Height));
                    Desktop.SlideMouse(pointer); Thread.Sleep(100);
                    mouse_event(8, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(16, 0, 0, 0, 0);
                    nint menu = 0;
                    bool opened = Desktop.WaitFor(() =>
                    {
                        while (source.NextReport(0) is { } report)
                            if (report.StartsWith("context-open ")) menu = (nint)long.Parse(report[13..]);
                        return menu != 0 && User32.IsWindowVisible(menu);
                    }, 1500);
                    Assert.True(opened, $"No source menu at {x:P0},{y:P0}: " + string.Join("\n", live.InputDiagnostics));
                    Assert.Equal(PressOutcome.Delivered, view.PressOutcomes.Last());
                    Assert.Equal(parked, Desktop.BoundsOf(source.Handle));
                    Desktop.Press(Keys.Escape);
                    Assert.True(Desktop.WaitFor(() => !User32.IsWindowVisible(menu)));
                    Thread.Sleep(100);
                }
            });
        }
        finally { PipInteraction.DiagnoseInput = false; }
    }

    [Fact]
    public void Each_button_double_click_and_wheel_arrives_once_with_coordinates_and_modifiers()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.RecordMessages();
        Drive(source, (view, ui, _) =>
        {
            Point pointer = Hover(view);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Point mapped = PipGeometry.PointToSource(new Point(pointer.X - view.Left, pointer.Y - view.Top), PipGeometry.ViewArea(view.ClientSize), view.Crop);
            while (source.NextReport(50) is not null) { }
            // Low-level input reaches the hook, while only posted messages reach the parked target.
            foreach (var (down, up, data) in new[] { (2u, 4u, 0u), (8u, 16u, 0u), (32u, 64u, 0u), (128u, 256u, 1u), (128u, 256u, 2u) })
            {
                mouse_event(down, 0, 0, data, 0);
                mouse_event(up, 0, 0, data, 0);
                Thread.Sleep(80);
            }
            Thread.Sleep(600);
            for (int i = 0; i < 2; i++) { mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(40); }
            mouse_event(0x800, 0, 0, 120, 0);
            Thread.Sleep(100);
            Desktop.Key(Keys.ControlKey, true);
            Thread.Sleep(50);
            mouse_event(0x800, 0, 0, 120, 0);
            Thread.Sleep(100);
            Desktop.Key(Keys.ControlKey, false);
            mouse_event(0x1000, 0, 0, 120, 0);
            Thread.Sleep(250);
            var reports = new List<string>();
            while (source.NextReport(150) is { } report) reports.Add(report);
            var messages = reports.Where(report => report.StartsWith("mouse ")).Select(report => report.Split(' ')).ToArray();
            int Count(string id) => messages.Count(message => message[1] == id);
            Assert.Equal(2, Count("201"));
            Assert.Equal(1, Count("203"));
            Assert.Equal(3, Count("202"));
            Assert.Equal(1, Count("204")); Assert.Equal(1, Count("205"));
            Assert.Equal(1, Count("207")); Assert.Equal(1, Count("208"));
            Assert.Equal(2, Count("20B")); Assert.Equal(2, Count("20C"));
            Assert.Equal(2, Count("20A")); Assert.Equal(1, Count("20E"));
            foreach (string[] message in messages.Where(message => message[1] is not "20A" and not "20E"))
            {
                Assert.Equal(mapped.X, int.Parse(message[2]));
                Assert.Equal(mapped.Y, int.Parse(message[3]));
            }
            string[][] wheels = messages.Where(message => message[1] == "20A").ToArray();
            Assert.Contains(wheels, message => (long.Parse(message[4]) & 8) != 0);
            Assert.Contains(wheels, message => (long.Parse(message[4]) & 8) == 0);
            Assert.All(wheels, message => { Assert.Equal(parked.Left + mapped.X, int.Parse(message[2])); Assert.Equal(parked.Top + mapped.Y, int.Parse(message[3])); });
            Assert.Equal(7, view.PressOutcomes.Count(outcome => outcome == PressOutcome.Delivered));
            Assert.Equal(parked, Desktop.BoundsOf(source.Handle));

        });
    }

    [Fact]
    public void Quick_moves_and_presses_deliver_each_press_once()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.RecordMessages();
        Drive(source, (view, _, _) =>
        {
            Hover(view);
            while (source.NextReport(50) is not null) { }
            var random = new Random(23);
            for (int i = 0; i < 40; i++)
            {
                Desktop.SlideMouse(new Point(view.Left + 20 + random.Next(view.Width - 40), view.Top + 20 + random.Next(view.Height - 40)));
                Thread.Sleep(5);
                mouse_event(2, 0, 0, 0, 0);
                mouse_event(4, 0, 0, 0, 0);
                Thread.Sleep(10);
            }
            Thread.Sleep(200);
            var reports = new List<string>();
            while (source.NextReport(150) is { } report) reports.Add(report);
            Assert.Equal(40, view.PressOutcomes.Count(outcome => outcome == PressOutcome.Delivered));
            Assert.Equal(40, reports.Count(report => report.StartsWith("mouse 201 ") || report.StartsWith("mouse 203 ")));
        });
    }

    [Fact]
    public void A_drag_posts_held_moves_to_the_source_and_a_key_reaches_it_after_a_click()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.RecordMessages();
        Drive(source, (view, _, _) =>
        {
            Point pointer = Hover(view);
            Rectangle before = view.Bounds, parked = Desktop.BoundsOf(source.Handle);
            Desktop.MouseButton(true);
            for (int i = 1; i <= 5; i++) { Desktop.SlideMouse(pointer + new Size(i * 12, i * 8)); Thread.Sleep(30); }
            Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == source.Handle));
            Desktop.Press(Keys.J);
            Thread.Sleep(200);
            var reports = new List<string>();
            while (source.NextReport(100) is { } report) reports.Add(report);
            Assert.Contains(reports, report => report.StartsWith("mouse 200 ") && (long.Parse(report.Split(' ')[4]) & 1) != 0);
            Assert.Contains(reports, report => report.StartsWith("key j"));
            Assert.Equal(before, view.Bounds);
            Assert.Equal(parked, Desktop.BoundsOf(source.Handle));
        });
    }

    [Fact]
    public void Interactive_popups_are_relocated_to_the_view_and_display_only_popups_stay_hidden()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        Drive(source, (view, _, _) =>
        {
            Point pointer = Hover(view);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            nint popup = source.ShowPopup(new Point(parked.Left + 230, parked.Top + 180));
            Assert.True(Desktop.WaitFor(() => Screen.FromPoint(pointer).WorkingArea.Contains(Desktop.BoundsOf(popup))), Desktop.BoundsOf(popup).ToString());
            Assert.True(Desktop.WaitFor(() => Desktop.TopWindowAt(new Point(Desktop.BoundsOf(popup).Left + 30, Desktop.BoundsOf(popup).Top + 20)) == popup));
            Thread.Sleep(1400);
            Assert.True(view.LiveInteraction is not null, "Visit ended before popup click: " + view.LastVisitEnd);
            Rectangle placed = Desktop.BoundsOf(popup);
            Assert.True(Math.Abs(placed.Left - pointer.X) <= placed.Width, $"Placed {placed}, pointer {pointer}, parked {parked}, view {view.Bounds}, screen {Screen.FromPoint(pointer).WorkingArea}");
            Desktop.ClickOn(new Point(placed.Left + 30, placed.Top + 20), popup);
            Assert.Contains(PressOutcome.Popup, view.PressOutcomes);
            nint tip = source.ShowPopup(new Point(pointer.X + 20, pointer.Y + 20), displayOnly: true);
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(tip).Right <= SystemInformation.VirtualScreen.Left));
        });
    }

    /// <summary>
    /// A visit can begin where the pointer rested when the view opened, and the pointer can move on before the
    /// visit's hook exists. A menu opened then still belongs next to where the pointer really is.
    /// </summary>
    [Fact]
    public void A_menu_opened_before_any_move_is_seen_appears_by_the_pointer()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        Drive(source, (view, _, _) =>
        {
            Point now = new(view.Left + view.Width / 3, view.Top + view.Height / 3);
            Desktop.MoveMouse(now); // At once, while the visit is still starting.
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit.");
            Thread.Sleep(300);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            var (area, shown) = view.LiveInteraction!.CurrentGeometry;
            Point spot = PipGeometry.PointToSource(now, area, shown);
            nint popup = source.ShowPopup(new Point(parked.Left + spot.X + 5, parked.Top + spot.Y + 5));
            Assert.True(Desktop.WaitFor(() => Distance(Desktop.BoundsOf(popup).Location, now) < 60, 2000),
                $"The menu is at {Desktop.BoundsOf(popup).Location}, the pointer at {now}.");
        }, restAt: new Point(900 + 400, 250 + 280)); // Inside where the view opens, far from where the pointer goes.
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>
    /// Popups the app makes on another UI thread, or from a helper process, are still its popups: moved next to the
    /// pointer, clickable, and put back afterwards. A window that merely sits near the original is left alone.
    /// </summary>
    [Fact]
    public void Popups_of_other_threads_and_processes_follow_the_pointer_and_go_back()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        TargetProcess? helper = null, stranger = null;
        nint threadPopup = 0;
        Point threadAt = default;
        try
        {
            Drive(source, (view, _, _) =>
            {
                Point pointer = Hover(view);
                Rectangle parked = Desktop.BoundsOf(source.Handle);
                Point at = new(parked.Left + 230, parked.Top + 180);

                threadAt = at;
                threadPopup = source.ShowThreadPopup(at);
                Assert.True(Desktop.WaitFor(() => Distance(Desktop.BoundsOf(threadPopup).Location, pointer) < 200, 2000),
                    $"Another thread's popup stayed at {Desktop.BoundsOf(threadPopup).Location}; pointer {pointer}.");
                Rectangle placed = Desktop.BoundsOf(threadPopup);
                Desktop.ClickOn(new Point(placed.Left + 30, placed.Top + 20), threadPopup);
                Assert.True(Desktop.WaitFor(() => source.NextReport(200) is { } r && r.StartsWith("popup-click"), 2000), "The popup took no click.");
                Assert.Contains(PressOutcome.Popup, view.PressOutcomes);

                helper = new TargetProcess(new Rectangle(at.X, at.Y + 60, 160, 40), owner: source.Handle);
                Assert.True(Desktop.WaitFor(() => Distance(Desktop.BoundsOf(helper.Handle).Location, pointer) < 200, 2000),
                    $"Another process's popup stayed at {Desktop.BoundsOf(helper.Handle).Location}; pointer {pointer}.");

                // Not owned by the original: not its popup, wherever it is.
                stranger = new TargetProcess(new Rectangle(at.X, at.Y + 120, 160, 40));
                Rectangle strangerAt = Desktop.BoundsOf(stranger.Handle);
                Thread.Sleep(500);
                Assert.Equal(strangerAt, Desktop.BoundsOf(stranger.Handle));
            });
            // The view closed: every popup it moved is back where its app put it.
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(threadPopup).Location == threadAt, 2000),
                $"The thread popup is at {Desktop.BoundsOf(threadPopup).Location}, not {threadAt}.");
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(helper!.Handle).Location == threadAt + new Size(0, 60), 2000),
                $"The helper popup is at {Desktop.BoundsOf(helper!.Handle).Location}.");
        }
        finally { helper?.Dispose(); stranger?.Dispose(); }
    }

    [Fact]
    public void Child_controls_receive_their_own_client_coordinates()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.AddButton();
        Drive(source, (view, ui, _) =>
        {
            ui(() => view.SetCrop(new Rectangle(0, 0, 400, 300)));
            Rectangle picture = PipGeometry.ViewArea(view.ClientSize);
            Point pointer = new(view.Left + picture.Left + 60 * picture.Width / 400,
                view.Top + picture.Top + 50 * picture.Height / 300);
            Desktop.SlideMouse(pointer);
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
            Thread.Sleep(100);
            Desktop.MouseButton(true);
            Thread.Sleep(40);
            Desktop.MouseButton(false);
            string? downReport = source.NextReport(), upReport = source.NextReport();
            Assert.True(downReport is not null && upReport is not null,
                $"Missing child messages. Outcomes: {string.Join(", ", view.PressOutcomes)}; ended: {view.LastVisitEnd}");
            string[] down = downReport!.Split(' ');
            string[] up = upReport!.Split(' ');
            Assert.Equal("child", down[0]); Assert.Equal("201", down[1]);
            Assert.Equal("child", up[0]); Assert.Equal("202", up[1]);
            foreach (string[] message in new[] { down, up })
            {
                Assert.InRange(int.Parse(message[2]), 39, 41);
                Assert.InRange(int.Parse(message[3]), 19, 21);
            }
        });
    }

    /// <summary>
    /// Windows' double-click rule, through a view: until the double-click time has passed, the pointer staying inside
    /// the double-click rectangle isn't a move, and the second press lands where the first did. In a view shown smaller
    /// one pixel of jitter is several in the app, which would count the two presses as apart.
    /// </summary>
    [Fact]
    public void A_double_click_with_jitter_lands_on_one_point_and_moves_resume_after_the_double_click_time()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.RecordMessages();
        Drive(source, (view, _, _) =>
        {
            Point pointer = Hover(view);
            Thread.Sleep(150);
            while (source.NextReport(50) is not null) { }
            mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(40);
            Desktop.NudgeMouse(1, 1); Thread.Sleep(40);
            mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); Desktop.NudgeMouse(-1, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0);
            Thread.Sleep(200);
            var reports = new List<string>();
            while (source.NextReport(100) is { } report) reports.Add(report);
            string all = string.Join("\n", reports);
            string[][] messages = reports.Where(r => r.StartsWith("mouse ")).Select(r => r.Split(' ')).ToArray();
            Assert.DoesNotContain(messages, m => m[1] == "200");
            string[] first = Assert.Single(messages, m => m[1] == "201");
            string[] second = Assert.Single(messages, m => m[1] == "203"); // The window asks for double-clicks.
            Assert.Equal((first[2], first[3]), (second[2], second[3]));
            Assert.All(messages.Where(m => m[1] == "202"), m => Assert.Equal((first[2], first[3]), (m[2], m[3])));

            // Once the double-click time has passed, the same small move is a move again.
            Thread.Sleep((int)GetDoubleClickTime() + 100);
            Desktop.NudgeMouse(1, 0);
            Assert.True(Desktop.WaitFor(() => source.NextReport(100) is { } r && r.StartsWith("mouse 200 "), 1500), "Moves didn't resume.\n" + all);
        });
    }

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    [Fact]
    public void Moves_reach_the_source_only_at_mapped_points_while_it_holds_the_mouse()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.BehaveLikeBrowser(); // Holds the mouse on a press and asks to hear when the pointer leaves.
        Drive(source, (view, _, _) =>
        {
            Point pointer = Hover(view);
            while (source.NextReport(50) is not null) { }
            Desktop.MouseButton(true);
            for (int i = 1; i <= 6; i++) { Desktop.SlideMouse(pointer + new Size(i * 10, i * 6)); Thread.Sleep(40); }
            Desktop.MouseButton(false);
            Thread.Sleep(200);
            var moves = new List<string[]>();
            while (source.NextReport(150) is { } report)
                if (report.StartsWith("mouse 200 ")) moves.Add(report.Split(' '));
            Assert.True(moves.Count >= 6, $"Only {moves.Count} moves arrived.");
            // A stray real move would arrive relative to the parked window, far outside its client area.
            Assert.All(moves, move =>
            {
                Assert.InRange(int.Parse(move[2]), 0, 799);
                Assert.InRange(int.Parse(move[3]), 0, 599);
            });
        });
    }

    [Fact]
    public void During_a_visit_the_pointer_takes_the_shape_the_app_sets()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.BehaveLikeBrowser(); // Sets a text cursor over its content.
        Drive(source, (view, ui, controller) =>
        {
            Hover(view);
            nint SetCursorOnView()
            {
                nint shown = 0;
                ui(() =>
                {
                    Cursor.Current = Cursors.Default;
                    User32.SendMessageTimeout(view.Handle, 0x0020, view.Handle, (nint)(1 | (0x200 << 16)), 0, 500, out _);
                    shown = Cursor.Current.Handle;
                });
                return shown;
            }
            Assert.True(view.LiveInteraction!.SharesInput, "Input wasn't shared during the visit.");
            // The app sets its shape as it handles the moves it's sent; the pointer seen over the view follows.
            Desktop.NudgeMouse(1, 0);
            Assert.True(Desktop.WaitFor(() => ShownCursor() == Cursors.IBeam.Handle, 2000), $"The pointer showed 0x{ShownCursor():X}.");
            Assert.True(windowsReachable(view, true), "The parked original wasn't reachable during the visit.");
            Desktop.SlideMouse(new Point(view.Left - 60, view.Top - 60));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null), "The visit didn't end.");
            Assert.True(windowsReachable(view, false), "The parked original stayed reachable after the visit.");
            Assert.Equal(Cursors.Default.Handle, SetCursorOnView()); // Outside a visit the view sets its own.
        });

        bool windowsReachable(PipWindow view, bool expected) => Desktop.WaitFor(() =>
            (Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT) == 0 == expected &&
            Desktop.Alpha(source.Handle) == (expected ? 1 : 0));
    }

    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    [Fact]
    public void A_browser_scrolls_under_the_wheel_in_a_view_used_directly()
    {
        if (!File.Exists(EdgePath)) { Skipped.Because("Edge isn't installed."); return; }
        string root = Directory.CreateTempSubdirectory("winscreen-wheel").FullName;
        using var browser = new System.Diagnostics.Process();
        try
        {
            string page = Path.Combine(root, "page.html");
            File.WriteAllText(page, """
                <!doctype html><title>WheelPage w=0 y=0</title>
                <style>body{margin:0}div{height:6000px;background:linear-gradient(#fff,#08f)}</style><div></div>
                <script>let w=0;function r(){document.title=`WheelPage w=${w} y=${Math.round(scrollY)}`};addEventListener('wheel',()=>{w++;r()},{passive:true});addEventListener('scroll',r);</script>
                """);
            browser.StartInfo = new System.Diagnostics.ProcessStartInfo(EdgePath) { UseShellExecute = false };
            foreach (string arg in new[] { "--user-data-dir=" + Path.Combine(root, "profile"), "--no-first-run", "--no-default-browser-check",
                "--disable-sync", "--window-size=900,700", "--window-position=160,100", "--app=" + new Uri(page).AbsoluteUri })
                browser.StartInfo.ArgumentList.Add(arg);
            Assert.True(browser.Start());
            nint edge = 0;
            Assert.True(Desktop.WaitFor(() => { edge = User32.FindWindow(null, "WheelPage w=0 y=0"); return edge != 0; }, 20000), "The browser didn't open.");
            Thread.Sleep(1000);
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(edge, new WindowController([])) { AllowInteractive = true };
                view.ShowOnTop();
                view.Location = new Point(1000, 250);
                view.SetInteractive(true);
                Exception? failure = null;
                Point home = Desktop.CursorPosition();
                var driver = new Thread(() =>
                {
                    try
                    {
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "The visit didn't start.");
                        Thread.Sleep(300);
                        for (int i = 0; i < 3; i++) { mouse_event(0x800, 0, 0, unchecked((uint)-120), 0); Thread.Sleep(80); }
                        Assert.True(Desktop.WaitFor(() => !WindowInfo.Title(edge).EndsWith(" y=0"), 3000), WindowInfo.Title(edge));
                    }
                    catch (Exception e) { failure = e; }
                    finally { Desktop.MoveMouse(home); }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            });
        }
        finally
        {
            try { if (!browser.HasExited) browser.Kill(true); } catch (InvalidOperationException) { }
            browser.WaitForExit(5000);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_dialog_reveals_the_parked_original()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        Drive(source, (view, _, windows) =>
        {
            Hover(view);
            source.ShowDialog();
            Assert.True(Desktop.WaitFor(() => !windows.IsHidden(source.Handle)));
            Assert.Null(view.LiveInteraction);
            Assert.False(windows.IsParked(source.Handle));
        });
    }

    [Fact]
    public void The_first_visible_restoration_frame_contains_source_pixels_instead_of_black()
    {
        using var backdrop = new TargetProcess(Screen.PrimaryScreen!.WorkingArea, "E0E0E0");
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            view.ShowOnTop();
            view.SetOriginalHidden(true);
            using var ready = new ManualResetEventSlim();
            using var stop = new ManualResetEventSlim();
            var colors = new List<Color>();
            var sampler = new Thread(() =>
            {
                ready.Set();
                while (!stop.IsSet)
                {
                    colors.Add(Desktop.ColorOnScreen(new Rectangle(450, 300, 8, 8)));
                    Thread.Sleep(2);
                }
            });
            sampler.Start();
            ready.Wait();
            try { view.Close(); Thread.Sleep(80); }
            finally { stop.Set(); sampler.Join(); }
            Assert.NotEmpty(colors);
            Assert.All(colors, color => Assert.True(color.R > 70, $"A black restoration frame appeared: {color}"));
            Assert.Contains(colors, color => Desktop.IsNear(color, Color.FromArgb(0xC8, 0x1E, 0x1E)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_a_parked_view_restores_placement_opacity_and_maximized_state(bool maximized)
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        if (maximized) User32.ShowWindow(source.Handle, 3);
        Rectangle before = Desktop.BoundsOf(source.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows);
            view.ShowOnTop();
            view.SetOriginalHidden(true);
            Assert.True(windows.IsParked(source.Handle));
            view.Close();
            Assert.False(windows.IsParked(source.Handle));
            Assert.False(windows.IsHidden(source.Handle));
            Assert.Equal(maximized, User32.IsZoomed(source.Handle));
            Assert.Equal(before, Desktop.BoundsOf(source.Handle));
            Assert.True(Desktop.Alpha(source.Handle) is null or 255);
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public int Flags; public nint Cursor; public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);

    private static nint ShownCursor()
    {
        var info = new CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) ? info.Cursor : 0;
    }
}
