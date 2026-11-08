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

    [Theory]
    [InlineData(20, MouseButtons.Left, CaptionAction.Close)]
    [InlineData(8, MouseButtons.Left, CaptionAction.Minimize)]
    [InlineData(9, MouseButtons.Left, CaptionAction.GoToOriginal)]
    [InlineData(3, MouseButtons.Left, CaptionAction.Menu)]
    [InlineData(3, MouseButtons.Right, CaptionAction.Menu)]
    [InlineData(2, MouseButtons.Right, CaptionAction.Menu)]
    [InlineData(2, MouseButtons.Left, null)]   // The title bar moves the view.
    [InlineData(20, MouseButtons.Right, null)] // Only a left click presses a button.
    [InlineData(13, MouseButtons.Left, null)]  // A border.
    [InlineData(1, MouseButtons.Left, null)]   // Content.
    public void The_originals_caption_controls_act_on_the_view(int hit, MouseButtons button, CaptionAction? action) =>
        Assert.Equal(action, PressRules.CaptionActionFor(hit, button));

    [Theory]
    [InlineData(10, true)]
    [InlineData(17, true)]
    [InlineData(18, true)]
    [InlineData(2, false)]
    [InlineData(20, false)]
    public void Borders_and_corners_do_nothing(int hit, bool border) => Assert.Equal(border, PressRules.IsBorder(hit));

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
            PipInteraction live = view.LiveInteraction!;
            string? report = source.NextReport();
            Assert.NotNull(report);
            Assert.True(view.LiveInteraction is not null, "The visit ended: " + view.LastVisitEnd);
            Assert.Equal(PressOutcome.Delivered, live.Log.Single().Outcome);
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
    public void Used_directly_the_title_bar_sits_outside_the_view_so_the_top_of_the_picture_can_be_clicked()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, ui) =>
        {
            Rectangle frame = view.Bounds;
            Hover(new Point(frame.Left + frame.Width / 2, frame.Top + 12), view);
            Rectangle? bar = null;
            Assert.True(Desktop.WaitFor(() => { ui(() => bar = view.TitleBarBounds); return bar is not null; }, 2000),
                "The title bar didn't show.");
            Assert.False(bar!.Value.IntersectsWith(PipGeometry.ViewArea(frame.Size) is var area
                ? new Rectangle(frame.Location + (Size)area.Location, area.Size) : frame), $"Title bar {bar} covers the view {frame}.");
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
                $"ended because {view.LastVisitEnd}; " +
                $"presses over the view's life: {view.PressOutcomes.Count}; " +
                string.Join(", ", view.PressOutcomes.GroupBy(outcome => outcome).Select(group => $"{group.Key}={group.Count()}")));
            int delivered = log.Count(press => press.Outcome == PressOutcome.Delivered);
            int arrived = 0;
            var reports = new List<string>();
            while (source.NextReport(300) is { } report)
                if (report.StartsWith("click ", StringComparison.Ordinal))
                {
                    arrived++;
                    reports.Add(report);
                }
            Assert.True(delivered == arrived, $"{delivered} delivered, {arrived} arrived; " +
                $"pressed at {string.Join(" ", log.Where(press => press.Outcome == PressOutcome.Delivered).Select(press => $"{press.At.X},{press.At.Y}"))}; " +
                $"replays {string.Join("; ", live.ReplayMisses)}; reports {string.Join("; ", reports)}");
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

    /// <summary>The view's picture area on screen.</summary>
    private static Rectangle PictureOf(PipWindow view)
    {
        Rectangle frame = view.Bounds;
        return new Rectangle(frame.Left + PipGeometry.Border, frame.Top + PipGeometry.Border,
            frame.Width - 2 * PipGeometry.Border, frame.Height - 2 * PipGeometry.Border);
    }

    /// <summary>Where a point of the original, relative to its top-left, shows in the view.</summary>
    private static Point InView(PipWindow view, Size original, Point offset)
    {
        Rectangle picture = PictureOf(view);
        return new Point(picture.Left + offset.X * picture.Width / original.Width,
            picture.Top + offset.Y * picture.Height / original.Height);
    }

    /// <summary>The first point along the original's title bar, right to left, that answers with this hit-test code.</summary>
    private static Point FindOnTitleBar(nint window, int code)
    {
        Rectangle r = Desktop.BoundsOf(window);
        for (int y = r.Top + 6; y < r.Top + 30; y += 4)
            for (int x = r.Right - 4; x > r.Left + r.Width / 2; x -= 3)
            {
                nint at = (nint)((y << 16) | (x & 0xFFFF));
                User32.SendMessageTimeout(window, User32.WM_NCHITTEST, 0, at, 0x2, 500, out nint hit);
                if ((int)hit == code)
                    return new Point(x - r.Left + 1, y - r.Top);
            }
        throw new InvalidOperationException($"No hit-test {code} on the title bar.");
    }

    [Fact]
    public void Used_directly_the_original_never_shows_while_hovered_pressed_or_dragged()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            Rectangle picture = PictureOf(view);
            var inside = new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2);
            Hover(inside, view);
            Assert.Equal((byte)0, Desktop.Alpha(source.Handle));
            Assert.False(view.LiveInteraction!.Reachable);
            Slide(new Point(inside.X + 20, inside.Y + 10));
            Assert.Equal((byte)0, Desktop.Alpha(source.Handle)); // Hovering never makes it faint.

            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            string? report = source.NextReport();
            Assert.NotNull(report);
            Assert.StartsWith("click", report);
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(source.Handle) == 0, 500), "It stayed faint after a click.");

            // A drag: the window holds the mouse, so it goes back to invisible while the drag goes on.
            Desktop.MouseButton(true);
            Assert.NotNull(source.NextReport());
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(source.Handle) == 0, 500), "It stayed faint during a drag.");
            for (int step = 1; step <= 4; step++)
                Slide(new Point(inside.X + 20 - step * 10, inside.Y + 10 + step * 6));
            Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(source.Handle) == 0, 500), "It stayed faint after the drag.");
            Assert.All(view.LiveInteraction!.Log, press => Assert.Equal(PressOutcome.Delivered, press.Outcome));
        });
    }

    [Fact]
    public void With_hover_effects_the_original_takes_the_pointer_only_over_its_content()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Size original = Desktop.BoundsOf(source.Handle).Size;
        Drive(source, (view, ui) =>
        {
            ui(() => view.ShowsHoverEffects = true);
            Hover(InView(view, original, new Point(original.Width / 2, original.Height / 2)), view);
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Reachable == true, 1000), "Content didn't take the pointer.");
            Assert.Equal((byte)1, Desktop.Alpha(source.Handle));

            Slide(InView(view, original, new Point(original.Width / 3, 12))); // The original's title bar.
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Reachable == false, 1000), "The title bar took the pointer.");
            Assert.Equal((byte)0, Desktop.Alpha(source.Handle));
        });
    }

    [Fact]
    public void The_originals_minimize_button_in_the_view_minimizes_the_view_not_the_original()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Size original = Desktop.BoundsOf(source.Handle).Size;
        // Tool-window switcher hiding removes native minimize chrome. Custom caption buttons still report HTMINBUTTON.
        source.UseCaptionButton(8);
        Point minimize = new(original.Width / 2, 16);
        Drive(source, (view, _) =>
        {
            Hover(InView(view, original, minimize), view);
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => view.IsSuspended, 2000), "The view didn't minimize.");
            Assert.Equal(PressOutcome.CaptionButton, view.PressOutcomes.Last());
            Assert.False(User32.IsIconic(source.Handle));
            Assert.Null(source.NextReport(200));
        });
    }

    [Fact]
    public void The_originals_close_button_in_the_view_closes_the_original_and_the_view()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Size original = Desktop.BoundsOf(source.Handle).Size;
        Point close = FindOnTitleBar(source.Handle, 20);
        PipWindow? seen = null;
        Drive(source, (view, _) =>
        {
            seen = view;
            Hover(InView(view, original, close), view);
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => source.HasExited, 3000), "The original didn't close.");
            Assert.True(Desktop.WaitFor(() => !view.Visible || view.IsDisposed, 3000), "The view stayed.");
            Assert.Contains(PressOutcome.CaptionButton, view.PressOutcomes);
        });
    }

    [Fact]
    public void A_question_from_the_original_ends_the_visit_and_shows_the_original()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            Rectangle picture = PictureOf(view);
            Hover(new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2), view);
            nint dialog = source.ShowDialog();
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000), "The visit went on.");
            Assert.True(Desktop.WaitFor(() => !WindowInfo.IsConcealed(source.Handle), 2000), "The original stayed hidden.");
            Assert.True(Desktop.BoundsOf(dialog).Right > SystemInformation.VirtualScreen.Left, "The question was moved away.");
        });
    }

    [Fact]
    public void Link_text_and_tips_of_a_hidden_original_never_show_but_its_menus_do()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Drive(source, (view, _) =>
        {
            // At rest, even right where the view is.
            Rectangle picture = PictureOf(view);
            var spot = new Point(picture.Left + 10, picture.Top + 10);
            nint text = source.ShowPopup(spot, displayOnly: true);
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(text).Right <= SystemInformation.VirtualScreen.Left, 2000),
                "Link text showed at rest: " + Desktop.BoundsOf(text));

            // During a visit, a menu by the pointer stays and the link text stays away.
            var inside = new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2);
            Hover(inside, view);
            nint menu = source.ShowPopup(new Point(inside.X + 4, inside.Y + 4));
            Thread.Sleep(400);
            Assert.Equal(new Point(inside.X + 4, inside.Y + 4), Desktop.BoundsOf(menu).Location);
            Assert.True(Desktop.BoundsOf(text).Right <= SystemInformation.VirtualScreen.Left, "Link text came back during a visit.");
        });
    }

    [Fact]
    public void After_a_visit_a_slow_original_stays_where_it_was_put_back()
    {
        using var source = new TargetProcess(SourceSpot);
        source.UseFrame();
        Rectangle home = Desktop.BoundsOf(source.Handle);
        source.BeSlow(25);
        Drive(source, (view, _) =>
        {
            Rectangle picture = PictureOf(view);
            var inside = new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2);
            Hover(inside, view);
            for (int step = 0; step < 30; step++)
                Desktop.MoveMouse(new Point(inside.X + (step % 2 == 0 ? 25 : -25), inside.Y + step % 7));
                Thread.Sleep(10);
            Desktop.MoveMouse(new Point(picture.Left - 60, picture.Top - 60));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is null, 2000), "The visit didn't end.");
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(source.Handle) == home, PipWindow.SettleMilliseconds),
                "Not put back: " + Desktop.BoundsOf(source.Handle));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                Assert.True(Desktop.BoundsOf(source.Handle) == home || clock.ElapsedMilliseconds < PipWindow.SettleMilliseconds,
                    $"Moved away again after {clock.ElapsedMilliseconds} ms: {Desktop.BoundsOf(source.Handle)}");
                Thread.Sleep(20);
            }
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
        });
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
