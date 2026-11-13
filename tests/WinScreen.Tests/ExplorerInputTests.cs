using System.Diagnostics;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "Interactive")]
public sealed class ExplorerInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private bool _requireStationary;

    [Fact]
    public void Explorer_tab_menus_remain_stationary_during_sustained_navigation()
    {
        _requireStationary = true;
        Double_clicking_a_folder_in_a_view_navigates_explorer();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private static string LastMenu = "";

    /// <summary>
    /// Closes the test's Explorer window with every tab in it. A close message only closes the tab in front, and the
    /// rest would stay open on the desktop.
    /// </summary>
    private static void CloseWindow(nint window)
    {
        if (Type.GetTypeFromProgID("Shell.Application") is { } type && Activator.CreateInstance(type) is { } shell)
        {
            try
            {
                foreach (dynamic tab in ((dynamic)shell).Windows())
                    if ((long)tab.HWND == window.ToInt64()) tab.Quit();
            }
            catch (System.Runtime.InteropServices.COMException) { }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
        if (User32.IsWindow(window)) User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
    }

    /// <summary>A menu item in one of the window's open popup menus, with its place on the screen.</summary>
    private static BrowserAccessibility.Control? MenuItem(nint window, string name)
    {
        uint thread = User32.GetWindowThreadProcessId(window, out _);
        for (nint top = User32.GetWindow(User32.GetDesktopWindow(), 5); top != 0; top = User32.GetWindow(top, 2))
        {
            if (!User32.IsWindowVisible(top) || User32.GetWindowThreadProcessId(top, out _) != thread || top == window) continue;
            var controls = BrowserAccessibility.Read(top);
            if (controls.FirstOrDefault(c => c.Type == 50011 && c.Name == name && c.Bounds.Width > 0) is not { } found) continue;
            // Accessibility can report where the menu was laid out rather than where its window is now: take the
            // item's place within the menu, on the window's real position.
            // The window's own element follows the move; its content keeps the place it was laid out at.
            Rectangle real = Desktop.BoundsOf(top);
            var content = controls.Skip(1).Where(c => c.Bounds.Width > 0).ToList();
            Rectangle laidOut = new(content.Min(c => c.Bounds.Left), content.Min(c => c.Bounds.Top), 0, 0);
            LastMenu = $"window {real}, accessibility {laidOut}, item {found.Bounds}";
            return found with { Bounds = new Rectangle(found.Bounds.Left - laidOut.Left + real.Left, found.Bounds.Top - laidOut.Top + real.Top,
                found.Bounds.Width, found.Bounds.Height) };
        }
        return null;
    }

    /// <summary>
    /// File Explorer's New submenu, used through a view: it opens beside its parent menu, as on the original, not
    /// on top of it; it is what the pointer reaches where the two meet; and its Folder item makes a folder.
    /// </summary>
    [Fact]
    public void A_submenu_opens_beside_its_menu_and_takes_the_click_through_a_view()
    {
        string root = Directory.CreateTempSubdirectory("WinScreenExplorer-").FullName;
        File.WriteAllText(Path.Combine(root, "probe.txt"), "x");
        nint window = 0;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
            Assert.True(Desktop.WaitFor(() =>
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                    using (process)
                        if (process.MainWindowTitle.StartsWith(Path.GetFileName(root), StringComparison.Ordinal))
                            window = process.MainWindowHandle;
                return window != 0;
            }, 15000), "Explorer did not open the probe folder.");
            Thread.Sleep(800);
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
                        // The empty part of the file list, low in the window.
                        Rectangle source = Desktop.BoundsOf(window);
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        Point background = new(source.Left + source.Width * 6 / 10, source.Top + source.Height * 8 / 10);
                        Desktop.SlideMouse(new Point(area.Left + (int)Math.Round((background.X - source.Left - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((background.Y - source.Top - shown.Top) * (double)area.Height / shown.Height)));
                        Thread.Sleep(150);
                        mouse_event(8, 0, 0, 0, 0); Thread.Sleep(50); mouse_event(16, 0, 0, 0, 0);
                        BrowserAccessibility.Control? item = null;
                        Assert.True(Desktop.WaitFor(() => (item = MenuItem(window, "New")) is not null, 4000), "No menu with New. " + string.Join(",", view.PressOutcomes));
                        Thread.Sleep(400);
                        item = MenuItem(window, "New")!;
                        nint parent = Desktop.TopWindowAt(new Point(item.Bounds.Left + 10, item.Bounds.Top + item.Bounds.Height / 2));
                        Rectangle parentBounds = Desktop.BoundsOf(parent);
                        Assert.True(view.Bounds.IntersectsWith(parentBounds), $"The menu {parentBounds} isn't by the view {view.Bounds}.");
                        Desktop.SlideMouse(new Point(item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Top + item.Bounds.Height / 2));
                        BrowserAccessibility.Control? folder = null;
                        Assert.True(Desktop.WaitFor(() => (folder = MenuItem(window, "Folder")) is not null, 4000), "The New submenu did not open.");
                        Thread.Sleep(500);
                        folder = MenuItem(window, "Folder")!;
                        Point onFolder = new(folder.Bounds.Left + folder.Bounds.Width / 2, folder.Bounds.Top + folder.Bounds.Height / 2);
                        nint child = Desktop.TopWindowAt(onFolder);
                        Rectangle childBounds = Desktop.BoundsOf(child);
                        Assert.NotEqual(parent, child);
                        Assert.Equal(parentBounds, Desktop.BoundsOf(parent));
                        // Beside the parent, overlapping it no more than a menu's edge, as Windows lays it out.
                        Rectangle overlap = Rectangle.Intersect(parentBounds, childBounds);
                        Assert.True(overlap.Width <= 16 || overlap.Height <= 16,
                            $"The submenu {childBounds} covers its menu {parentBounds}.");
                        for (int i = 0; i < 20; i++)
                        {
                            Desktop.SlideMouse(onFolder + new Size(i % 5 * 4 - 8, i % 3 * 3 - 3));
                            Thread.Sleep(40);
                            Assert.Equal(child, Desktop.TopWindowAt(onFolder));
                            if (!overlap.IsEmpty)
                                Assert.Equal(child, Desktop.TopWindowAt(new Point(overlap.Left + overlap.Width / 2, overlap.Top + overlap.Height / 2)));
                            Assert.Equal(childBounds, Desktop.BoundsOf(child));
                        }
                        Desktop.SlideMouse(onFolder);
                        Thread.Sleep(150);
                        Desktop.MouseButton(true); Thread.Sleep(50); Desktop.MouseButton(false);
                        Assert.True(Desktop.WaitFor(() => Directory.GetDirectories(root).Length == 1, 4000), "Folder did not make a folder.");
                        Assert.Equal(PressOutcome.Popup, view.PressOutcomes.Last());
                        Thread.Sleep(300);
                        Desktop.Press(Keys.Escape); // Leaves the new folder's name as it is.
                        Thread.Sleep(300);
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
            if (window != 0) CloseWindow(window);
            Directory.Delete(root, true);
        }
    }

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
                if (Environment.GetEnvironmentVariable("WINSCREEN_TEST_ENHANCED_HOVER") == "1") view.SetEnhancedHover(true);
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
                        if (Environment.OSVersion.Version.Build < 22000) { Skipped.Because("The rest needs Windows 11's command bar, tabs and address bar."); return; }
                        void ClickControl(Func<BrowserAccessibility.Control, bool> match, double across = .5, bool right = false, bool last = false, bool first = false)
                        {
                            var control = last ? BrowserAccessibility.Read(window).Last(c => match(c)) : first ? BrowserAccessibility.Read(window).First(c => match(c)) : Assert.Single(BrowserAccessibility.Read(window), c => match(c));
                            Assert.True(control.Enabled, control.ToString());
                            source = Desktop.BoundsOf(window);
                            (area, shown) = view.LiveInteraction!.CurrentGeometry;
                            var point = new Point(area.Left + (int)Math.Round((control.Bounds.Left + control.Bounds.Width * across - source.Left - shown.Left) * (double)area.Width / shown.Width),
                                area.Top + (int)Math.Round((control.Bounds.Top + control.Bounds.Height / 2 - source.Top - shown.Top) * (double)area.Height / shown.Height));
                            Desktop.SlideMouse(point);
                            Thread.Sleep(100);
                            mouse_event(right ? 8u : 2u, 0, 0, 0, 0);
                            Thread.Sleep(50);
                            mouse_event(right ? 16u : 4u, 0, 0, 0, 0);
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

                        // A right-click on a tab opens its own menu, next to the pointer, and the menu takes a real click.
                        int tabs = BrowserAccessibility.Read(window).Count(c => c.Type == 50019);
                        ClickControl(c => c.Type == 50019 && c.Name == folder, right: true);
                        BrowserAccessibility.Control? duplicate = null;
                        Check(() => (duplicate = MenuItem(window, "Duplicate tab")) is not null, "Tab menu " + string.Join(",", view.PressOutcomes));
                        Check(() => view.PressOutcomes.Last() == PressOutcome.Delivered, "Right-click outcome " + string.Join(",", view.PressOutcomes));
                        // The menu opens by the hidden original; the view's popup handling brings it next to the pointer.
                        Point MenuItemAt() => (duplicate = MenuItem(window, "Duplicate tab")) is { } found
                            ? new(found.Bounds.Left + found.Bounds.Width / 2, found.Bounds.Top + found.Bounds.Height / 2) : default;
                        Point menuItem = default;
                        Assert.True(Desktop.WaitFor(() => view.Bounds.IntersectsWith(new Rectangle((menuItem = MenuItemAt()) - new Size(400, 400), new Size(800, 800))), 3000),
                            $"The menu isn't by the pointer: item {menuItem}, pointer {Desktop.CursorPosition()}, view {view.Bounds}; {LastMenu}");
                        // Real pointer navigation must not remap an already relocated popup on each sweep.
                        // Visit the whole menu, pause on each row, then activate a real item.
                        nint popup = 0;
                        Assert.True(Desktop.WaitFor(() =>
                        {
                            for (nint top = User32.GetWindow(User32.GetDesktopWindow(), 5); top != 0; top = User32.GetWindow(top, 2))
                                if (User32.IsWindowVisible(top) && WindowInfo.IsOwnedBy(top, window) &&
                                    BrowserAccessibility.Read(top).Any(c => c.Type == 50011 && c.Name == "Duplicate tab"))
                                { popup = top; return true; }
                            return false;
                        }, 3000));
                        Thread.Sleep(300);
                        Rectangle settled = Desktop.BoundsOf(popup);
                        var positions = new List<Rectangle>();
                        for (int round = 0; round < 2; round++)
                            for (int y = 16; y < settled.Height - 12; y += 24)
                            {
                                Desktop.SlideMouse(new Point(settled.Left + settled.Width / 2, settled.Top + y));
                                for (int pause = 0; pause < 8; pause++)
                                { Thread.Sleep(30); positions.Add(Desktop.BoundsOf(popup)); }
                            }
                        output.WriteLine($"Menu navigation: settled {settled}; observed {string.Join(", ", positions.Distinct())}; live={view.LiveInteraction is not null}");
                        // Keep the desired stationary-menu acceptance explicitly skipped until PIP-501 is resolved.
                        // The active flow still verifies real navigation and Duplicate tab, without claiming no flicker.
                        if (_requireStationary) Assert.True(positions.All(p => p == settled),
                            $"Tab menu moved during navigation: initial {settled}; observed {string.Join(", ", positions.Distinct())}; live={view.LiveInteraction is not null}; ended={view.LastVisitEnd}; {LastMenu}");
                        Assert.True(Desktop.WaitFor(() => MenuItemAt() is var candidate &&
                            view.Bounds.IntersectsWith(new Rectangle(candidate - new Size(400, 400), new Size(800, 800))), 2000));
                        menuItem = MenuItemAt();
                        Desktop.SlideMouse(menuItem);
                        Thread.Sleep(150);
                        Desktop.MouseButton(true); Thread.Sleep(50); Desktop.MouseButton(false);
                        Check(() => BrowserAccessibility.Read(window).Count(c => c.Type == 50019) == tabs + 1, "Duplicate tab");
                        Check(() => view.PressOutcomes.Last() == PressOutcome.Popup, "Menu click outcome " + string.Join(",", view.PressOutcomes));

                        // A double-click on a tab means something of its own, which automation can't do: the second
                        // press is reported as unsupported, and only the first click acts.
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Check(() => view.LiveInteraction is not null, "Visit after the menu");
                        Thread.Sleep(200);
                        ClickControl(c => c.Type == 50019 && c.Name == folder, last: true); // The folder's tab now has a duplicate.
                        mouse_event(2, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(4, 0, 0, 0, 0);
                        Check(() => view.PressOutcomes.Last() == PressOutcome.Unsupported, "Double-click outcome " + string.Join(",", view.PressOutcomes));

                        // A click made just before the pointer leaves the view still happens: Back, in the tab with history.
                        ClickControl(c => c.Type == 50019 && c.Name == folder, first: true);
                        Thread.Sleep(300);
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
            if (window != 0) CloseWindow(window);
            Directory.Delete(root, true);
        }
    }
}
