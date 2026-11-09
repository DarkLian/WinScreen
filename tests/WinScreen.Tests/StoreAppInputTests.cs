using System.Diagnostics;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Store apps draw their content in a window of their own process, inside a frame window that belongs to another
/// process. A view of the frame must still reach the content.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class StoreAppInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Calculator_buttons_take_clicks_through_a_view()
    {
        // A calculator someone already has open is theirs: leave it alone.
        if (Desktop.FindWindow("ApplicationFrameWindow", "Calculator") != 0) return;
        nint window = 0;
        try { Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { return; } // No calculator on this system.
        bool started = true;
        Desktop.WaitFor(() => (window = Desktop.FindWindow("ApplicationFrameWindow", "Calculator")) != 0 &&
            BrowserAccessibility.Read(window).Any(c => c.Name == "Seven"), 20000);
        if (window == 0 || !BrowserAccessibility.Read(window).Any(c => c.Name == "Seven")) return; // Not installed, or another mode.
        var log = new StringBuilder();
        try
        {
            User32.ShowWindow(window, 9);
            Thread.Sleep(500);
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
                var problems = new List<string>();
                view.Problem += problems.Add;
                view.Notice += problems.Add;
                view.ShowOnTop();
                view.Location = new Point(900, 200);
                view.SetInteractive(true);
                if (!view.KeepsUpdating)
                {
                    // Today a Store app can't be hidden, so it can't be used directly. That must be said, never silent,
                    // and the app must be left as it was.
                    output.WriteLine("Direct use refused: " + string.Join("; ", problems));
                    Assert.NotEmpty(problems);
                    Assert.False(WindowInfo.IsConcealed(window));
                    Assert.Null(view.LiveInteraction);
                    view.Close();
                    return;
                }
                Exception? failure = null;
                Point home = Desktop.CursorPosition();
                PipInteraction.DiagnoseInput = true;
                var driver = new Thread(() =>
                {
                    try
                    {
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), $"No visit started: keepsUpdating={view.KeepsUpdating}; {string.Join("; ", problems)}; {view.StateDescription}");
                        Thread.Sleep(300);
                        void Click(string name)
                        {
                            var control = Assert.Single(BrowserAccessibility.Read(window), c => c.Type == 50000 && c.Name == name);
                            Rectangle source = Desktop.BoundsOf(window);
                            var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                            var point = new Point(area.Left + (int)Math.Round((control.Bounds.Left + control.Bounds.Width / 2 - source.Left - shown.Left) * (double)area.Width / shown.Width),
                                area.Top + (int)Math.Round((control.Bounds.Top + control.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                            Desktop.SlideMouse(point);
                            Thread.Sleep(100);
                            Desktop.MouseButton(true);
                            Thread.Sleep(50);
                            Desktop.MouseButton(false);
                            Thread.Sleep(300);
                        }
                        Click("Clear");
                        foreach (string name in new[] { "Seven", "Plus", "Eight", "Equals" }) Click(name);
                        string display = BrowserAccessibility.Read(window).FirstOrDefault(c => c.Name.StartsWith("Display is", StringComparison.Ordinal))?.Name ?? "(none)";
                        log.AppendLine("display: " + display + "; outcomes " + string.Join(",", view.PressOutcomes));
                        foreach (string entry in view.LiveInteraction?.InputDiagnostics ?? []) log.AppendLine(entry);
                        Assert.True(Desktop.WaitFor(() => BrowserAccessibility.Read(window).Any(c => c.Name == "Display is 15"), 3000), log.ToString());
                        Assert.All(view.PressOutcomes, o => Assert.Equal(PressOutcome.Delivered, o));
                    }
                    catch (Exception e) { failure = e; }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); PipInteraction.DiagnoseInput = false; }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            });
        }
        finally
        {
            output.WriteLine(log.ToString());
            if (started && window != 0) User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
        }
    }
}
