using System.Diagnostics;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "Interactive")]
public sealed class ExplorerInputTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    [Fact]
    public void Double_clicking_a_folder_in_a_view_navigates_explorer()
    {
        string root = Directory.CreateTempSubdirectory("WinScreenExplorer-").FullName;
        string folder = "ClickProbe-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, folder));
        nint window = 0;
        PipInteraction.DiagnoseInput = true;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
            Assert.True(Desktop.WaitFor(() =>
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    using (process)
                        if (process.MainWindowTitle.StartsWith(Path.GetFileName(root), StringComparison.Ordinal))
                            window = process.MainWindowHandle;
                }
                return window != 0;
            }, 15000), "Explorer did not open the probe folder.");
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
                view.ShowOnTop();
                view.Location = new Point(900, 220);
                view.SetInteractive(true);
                Exception? failure = null;
                Point home = Desktop.CursorPosition();
                var driver = new Thread(() =>
                {
                    try
                    {
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
                        Thread.Sleep(500);
                        var item = Assert.Single(BrowserAccessibility.Read(window), c => c.Type == 50007 && c.Name == folder);
                        Rectangle source = Desktop.BoundsOf(window);
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        Point pointer = new(area.Left + (int)Math.Round((item.Bounds.Left + item.Bounds.Width / 2 - source.Left - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((item.Bounds.Top + item.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                        Desktop.SlideMouse(pointer);
                        Thread.Sleep(100);
                        for (int i = 0; i < 2; i++)
                        {
                            Desktop.MouseButton(true);
                            Thread.Sleep(50);
                            Desktop.MouseButton(false);
                            Thread.Sleep(60);
                        }
                        Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith(folder, StringComparison.Ordinal), 4000),
                            "Explorer did not navigate. " + string.Join("\n", view.LiveInteraction?.InputDiagnostics ?? []));
                        // The rest is Windows 11's Explorer: its command bar, tabs and address bar.
                        if (Environment.OSVersion.Version.Build < 22000) return;
                        void ClickControl(Func<BrowserAccessibility.Control, bool> match, double across = .5)
                        {
                            var control = Assert.Single(BrowserAccessibility.Read(window), c => match(c));
                            Assert.True(control.Enabled, control.ToString());
                            source = Desktop.BoundsOf(window);
                            (area, shown) = view.LiveInteraction!.CurrentGeometry;
                            var point = new Point(area.Left + (int)Math.Round((control.Bounds.Left + control.Bounds.Width * across - source.Left - shown.Left) * (double)area.Width / shown.Width),
                                area.Top + (int)Math.Round((control.Bounds.Top + control.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                            Desktop.SlideMouse(point);
                            Thread.Sleep(100);
                            Desktop.MouseButton(true);
                            Thread.Sleep(50);
                            Desktop.MouseButton(false);
                        }
                        void Check(Func<bool> effect, string name) => Assert.True(Desktop.WaitFor(effect, 4000),
                            name + " failed. " + string.Join("\n", view.LiveInteraction?.InputDiagnostics ?? []));
                        ClickControl(c => c.Type == 50000 && c.Name == "Back");
                        Check(() => WindowInfo.Title(window).StartsWith(Path.GetFileName(root), StringComparison.Ordinal), "Back");
                        ClickControl(c => c.Type == 50000 && c.Name == "Forward");
                        Check(() => WindowInfo.Title(window).StartsWith(folder, StringComparison.Ordinal), "Forward");
                        ClickControl(c => c.Type == 50004 && c.Name.Contains("Search"));
                        Check(() => BrowserAccessibility.Read(window).Any(c => c.Type == 50004 && c.Name.Contains("Search") && c.Focused), "Search focus");
                        ClickControl(c => c.Type == 50004 && c.Name == "Address Bar", .95);
                        Check(() => BrowserAccessibility.Read(window).Any(c => c.Type == 50004 && !c.Name.Contains("Search") && c.Focused), "Address focus");
                        Desktop.Press(Keys.Escape);
                        ClickControl(c => c.Type == 50000 && c.Name == "Add New Tab");
                        Check(() => BrowserAccessibility.Read(window).Count(c => c.Type == 50019) == 2, "Add tab");
                        ClickControl(c => c.Type == 50019 && c.Name == folder);
                        Check(() => WindowInfo.Title(window).StartsWith(folder, StringComparison.Ordinal), "Select tab");
                        ClickControl(c => c.Type == 50000 && c.Name == "View");
                        Check(() => BrowserAccessibility.Read(window).Any(c => c.Name == "Extra large icons"), "View menu");
                        Desktop.Press(Keys.Escape);
                        Thread.Sleep(300);
                        // Every click above reported what really happened, once the control acted.
                        Check(() => view.PressOutcomes.Count(o => o == PressOutcome.Unconfirmed) == 0,
                            "Outcomes " + string.Join(",", view.PressOutcomes));

                        // A right-click has no equivalent on these controls: it's reported, not counted as delivered.
                        int before = view.PressOutcomes.Count;
                        var viewButton = Assert.Single(BrowserAccessibility.Read(window), c => c.Type == 50000 && c.Name == "View");
                        source = Desktop.BoundsOf(window);
                        (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        Point right = new(area.Left + (int)Math.Round((viewButton.Bounds.Left + viewButton.Bounds.Width / 2 - source.Left - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((viewButton.Bounds.Top + viewButton.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                        Desktop.SlideMouse(right);
                        Thread.Sleep(100);
                        mouse_event(8, 0, 0, 0, 0); Thread.Sleep(50); mouse_event(16, 0, 0, 0, 0);
                        // Explorer's own tooltip can be under the pointer by then, which takes the press as a real popup.
                        Check(() => view.PressOutcomes.Count == before + 1 && view.PressOutcomes.Last() is PressOutcome.Unconfirmed or PressOutcome.Popup,
                            "Right-click outcome " + string.Join(",", view.PressOutcomes));

                        // A click made just before the pointer leaves the view still happens.
                        ClickControl(c => c.Type == 50000 && c.Name == "Back");
                        Desktop.SlideMouse(new Point(view.Left - 60, view.Top - 60));
                        Check(() => WindowInfo.Title(window).StartsWith(Path.GetFileName(root), StringComparison.Ordinal), "Back, then leaving at once");
                        Check(() => view.PressOutcomes.Last() == PressOutcome.Delivered, "Late outcome " + string.Join(",", view.PressOutcomes));
                    }
                    catch (Exception e) { failure = e; }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            });
        }
        finally
        {
            PipInteraction.DiagnoseInput = false;
            if (window != 0) User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
            Directory.Delete(root, true);
        }
    }
}
