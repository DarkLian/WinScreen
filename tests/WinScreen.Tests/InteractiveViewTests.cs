using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PressRuleTests
{
    [Theory]
    [InlineData(1, true)]   // Content.
    [InlineData(5, true)]   // A native menu bar.
    [InlineData(6, true)]   // Scroll bars.
    [InlineData(7, true)]
    [InlineData(2, false)]  // Title bar.
    [InlineData(3, false)]  // System menu.
    [InlineData(8, false)]  // Minimize, maximize and close.
    [InlineData(9, false)]
    [InlineData(20, false)]
    [InlineData(10, false)] // Every resize edge and corner.
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(17, false)]
    [InlineData(0, false)]  // Nowhere, transparent and error.
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    public void Only_content_takes_a_press(int hit, bool reaches) =>
        Assert.Equal(reaches, PressRules.ReachesContent(hit));

    [Fact]
    public void Mixed_scaling_is_refused()
    {
        Assert.True(PressRules.SharedScaling([100]));
        Assert.True(PressRules.SharedScaling([150, 150]));
        Assert.False(PressRules.SharedScaling([100, 125]));
    }
}

[Trait("Category", "RealWindows")]
public sealed class InteractiveRecoveryTests
{
    [Fact]
    public void A_forced_kill_during_a_slide_puts_the_window_back_in_place()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-interactive-").FullName;
        try
        {
            string file = Path.Combine(folder, "changed-windows.json");
            using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
            target.UseFrame();
            Rectangle home = Desktop.BoundsOf(target.Handle);
            var windows = new WindowController([], new ChangeJournal(file));
            windows.Hide(target.Handle);
            windows.BeginInteractive(target.Handle, User32.HWND_TOPMOST);
            Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
            User32.SetWindowPos(target.Handle, 0, 700, 400, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Assert.NotEqual(home, Desktop.BoundsOf(target.Handle));

            // The next copy has only what reached the disk, as a watcher or a restart would.
            var next = new WindowController([], new ChangeJournal(file));
            next.RecoverFromJournal();
            Assert.Equal(home, Desktop.BoundsOf(target.Handle));
            Assert.False(WindowInfo.IsConcealed(target.Handle));
            Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Ending_a_visit_takes_clicks_away_and_returns_the_window()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        target.UseFrame();
        Rectangle home = Desktop.BoundsOf(target.Handle);
        var windows = new WindowController([]);
        windows.Hide(target.Handle);
        windows.BeginInteractive(target.Handle, User32.HWND_TOPMOST);
        User32.SetWindowPos(target.Handle, 0, 700, 400, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        windows.EndInteractive(target.Handle);
        Assert.Equal(home, Desktop.BoundsOf(target.Handle));
        Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
        Assert.True(WindowInfo.IsConcealed(target.Handle));
        windows.RestoreAll();
        Assert.False(WindowInfo.IsConcealed(target.Handle));
    }

    [Fact]
    public void A_window_that_is_not_hidden_cannot_take_input_invisibly()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        Assert.Throws<WindowOperationException>(() => new WindowController([]).BeginInteractive(target.Handle, User32.HWND_TOPMOST));
    }
}

/// <summary>Real pointer input on the live desktop, limited to these tests' own windows.</summary>
[Trait("Category", "Interactive")]
public sealed class InteractiveViewTests
{
    private static readonly Rectangle SourceSpot = new(120, 120, 500, 400);

    /// <summary>Runs the steps on a worker with the view's thread pumping, as in the app.</summary>
    private static void Drive(TargetProcess source, Action<PipWindow, Func<Action, bool>> steps, Point? viewAt = null) =>
        Drive(source.Handle, steps, viewAt);

