using System.Runtime.InteropServices;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// At 150%, Windows scales apps that aren't per-monitor aware and gives them their own, smaller coordinates. Every
/// kind of press, the wheel and a drag must still reach the point the view shows, in the app's own coordinates.
/// Runs only in the test VM, which is the only place the scale is ever changed.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class ScaledDisplayInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool PhysicalToLogicalPointForPerMonitorDPI(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    [Fact]
    public void Every_kind_of_input_lands_on_the_shown_point_in_scaled_and_unscaled_apps_at_150_percent()
    {
        if (!DisplayScale.Allowed) { Skipped.Because("Changes the display scale, which only the test VM allows."); return; } // Never on someone's own screen.
        int before = DisplayScale.Current;
        var log = new StringBuilder();
        var failures = new List<string>();
        // Unaware and system-aware apps started at 100% are scaled by Windows once the display is at 150%.
        var targets = new[] { "permonitorv2", "unaware", "system" }
            .Select((mode, i) => (Mode: mode, Target: new TargetProcess(new Rectangle(100 + i * 40, 100 + i * 40, 520, 380), dpi: mode)))
            .ToArray();
        try
        {
            foreach (var (_, target) in targets) { target.UseFrame(); target.RecordMessages(); }
            DisplayScale.Set(150);
            foreach (var (mode, target) in targets)
            {
                try { Check(mode, target, log, failures); }
                catch (Exception e) { failures.Add($"[{mode}] {e.Message}"); }
            }
        }
        finally
        {
            try { DisplayScale.Set(before); } catch (InvalidOperationException e) { log.AppendLine("restore: " + e.Message); }
            foreach (var (_, target) in targets) target.Dispose();
            output.WriteLine(log.ToString());
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + log);
    }

    private static void Check(string mode, TargetProcess target, StringBuilder log, List<string> failures)
    {
        nint window = target.Handle;
        log.AppendLine($"[{mode}] window dpi {GetDpiForWindow(window)}, bounds {Desktop.BoundsOf(window)}");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(1150, 250);
            view.SetInteractive(true);
            Exception? failure = null;
            var driver = new Thread(() =>
            {
                try
                {
                    Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                    if (!Desktop.WaitFor(() => view.LiveInteraction is not null)) throw new InvalidOperationException("No visit.");
                    Thread.Sleep(200);
                    var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                    Point at = new(area.Left + area.Width * 2 / 3, area.Top + area.Height * 2 / 3);
                    // Where the app should see it: its own client and screen coordinates for that point.
                    Rectangle source = Desktop.BoundsOf(window);
                    Point spot = PipGeometry.PointToSource(at, area, shown);
                    var physical = new NativePoint { X = source.Left + spot.X, Y = source.Top + spot.Y };
                    var origin = new NativePoint();
                    ClientToScreen(window, ref origin);
                    double factor = GetDpiForWindow(window) / (double)GetDpiForWindow(view.Handle);
                    Point client = new((int)Math.Round((physical.X - origin.X) * factor), (int)Math.Round((physical.Y - origin.Y) * factor));
                    var screen = physical;
                    PhysicalToLogicalPointForPerMonitorDPI(window, ref screen);
                    log.AppendLine($"[{mode}] view point {at} -> physical {physical.X},{physical.Y}; expect client {client}, screen {screen.X},{screen.Y}");
                    while (target.NextReport(100) is not null) { }

                    Desktop.SlideMouse(at); Thread.Sleep(150);
                    void Press(uint down, uint up) { mouse_event(down, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(up, 0, 0, 0, 0); Thread.Sleep(80); }
                    Press(2, 4);                                   // left
                    Thread.Sleep(600);                             // past the double-click time
                    Press(2, 4); Press(2, 4);                      // double
                    Thread.Sleep(600);
                    Press(8, 16);                                  // right
                    mouse_event(0x800, 0, 0, 120, 0); Thread.Sleep(150); // wheel
                    mouse_event(2, 0, 0, 0, 0); Thread.Sleep(60);  // drag 30 physical pixels right
                    for (int i = 1; i <= 3; i++) { Desktop.SlideMouse(at + new Size(i * 10, 0)); Thread.Sleep(60); }
                    mouse_event(4, 0, 0, 0, 0); Thread.Sleep(400);

                    var messages = new List<string[]>();
                    while (target.NextReport(300) is { } report)
                        if (report.StartsWith("mouse ")) messages.Add(report.Split(' '));
                    log.AppendLine($"[{mode}] " + string.Join(" | ", messages.Select(m => string.Join(" ", m.Skip(1)))));
                    void Expect(string message, Point where, string what)
                    {
                        var found = messages.Where(m => m[1] == message).ToList();
                        if (found.Count == 0) { failures.Add($"[{mode}] no {what} ({message})"); return; }
                        var first = found[0];
                        int x = int.Parse(first[2]), y = int.Parse(first[3]);
                        if (Math.Abs(x - where.X) > 2 || Math.Abs(y - where.Y) > 2)
                            failures.Add($"[{mode}] {what} at {x},{y}, expected {where.X},{where.Y}");
                    }
                    Expect("201", client, "left press");
                    Expect("203", client, "double-click");
                    Expect("204", client, "right press");
                    Expect("20A", new Point(screen.X, screen.Y), "wheel (screen coordinates)");
                    // The drag: held moves arrive with the left button flag, 30 physical pixels further on.
                    var held = messages.Where(m => m[1] == "200" && (long.Parse(m[4]) & 1) != 0).ToList();
                    if (held.Count == 0) failures.Add($"[{mode}] no held moves during the drag");
                    else
                    {
                        int moved = int.Parse(held[^1][2]) - client.X;
                        // 30 pixels on the view are fewer or more on the window, by the view's own scale.
                        int expected = (int)Math.Round(30 * shown.Width / (double)area.Width * factor);
                        if (Math.Abs(moved - expected) > 3) failures.Add($"[{mode}] drag moved {moved}, expected {expected}");
                    }

                    // The title bar in the picture moves the view, which needs the app's own hit test to agree.
                    Point caption = new(area.Left + area.Width / 2, area.Top + (int)Math.Round(20 * area.Height / (double)shown.Height));
                    int count = view.PressOutcomes.Count;
                    Desktop.SlideMouse(caption); Thread.Sleep(150);
                    Press(2, 4);
                    if (!Desktop.WaitFor(() => view.PressOutcomes.Count > count, 2000) || view.PressOutcomes.Last() != PressOutcome.MovedView)
                        failures.Add($"[{mode}] title bar press was {string.Join(",", view.PressOutcomes.Skip(count))}");
                }
                catch (Exception e) { failure = e; }
                finally { Desktop.MouseButton(false); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            view.Close();
            if (failure is not null) failures.Add($"[{mode}] {failure.Message}");
        });
    }
}
