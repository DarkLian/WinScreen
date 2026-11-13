using System.Diagnostics;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Store apps draw their content in a window of their own process, inside a frame that belongs to another process,
/// and their content ignores posted mouse messages. Windows owns their taskbar button. These tests use the
/// calculator and check what it actually does. A calculator someone already has open is left alone.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class StoreAppInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Opens a calculator of its own, or returns 0 when there is none or one is already open.</summary>
    internal static nint OpenCalculator()
    {
        // One a previous test closed can take a moment to go.
        if (!Desktop.WaitFor(() => Desktop.FindWindow("ApplicationFrameWindow", "Calculator") == 0, 5000)) return 0;
        try { Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { return 0; }
        nint window = 0;
        Desktop.WaitFor(() => (window = Desktop.FindWindow("ApplicationFrameWindow", "Calculator")) != 0 &&
            BrowserAccessibility.Read(window).Any(c => c.Name == "Seven"), 20000);
        if (window == 0 || !BrowserAccessibility.Read(window).Any(c => c.Name == "Seven")) return 0;
        User32.ShowWindow(window, 9);
        Thread.Sleep(500);
        return window;
    }

    internal static void Close(nint window)
    {
        if (window == 0 || !User32.IsWindow(window)) return;
        User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
        Desktop.WaitFor(() => !User32.IsWindow(window) || !User32.IsWindowVisible(window), 5000);
    }

    internal static string Display(nint window) =>
        BrowserAccessibility.Read(window).FirstOrDefault(c => c.Name.StartsWith("Display is", StringComparison.Ordinal))?.Name ?? "(none)";

    /// <summary>Runs the steps on a worker with the view's thread pumping, as in the app.</summary>
    private static void Visit(nint window, Action<PipWindow, Func<string, Point>> steps, StringBuilder log)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
            var problems = new List<string>();
            view.Problem += problems.Add;
            view.ShowOnTop();
            view.Location = new Point(900, 200);
            view.SetInteractive(true);
            Assert.True(view.KeepsUpdating, "The Store app wasn't hidden: " + string.Join("; ", problems));
            Exception? failure = null;
            Point home = Desktop.CursorPosition();
            PipInteraction.DiagnoseInput = true;
            var driver = new Thread(() =>
            {
                try
                {
                    Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit started: " + view.LastVisitEnd);
                    Thread.Sleep(300);
                    Point At(string name)
                    {
                        var control = Assert.Single(BrowserAccessibility.Read(window), c => c.Name == name && c.Bounds.Width > 0);
                        Rectangle source = Desktop.BoundsOf(window);
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        return new Point(area.Left + (int)Math.Round((control.Bounds.Left + control.Bounds.Width / 2 - source.Left - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((control.Bounds.Top + control.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                    }
                    steps(view, At);
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    Desktop.MouseButton(false);
                    foreach (string entry in view.LiveInteraction?.InputDiagnostics ?? []) log.AppendLine(entry);
                    Desktop.MoveMouse(home);
                    PipInteraction.DiagnoseInput = false;
                }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            log.AppendLine("outcomes " + string.Join(",", view.PressOutcomes) + "; problems " + string.Join("; ", problems));
            if (!view.IsDisposed) view.Close();
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure + "\n" + log);
        });
    }

    private static void Click(Point point, uint down = 2, uint up = 4)
    {
        Desktop.SlideMouse(point);
        Thread.Sleep(120);
        mouse_event(down, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(up, 0, 0, 0, 0);
        Thread.Sleep(350);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    [Fact]
    public void Calculator_adds_through_a_view_takes_keys_and_says_what_it_cannot_do()
    {
        nint window = OpenCalculator();
        if (window == 0) return; // No calculator, or one that's someone else's.
        var log = new StringBuilder();
        Rectangle before = Desktop.BoundsOf(window);
        try
        {
            Visit(window, (view, at) =>
            {
                Bitmap Picture() => Capture(view.RectangleToScreen(view.ClientRectangle));
                using Bitmap start = Picture();
                foreach (string name in new[] { "Clear", "Seven", "Plus", "Eight", "Equals" }) Click(at(name));
                Assert.True(Desktop.WaitFor(() => Display(window) == "Display is 15", 3000), "After 7 + 8 =: " + Display(window));
                // The view shows it: the picture changed while the calculator stayed hidden and parked.
                using Bitmap after = Picture();
                Assert.True(Differs(start, after), "The view's picture didn't change.");
                Assert.True(WindowInfo.IsConcealed(window));

                // The click gave it the keyboard: typed keys reach it.
                Desktop.Press(Keys.Multiply); Desktop.Press(Keys.D2); Desktop.Press(Keys.Enter);
                Assert.True(Desktop.WaitFor(() => Display(window) == "Display is 30", 3000), "After typing * 2 Enter: " + Display(window));

                // A double-click on a button is two clicks, as it is on the calculator itself.
                Click(at("Clear"));
                Point seven = at("Seven");
                Desktop.SlideMouse(seven); Thread.Sleep(120);
                for (int i = 0; i < 2; i++) { mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(60); }
                Assert.True(Desktop.WaitFor(() => Display(window) == "Display is 77", 3000), "After a double-click on 7: " + Display(window));

                // A middle click has no equivalent: reported as unsupported, and nothing happens.
                int count = view.PressOutcomes.Count;
                Click(at("Eight"), 0x20, 0x40);
                Assert.True(Desktop.WaitFor(() => view.PressOutcomes.Count == count + 1), "No outcome for the middle click.");
                Assert.Equal(PressOutcome.Unsupported, view.PressOutcomes.Last());
                Assert.Equal("Display is 77", Display(window));
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
                // The view says so, and offers the original instead.
                Assert.True(Desktop.WaitFor(() => view.ChipShown is PipChipState.OriginalOffered or PipChipState.AlwaysOriginalOffered, 2000),
                    "The view didn't say the middle click didn't get through.");
            }, log);
            // Closing the view puts it back where it was, visible.
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window) == before && !WindowInfo.IsConcealed(window), 3000),
                $"Not restored: {Desktop.BoundsOf(window)} vs {before}");
            Assert.True(Desktop.Alpha(window) is null or 255);
        }
        finally { output.WriteLine(log.ToString()); Close(window); }
    }

    [Fact]
    public void Calculator_title_bar_buttons_in_the_picture_act_on_the_view()
    {
        nint window = OpenCalculator();
        if (window == 0) return;
        var log = new StringBuilder();
        try
        {
            Visit(window, (view, at) =>
            {
                // Minimize in the picture minimizes the view; the calculator stays hidden and running.
                Click(at("Minimize Calculator"));
                Assert.True(Desktop.WaitFor(() => !view.Visible, 3000), "The view didn't minimize. " + string.Join(",", view.PressOutcomes));
                Assert.Equal(PressOutcome.CaptionButton, view.PressOutcomes.Last());
                Assert.False(User32.IsIconic(window));
                Assert.True(WindowInfo.IsConcealed(window));
                view.Invoke(view.ResumeView);
                Assert.True(Desktop.WaitFor(() => view.Visible, 3000));
                // Close in the picture closes the calculator, and the view goes with it.
                Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit after resuming.");
                Thread.Sleep(300);
                Click(at("Close Calculator"));
                Assert.True(Desktop.WaitFor(() => !User32.IsWindow(window) || !User32.IsWindowVisible(window) && !WindowInfo.IsConcealed(window), 6000),
                    "The calculator didn't close. " + string.Join(",", view.PressOutcomes));
                Assert.True(Desktop.WaitFor(() => view.IsDisposed || !view.Visible, 4000), "The view stayed.");
            }, log);
        }
        finally { output.WriteLine(log.ToString()); Close(window); }
    }

    [Fact]
    public void A_hidden_store_app_brought_forward_from_elsewhere_is_shown_and_its_taskbar_button_stays()
    {
        nint window = OpenCalculator();
        if (window == 0) return;
        Rectangle before = Desktop.BoundsOf(window);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]);
                using var view = new PipWindow(window, windows) { AllowInteractive = true };
                view.ShowOnTop();
                view.SetOriginalHidden(true);
                Assert.True(windows.IsHidden(window) && windows.IsParked(window));
                Assert.False(windows.IsOutOfSwitcher(window)); // Windows keeps its button.
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(20); }
                // As its taskbar button or Alt+Tab would.
                Desktop.GainForegroundRights();
                User32.SetForegroundWindow(window);
                Assert.True(Desktop.WaitFor(() => !windows.IsHidden(window), 3000, Application.DoEvents), "It stayed hidden.");
                Assert.NotEqual(0, view.LastReveal);
                Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window) == before, 2000, Application.DoEvents));
                Assert.False(WindowInfo.IsConcealed(window));
            });
        }
        finally { Close(window); }
    }

    [Fact]
    public void A_hidden_store_app_comes_back_after_a_forced_stop()
    {
        nint window = OpenCalculator();
        if (window == 0) return;
        Rectangle before = Desktop.BoundsOf(window);
        string folder = Directory.CreateTempSubdirectory("winscreen-store-recovery").FullName;
        var journal = new ChangeJournal(Path.Combine(folder, "journal.json"));
        var windows = new WindowController([], journal);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(window, windows);
                view.ShowOnTop();
                view.SetOriginalHidden(true);
                Assert.True(windows.IsParked(window));
                windows.SetParkedReachable(window, true); // As during a visit.
                // A new copy reading the journal, as after WinScreen was killed.
                new WindowController([], journal).RecoverFromJournal();
                Assert.Equal(before, Desktop.BoundsOf(window));
                Assert.False(WindowInfo.IsConcealed(window));
                Assert.True(Desktop.Alpha(window) is null or 255);
                Assert.Empty(journal.Load());
            });
        }
        finally { windows.RestoreAll(); Close(window); Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_wheel_scrolls_a_store_app_through_a_view()
    {
        // Settings is a Store app whose pages scroll. One someone already has open is left alone.
        if (!Desktop.WaitFor(() => Desktop.FindWindow("ApplicationFrameWindow", "Settings") == 0, 3000)) return;
        try { Process.Start(new ProcessStartInfo("ms-settings:about") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { return; }
        nint window = 0;
        if (!Desktop.WaitFor(() => (window = Desktop.FindWindow("ApplicationFrameWindow", "Settings")) != 0, 20000)) return;
        var log = new StringBuilder();
        try
        {
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 100, 100, 900, 600, 0x14); // Short enough that its lists scroll.
            Thread.Sleep(2500);
            Visit(window, (view, _) =>
            {
                // Over the left half: its navigation list, or the page, whichever is under the pointer.
                var (area, _) = view.LiveInteraction!.CurrentGeometry;
                Point over = new(area.Left + area.Width / 5, area.Top + area.Height * 2 / 3);
                Desktop.SlideMouse(over); Thread.Sleep(300);
                using Bitmap before = Capture(view.RectangleToScreen(view.ClientRectangle));
                for (int i = 0; i < 3; i++) { mouse_event(0x800, 0, 0, unchecked((uint)-120), 0); Thread.Sleep(120); }
                Thread.Sleep(800);
                using Bitmap after = Capture(view.RectangleToScreen(view.ClientRectangle));
                Assert.True(Differs(before, after), "Nothing scrolled under the wheel.");
            }, log);
        }
        finally { output.WriteLine(log.ToString()); Close(window); }
    }

    private static Bitmap Capture(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return bitmap;
    }

    private static bool Differs(Bitmap a, Bitmap b)
    {
        int changed = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height); y += 2)
            for (int x = 0; x < Math.Min(a.Width, b.Width); x += 2)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) changed++;
        return changed > 20;
    }
}