    private static void Drive(nint source, Action<PipWindow, Func<Action, bool>> steps, Point? viewAt = null)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source, new WindowController([])) { AllowInteractive = true };
            view.ApplyViewPreferences(new PipViewPreferences { CompactTitleBar = false });
            var problems = new List<string>();
            view.Problem += problems.Add;
            view.Notice += problems.Add;
            view.ShowOnTop();
            view.Location = viewAt ?? new Point(1100, 500);
            view.SetInteractive(true);
            Assert.True(view.KeepsUpdating,
                $"Interactive source was not concealed: {string.Join("; ", problems)}; source=0x{source:X}; view={view.Bounds}");
            Application.DoEvents();
            bool Ui(Action action)
            {
                view.Invoke(action);
                return true;
            }

            Exception? failure = null;
            Point home = Desktop.CursorPosition();
            var driver = new Thread(() =>
            {
                try { steps(view, Ui); }
                catch (Exception exception) { failure = exception; }
                finally { Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive)
            {
                Application.DoEvents();
                Thread.Sleep(2);
            }

            view.Close();
            if (failure is not null)
                throw new Xunit.Sdk.XunitException(failure.ToString());
        });
    }

    private static void Hover(Point point, PipWindow view)
    {
        Desktop.MoveMouse(new Point(point.X - 1, point.Y));
        Thread.Sleep(30);
        Desktop.MoveMouse(point);
        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null, 2000),
            $"The visit didn't start: pointer={Desktop.CursorPosition()}; view={view.Bounds}; visible={view.Visible}; {view.StateDescription}; {view.DiagnosticSnapshot}");
        Thread.Sleep(100);
    }

    private static void Slide(Point to)
    {
        Desktop.SlideMouse(to);
        Thread.Sleep(60);
    }

    [Fact]
    public void A_press_lands_on_the_point_the_view_shows_and_the_visit_leaves_nothing_behind()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Rectangle home = Desktop.BoundsOf(source.Handle);
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            var inside = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
            Hover(inside, view);
            Assert.Equal(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT);
            Assert.True(WindowInfo.IsConcealed(source.Handle));
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            string? report = source.NextReport();
            Assert.NotNull(report);
            Assert.Equal(PressOutcome.Delivered, view.LiveInteraction!.Log.Single().Outcome);
            int reportedX = int.Parse(report.Split(' ')[1]), reportedY = int.Parse(report.Split(' ')[2]);
            Assert.InRange(reportedX, 150, 350); // The middle of a 500 by 400 window.
            Assert.InRange(reportedY, 120, 280);

            Desktop.MoveMouse(new Point(frame.Left - 40, frame.Top - 40));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000), "The visit didn't end.");
            Assert.True(Desktop.WaitFor(() => PipInteraction.Active == 0, 2000), "The hook stayed.");
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
            Assert.NotEqual(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT);
            Assert.True(WindowInfo.IsConcealed(source.Handle));
        });
    }

    [Fact]
    public void A_popup_of_the_original_outside_the_view_is_kept_off_screen_until_the_visit_ends()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            var inside = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
            Hover(inside, view);
            var away = new Point(40, Screen.PrimaryScreen!.WorkingArea.Bottom - 80);
            nint popup = source.ShowPopup(away);
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(popup).Right <= SystemInformation.VirtualScreen.Left, 2000),
                $"The popup stayed on screen: {Desktop.BoundsOf(popup)}; live={view.LiveInteraction is not null}; " +
                $"rootowner=0x{User32.GetAncestor(popup, User32.GA_ROOTOWNER):X} source=0x{source.Handle:X}; " +
                $"threads {User32.GetWindowThreadProcessId(popup, out uint p1)}/{User32.GetWindowThreadProcessId(source.Handle, out uint p2)}; " +
                $"visible={Desktop.IsWindowVisible(popup)}");

            Desktop.MoveMouse(new Point(frame.Left - 40, frame.Top - 40));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000), "The visit didn't end.");
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(popup).Location == away, 2000),
                "The popup didn't come back: " + Desktop.BoundsOf(popup));
        });
    }

    [Fact]
    public void A_press_on_the_hidden_title_bar_moves_the_view_and_never_reaches_the_window()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Rectangle home = Desktop.BoundsOf(source.Handle);
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            Rectangle area = new(frame.Left + PipGeometry.Border, frame.Top + PipGeometry.Border,
                frame.Width - 2 * PipGeometry.Border, frame.Height - 2 * PipGeometry.Border);
            var title = new Point(area.Left + area.Width / 3, area.Top + 10);
            Hover(title, view);
            Desktop.MouseButton(true);
            for (int step = 1; step <= 5; step++)
                Slide(new Point(title.X + step * 12, title.Y + step * 8));
            Desktop.MouseButton(false);
            Assert.Null(source.NextReport(200));
            var press = view.LiveInteraction!.Log.Single();
            Assert.Equal(PressOutcome.MovedView, press.Outcome);
            Assert.False(PressRules.ReachesContent(press.Hit));
            Assert.True(Desktop.WaitFor(() => view.Location == new Point(frame.Left + 60, frame.Top + 40)), view.Location.ToString());
            Desktop.MoveMouse(new Point(40, 40));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000));
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
        });
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(1, 50)]
    [InlineData(50, -1)]
    [InlineData(50, 1)]
    public void A_press_just_outside_an_edge_never_reaches_the_window(int edgeX, int edgeY)
    {
        using var catcher = new TargetProcess(new Rectangle(1000, 400, 860, 560), "1E64C8");
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            int x = edgeX switch { -1 => frame.Left - 1, 1 => frame.Right, _ => frame.Left + frame.Width / 2 };
            int y = edgeY switch { -1 => frame.Top - 1, 1 => frame.Bottom, _ => frame.Top + frame.Height / 2 };
            Hover(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2), view);
            Desktop.MoveMouse(new Point(x, y));
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Assert.Null(source.NextReport(300));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000), "Leaving didn't end the visit.");
            Assert.True(WindowInfo.IsConcealed(source.Handle));
        });
    }

    [Fact]
    public void Keys_go_back_to_the_previous_window_when_the_pointer_leaves()
    {
        using var other = new TargetProcess(new Rectangle(1000, 80, 300, 200), "1E64C8");
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Desktop.GainForegroundRights();
        Desktop.Activate(other.Handle);
        Assert.Equal(other.Handle, User32.GetForegroundWindow());
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            Hover(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2), view);
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Assert.NotNull(source.NextReport());
            Desktop.MoveMouse(new Point(1100, 150));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000));
            Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() != source.Handle, 1000),
                "Keys were left going to the hidden window.");
            Assert.Equal(other.Handle, User32.GetForegroundWindow());
        });
    }

    [Fact]
    public void Every_press_in_a_quick_run_is_accounted_for_and_delivered_ones_all_arrive()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            Rectangle frame = view.Bounds;
            Hover(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2), view);
            PipInteraction live = view.LiveInteraction!;
            var rng = new Random(11);
            const int presses = 120;
            for (int i = 0; i < presses; i++)
            {
                // Moved as input, as a real mouse is: the view stops a visit whose hook stops seeing the pointer.
                Desktop.SlideMouse(new Point(frame.Left + 3 + rng.Next(frame.Width - 6), frame.Top + 3 + rng.Next(frame.Height - 6)));
                Thread.Sleep(4);
                Desktop.MouseButton(true);
                Desktop.MouseButton(false);
                Thread.Sleep(30);
            }

            Thread.Sleep(300);
            var log = live.Log;
            Assert.True(log.Count == presses,
                $"{log.Count} of {presses} presses in the first visit; same visit still live: {ReferenceEquals(view.LiveInteraction, live)}; " +
                $"presses over the view's life: {view.PressOutcomes.Count}; " +
                string.Join(", ", view.PressOutcomes.GroupBy(outcome => outcome).Select(group => $"{group.Key}={group.Count()}")));
            int delivered = log.Count(press => press.Outcome == PressOutcome.Delivered);
            int arrived = 0;
            while (source.NextReport(100) is { } report)
                if (report.StartsWith("click ", StringComparison.Ordinal))
                    arrived++;
            Assert.Equal(delivered, arrived);
            // A window too busy to confirm its place keeps a press from reaching it. That's safe, but must stay rare.
            Assert.True(log.Count(press => press.Outcome is PressOutcome.Delivered or PressOutcome.MovedView) >= presses * 98 / 100,
                string.Join(", ", log.GroupBy(press => press.Outcome).Select(group => $"{group.Key}={group.Count()}")));
        });
    }

    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Page = """
        <!doctype html><title>InteractiveProbe n=0</title><style>html,body{margin:0;height:100%;background:#fff}</style><body>
        <script>let n=0;addEventListener('mousedown',e=>{n++;document.title='InteractiveProbe n='+n},true);</script>
        """;

    /// <summary>
    /// The case that lost presses before: fast relative moves, whose acceleration overshoots into the
    /// page's title area. Every press must have an outcome, and the page must see exactly those delivered.
    /// </summary>
    [Fact]
    public void Fast_accelerated_pointer_in_a_browser_misdelivers_nothing()
    {
        if (!File.Exists(ChromePath))
            return;
        string folder = Directory.CreateTempSubdirectory("winscreen-chrome-").FullName;
        System.Diagnostics.Process? browser = null;
        try
        {
            string page = Path.Combine(folder, "page.html");
            File.WriteAllText(page, Page);
            browser = System.Diagnostics.Process.Start(ChromePath,
                $"--user-data-dir=\"{Path.Combine(folder, "profile")}\" --no-first-run --no-default-browser-check --disable-sync --window-size=1200,760 --window-position=100,80 --app=\"file:///{page.Replace('\\', '/')}\"");
            nint chrome = 0;
            Assert.True(Desktop.WaitFor(() =>
            {
                chrome = User32.FindWindow(null, "InteractiveProbe n=0");
                return chrome != 0;
            }, 20000), "The browser didn't open.");
            Thread.Sleep(800);
            Drive(chrome, DrivePresses(chrome), new Point(1000, 500));
        }
        finally
        {
            try { browser?.Kill(entireProcessTree: true); }
            catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            browser?.Dispose();
            Thread.Sleep(300);
            try { Directory.Delete(folder, true); }
            catch (IOException) { } // A browser helper may still hold its profile for a moment.
        }
    }

    private static Action<PipWindow, Func<Action, bool>> DrivePresses(nint chrome) => (view, _) =>
    {
        Rectangle frame = view.Bounds;
        Hover(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2), view);
        var rng = new Random(23);
        int sent = 0;
        for (int i = 0; i < 80; i++)
        {
            var goal = new Point(frame.Left + 8 + rng.Next(frame.Width - 16), frame.Top + 8 + rng.Next(frame.Height - 16));
            Point from = Desktop.CursorPosition();
            double dx = goal.X - from.X, dy = goal.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(4, (int)(length / 1500 * 125));
            for (int step = 0; step < steps; step++)
            {
                Desktop.NudgeMouse((int)(dx / steps), (int)(dy / steps));
                Thread.Sleep(8);
            }

            if (!frame.Contains(Desktop.CursorPosition()) || view.LiveInteraction is null)
            {
                Hover(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2), view);
                continue;
            }

            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            sent++;
            Thread.Sleep(40);
        }

        Assert.True(sent >= 40, $"Only {sent} presses were sent.");
        Thread.Sleep(400);
        PressOutcome[] outcomes = view.PressOutcomes.ToArray();
        Assert.Equal(sent, outcomes.Length);
        int delivered = outcomes.Count(outcome => outcome == PressOutcome.Delivered);
        Assert.Equal($"InteractiveProbe n={delivered}", WindowInfo.Title(chrome));
        Assert.Contains(outcomes, outcome => outcome == PressOutcome.Delivered);
    };
}
