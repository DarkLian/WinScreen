using System.Diagnostics;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>File Explorer's own hover highlight, seen in the view's picture rather than asked of the app.</summary>
[Trait("Category", "Interactive")]
public sealed class ExplorerHoverTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [NativeHoverFact(true)]
    public void A_hovered_row_stays_highlighted_in_the_view_only_with_enhanced_hover()
    {
        string root = Directory.CreateTempSubdirectory("WinScreenHoverExplorer-").FullName;
        foreach (string name in new[] { "Alpha", "Bravo", "Charlie" }) Directory.CreateDirectory(Path.Combine(root, name));
        nint window = 0;
        var log = new StringBuilder();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
            Assert.True(Desktop.WaitFor(() =>
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                    using (process)
                        if (process.MainWindowTitle.StartsWith(Path.GetFileName(root), StringComparison.Ordinal)) window = process.MainWindowHandle;
                return window != 0;
            }, 15000), "Explorer did not open the probe folder.");
            Thread.Sleep(1500);
            Assert.Equal((PipHoverApp.FileExplorer, true), PipEnhancedHover.Describe(window));
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
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height * 3 / 4));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
                        Thread.Sleep(500);
                        Point Map(Point screen)
                        {
                            Rectangle source = Desktop.BoundsOf(window);
                            var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                            return new Point(area.Left + (int)Math.Round((screen.X - source.Left - shown.Left) * (double)area.Width / shown.Width),
                                area.Top + (int)Math.Round((screen.Y - source.Top - shown.Top) * (double)area.Height / shown.Height));
                        }
                        Color Shown(Point at)
                        {
                            using var pixel = new Bitmap(1, 1);
                            using (Graphics g = Graphics.FromImage(pixel)) g.CopyFromScreen(at.X, at.Y, 0, 0, new Size(1, 1));
                            return pixel.GetPixel(0, 0);
                        }
                        foreach (bool enhanced in new[] { false, true, false, true })
                        {
                            view.Invoke(() => view.SetEnhancedHover(enhanced));
                            Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height * 3 / 4));
                            Thread.Sleep(600);
                            foreach (string name in new[] { "Alpha", "Charlie" })
                            {
                                var item = BrowserAccessibility.Read(window).First(c => c.Type == 50007 && c.Name == name);
                                Point pointer = Map(new Point(item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Top + item.Bounds.Height / 2));
                                Point sample = Map(new Point(item.Bounds.Left + 3, item.Bounds.Top + 3));
                                Color rest = Shown(sample);
                                Desktop.SlideMouse(pointer);
                                // Looked at for four seconds without moving: a highlight that only flashes doesn't count.
                                foreach (int wait in new[] { 150, 350, 500, 1000, 2000 })
                                {
                                    Thread.Sleep(wait);
                                    Color now = Shown(sample);
                                    int change = Math.Abs(now.R - rest.R) + Math.Abs(now.G - rest.G) + Math.Abs(now.B - rest.B);
                                    string seen = $"enhanced={enhanced} {name} rest={rest.R},{rest.G},{rest.B} now={now.R},{now.G},{now.B} :: {view.LiveInteraction?.HoverStatus}";
                                    log.AppendLine(seen);
                                    if (enhanced) Assert.True(change > 60, "The hovered row is not highlighted in the view. " + seen);
                                    else Assert.True(change < 15, "Without the option the row was expected to stay as it was. " + seen);
                                }
                                if (enhanced) Assert.StartsWith("active", view.LiveInteraction!.HoverStatus);
                            }
                        }
                        Desktop.SlideMouse(new Point(view.Left - 60, view.Top + 100));
                        Thread.Sleep(800);
                    }
                    catch (Exception e) { failure = e; }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
                Assert.Equal(0, User32.GetProp(window, WindowController.EnhancedHoverOwnerProperty));
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            });
        }
        finally
        {
            output.WriteLine(log.ToString());
            if (window != 0)
            {
                if (Type.GetTypeFromProgID("Shell.Application") is { } type && Activator.CreateInstance(type) is { } shell)
                {
                    try { foreach (dynamic tab in ((dynamic)shell).Windows()) if ((long)tab.HWND == window.ToInt64()) tab.Quit(); }
                    catch (System.Runtime.InteropServices.COMException) { }
                    finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
                }
                if (User32.IsWindow(window)) User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
            }
            Thread.Sleep(500);
            try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
