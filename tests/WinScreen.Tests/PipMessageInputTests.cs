using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "Interactive")]
public sealed class PipMessageInputTests
{
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private static void Drive(TargetProcess source, Action<PipWindow, Func<Action, bool>, WindowController> action)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetCrop(new Rectangle(100, 80, 400, 300));
            view.SetInteractive(true);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Assert.True(windows.IsParked(source.Handle));
            Assert.Equal((byte)0, Desktop.Alpha(source.Handle));
            Exception? failure = null;
            Point home = Desktop.CursorPosition();
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
            Assert.InRange(Math.Abs(placed.Left - pointer.X), 0, placed.Width);
            Desktop.ClickOn(new Point(placed.Left + 30, placed.Top + 20), popup);
            Assert.Contains(PressOutcome.Popup, view.PressOutcomes);
            nint tip = source.ShowPopup(new Point(pointer.X + 20, pointer.Y + 20), displayOnly: true);
            Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(tip).Right <= SystemInformation.VirtualScreen.Left));
        });
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

    [Fact]
    public void Compatible_input_supports_a_control_that_checks_the_real_pointer()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.AddButton();
        Drive(source, (view, ui, _) =>
        {
            ui(() => { view.SetCrop(new Rectangle(0, 0, 400, 300)); view.CompatibleInput = true; });
            Rectangle picture = PipGeometry.ViewArea(view.ClientSize);
            Point pointer = new(view.Left + picture.Left + 60 * picture.Width / 400,
                view.Top + picture.Top + 50 * picture.Height / 300);
            Desktop.SlideMouse(pointer);
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
            Thread.Sleep(100);
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Thread.Sleep(100);
            var reports = new List<string>();
            while (source.NextReport(300) is { } report) reports.Add(report);
            Assert.Contains("control-click", reports);
        });
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
}
