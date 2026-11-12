using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Several views used directly at once, beyond the two browser pages covered elsewhere: three views, two windows of
/// one program, File Explorer in two views, and middle-click scrolling with a second view beside it.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class MultiViewTests
{
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int x, int y, uint data, nuint extra);
    private const uint MiddleDown = 0x20, MiddleUp = 0x40;

    /// <summary>Opens a view of each window, side by side and used directly, and runs the steps on a worker with the views' thread pumping.</summary>
    private static void Views(nint[] sources, Action<PipWindow[]> steps)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]); // One controller for all, as in the app.
            PipWindow[] views = sources.Select(source => new PipWindow(source, windows) { AllowInteractive = true }).ToArray();
            try
            {
                int x = 40;
                foreach (PipWindow view in views)
                {
                    view.ShowOnTop();
                    view.Location = new Point(x, 260);
                    view.SetInteractive(true);
                    x = view.Right + 16;
                }
                foreach (PipWindow view in views)
                    Assert.True(view.KeepsUpdating, "Not hidden: " + view.StateDescription);
                Exception? failure = null;
                Point home = Desktop.CursorPosition();
                var driver = new Thread(() =>
                {
                    try { steps(views); }
                    catch (Exception exception) { failure = exception; }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
                });
                driver.Start();
                while (driver.IsAlive)
                {
                    Application.DoEvents();
                    Thread.Sleep(2);
                }
                if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            }
            finally
            {
                foreach (PipWindow view in views)
                {
                    if (!view.IsDisposed) view.Close();
                    view.Dispose();
                }
                windows.RestoreAll();
            }
        });
    }

    private static Point Center(PipWindow view) => new(view.Left + view.Width / 2, view.Top + view.Height / 2);

    /// <summary>Moves straight into the view from wherever the pointer is, another view included, and waits for its visit.</summary>
    private static void Enter(PipWindow view)
    {
        Desktop.SlideMouse(Center(view));
        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null, 3000), "No visit started: " + view.StateDescription);
        Thread.Sleep(120);
    }

    private static void Click()
    {
        Desktop.MouseButton(true);
        Thread.Sleep(40);
        Desktop.MouseButton(false);
    }

    /// <summary>Only what starts with <paramref name="prefix"/>; anything else the window says is passed over.</summary>
    private static string? Next(TargetProcess target, string prefix, int milliseconds = 2000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            string? line = target.NextReport((int)Math.Max(1, milliseconds - clock.ElapsedMilliseconds));
            if (line is null) return null;
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
        return null;
    }

    private static void Drain(TargetProcess target)
    {
        while (target.NextReport(50) is not null) { }
    }

    /// <summary>Three views at once: each click and key reaches the window under the pointer and no other.</summary>
    [Fact]
    public void Three_views_at_once_each_get_only_their_own_clicks_and_keys()
    {
        using var a = new TargetProcess(new Rectangle(120, 120, 420, 320), "C81E1E");
        using var b = new TargetProcess(new Rectangle(160, 160, 420, 320), "1E64C8");
        using var c = new TargetProcess(new Rectangle(200, 200, 420, 320), "1EC81E");
        TargetProcess[] targets = [a, b, c];
        Views([a.Handle, b.Handle, c.Handle], views =>
        {
            Keys[] keys = [Keys.A, Keys.B, Keys.C];
            foreach (int i in new[] { 0, 1, 2, 0, 2, 1 })
            {
                foreach (TargetProcess target in targets) Drain(target);
                Enter(views[i]);
                Click();
                Assert.NotNull(Next(targets[i], "click "));
                Desktop.Press(keys[i]);
                string? key = Next(targets[i], "key ");
                Assert.True(key?.StartsWith("key " + char.ToLowerInvariant((char)keys[i]), StringComparison.Ordinal) == true,
                    $"View {i}: '{key}'");
                for (int j = 0; j < targets.Length; j++)
                    if (j != i) Assert.Null(Next(targets[j], "", 200));
            }
        });
    }

    /// <summary>
    /// Two main windows of one program, each in its own view: they share a process and a UI thread, and each still gets
    /// only the clicks and keys meant for it, going from one view to the other and back.
    /// </summary>
    [Fact]
    public void Two_windows_of_one_program_in_two_views_each_get_only_their_own_input()
    {
        using var target = new TargetProcess(new Rectangle(120, 120, 420, 320));
        target.UseFrame();
        nint second = target.CreateSecondWindow(new Rectangle(600, 120, 420, 320));
        Views([target.Handle, second], views =>
        {
            foreach ((int view, string click, string key, Keys press) in new[]
                     {
                         (0, "click ", "key x", Keys.X), (1, "second-click ", "second-key y", Keys.Y), (0, "click ", "key z", Keys.Z),
                     })
            {
                Drain(target);
                Enter(views[view]);
                Click();
                string? first = Next(target, "", 2000);
                Assert.True(first?.StartsWith(click, StringComparison.Ordinal) == true, $"Expected {click.Trim()} from view {view}, got '{first}'.");
                Desktop.Press(press);
                string? typed = Next(target, view == 0 ? "key " : "second-key ", 2000);
                Assert.True(typed?.StartsWith(key, StringComparison.Ordinal) == true, $"Expected '{key}', got '{typed}'.");
                Assert.Null(Next(target, view == 0 ? "second-" : "click ", 200));
            }
        });
    }

    /// <summary>
    /// Middle-click scrolling in one view scrolls only its window, and stops when the pointer goes on into another
    /// view, whose window never scrolls.
    /// </summary>
    [Fact]
    public void Middle_click_scrolling_in_one_view_stops_on_entering_another_and_never_reaches_it()
    {
        using var a = new TargetProcess(new Rectangle(120, 120, 420, 320), "C81E1E");
        using var b = new TargetProcess(new Rectangle(160, 160, 420, 320), "1E64C8");
        Views([a.Handle, b.Handle], views =>
        {
            views[0].Invoke(() => views[0].SetMiddleClickScroll(true));
            Enter(views[0]);
            Drain(a);
            Drain(b);
            mouse_event(MiddleDown, 0, 0, 0, 0);
            Thread.Sleep(40);
            mouse_event(MiddleUp, 0, 0, 0, 0);
            Thread.Sleep(100);
            Point start = Center(views[0]);
            Desktop.SlideMouse(new Point(start.X, Math.Min(views[0].Bottom - 10, start.Y + 80)));
            Assert.NotNull(Next(a, "wheel ", 2000));
            Desktop.SlideMouse(Center(views[1]));
            Assert.True(Desktop.WaitFor(() => views[1].LiveInteraction is not null, 3000), "No visit in the second view.");
            Thread.Sleep(300);
            Drain(a);
            Assert.Null(Next(a, "wheel ", 600));
            Assert.Null(Next(b, "wheel ", 100));
            Assert.Null(Next(b, "click ", 100));
        });
    }

    /// <summary>
    /// Two File Explorer windows, which share one program, each in its own view: a double-click on a folder opens it in
    /// the window under the pointer, and the other stays where it was.
    /// </summary>
    [Fact]
    public void File_explorer_in_two_views_navigates_only_the_window_under_the_pointer()
    {
        string rootA = Directory.CreateTempSubdirectory("WinScreenExplorerA-").FullName;
        string rootB = Directory.CreateTempSubdirectory("WinScreenExplorerB-").FullName;
        string folderA = "ProbeA-" + Guid.NewGuid().ToString("N")[..8], folderB = "ProbeB-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.Combine(rootA, folderA));
        Directory.CreateDirectory(Path.Combine(rootB, folderB));
        nint windowA = 0, windowB = 0;
        try
        {
            windowA = OpenExplorer(rootA);
            windowB = OpenExplorer(rootB);
            Views([windowA, windowB], views =>
            {
                DoubleClickFolder(views[0], windowA, folderA);
                Assert.True(Desktop.WaitFor(() => WindowInfo.Title(windowA).StartsWith(folderA, StringComparison.Ordinal), 4000),
                    "Explorer A didn't open its folder: " + WindowInfo.Title(windowA));
                Assert.StartsWith(Path.GetFileName(rootB), WindowInfo.Title(windowB));
                DoubleClickFolder(views[1], windowB, folderB);
                Assert.True(Desktop.WaitFor(() => WindowInfo.Title(windowB).StartsWith(folderB, StringComparison.Ordinal), 4000),
                    "Explorer B didn't open its folder: " + WindowInfo.Title(windowB));
                Assert.StartsWith(folderA, WindowInfo.Title(windowA));
            });
        }
        finally
        {
            foreach (nint window in new[] { windowA, windowB })
                if (window != 0) Desktop.PostMessage(window, 0x0010, 0, 0); // WM_CLOSE
            Thread.Sleep(500);
            foreach (string root in new[] { rootA, rootB })
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { } // Explorer may still hold it for a moment; a temporary folder, left to Windows.
            }
        }
    }

    private static nint OpenExplorer(string root)
    {
        string name = Path.GetFileName(root);
        Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        nint found = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            foreach (Process process in Process.GetProcessesByName("explorer"))
                using (process)
                {
                    nint window = Desktop.FindProcessWindow(process.Id, title => title.StartsWith(name, StringComparison.Ordinal));
                    if (window != 0) found = window;
                }
            return found != 0;
        }, 15000), "Explorer didn't open " + root);
        Thread.Sleep(800);
        return found;
    }

    private static void DoubleClickFolder(PipWindow view, nint window, string folder)
    {
        Enter(view);
        Thread.Sleep(400);
        var item = Assert.Single(BrowserAccessibility.Read(window), control => control.Type == 50007 && control.Name == folder);
        Rectangle source = Desktop.BoundsOf(window);
        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
        var pointer = new Point(
            area.Left + (int)Math.Round((item.Bounds.Left + item.Bounds.Width / 2 - source.Left - shown.Left) * (double)area.Width / shown.Width),
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
    }
}

/// <summary>A view of a maximized window, and what Keep updating does to Alt+Tab.</summary>
[Trait("Category", "RealWindows")]
public sealed class ViewOfStateTests
{
    private static readonly Rectangle Spot = new(240, 160, 480, 340);

    /// <summary>Go to it, from a view of a maximized window, brings that window forward still maximized and in its place, whether it was shown or hidden.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Go_to_original_brings_a_maximized_window_forward_still_maximized(bool hidden)
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000));
        Rectangle before = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(target.Handle, windows);
            try
            {
                view.ShowOnTop();
                Application.DoEvents();
                if (hidden)
                {
                    view.SetOriginalHidden(true);
                    Assert.True(windows.IsHidden(target.Handle));
                }
                User32.SetForegroundWindow(view.Handle);
                view.GoToSource();
                Assert.True(Desktop.WaitFor(() => !windows.IsHidden(target.Handle) && !WindowInfo.IsConcealed(target.Handle) &&
                    User32.IsZoomed(target.Handle) && Desktop.BoundsOf(target.Handle) == before &&
                    User32.GetForegroundWindow() == target.Handle, 3000, Application.DoEvents),
                    $"hidden {windows.IsHidden(target.Handle)} zoomed {User32.IsZoomed(target.Handle)} bounds {Desktop.BoundsOf(target.Handle)} (was {before}) " +
                    $"front '{WindowInfo.Title(User32.GetForegroundWindow())}'");
            }
            finally { windows.RestoreAll(); }
        });
    }
}

/// <summary>Reads the Alt+Tab switcher, so it presses real keys.</summary>
[Trait("Category", "Interactive")]
public sealed class SwitcherListTests
{
    /// <summary>
    /// Keep updating takes the original's taskbar button away, and it is gone from Alt+Tab too while hidden, and back
    /// once shown. A window that wasn't hidden is listed, which shows the switcher was read. Only on Windows 10, whose
    /// switcher can be read; see the note inside.
    /// </summary>
    [Fact]
    public void Keep_updating_takes_the_original_out_of_alt_tab_until_it_is_shown()
    {
        string title = "Switcher probe " + Guid.NewGuid().ToString("N")[..8];
        using var target = new TargetProcess(new Rectangle(240, 160, 480, 340), title: title);
        target.UseFrame();
        Desktop.GainForegroundRights();
        Desktop.Activate(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(target.Handle, windows);
            try
            {
                view.ShowOnTop();
                view.Location = new Point(1000, 200);
                Application.DoEvents();
                List<string> shown = Names();
                // Windows 11's switcher shows its list only to the eye: through accessibility it has a title and nothing
                // else, so there is nothing to compare there.
                if (shown.Count == 0 && Environment.OSVersion.Version.Build >= 22000) return;
                Assert.Contains(shown, name => name.Contains(title, StringComparison.Ordinal));
                view.SetOriginalHidden(true);
                Assert.True(windows.IsHidden(target.Handle));
                Thread.Sleep(500);
                Assert.DoesNotContain(Names(), name => name.Contains(title, StringComparison.Ordinal));
                view.SetOriginalHidden(false);
                Thread.Sleep(500);
                Assert.Contains(Names(), name => name.Contains(title, StringComparison.Ordinal));
            }
            finally { windows.RestoreAll(); }
        });

        static List<string> Names()
        {
            List<string> names = [];
            var reader = new Thread(() =>
            {
                Desktop.Key(Keys.Menu, true);
                try
                {
                    Desktop.Press(Keys.Tab);
                    nint switcher = 0;
                    Desktop.WaitFor(() =>
                    {
                        foreach (string kind in new[] { "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "TaskSwitcherWnd" })
                        {
                            nint found = Desktop.FindWindow(kind, null);
                            if (found != 0 && Desktop.IsWindowVisible(found)) switcher = found;
                        }
                        return switcher != 0;
                    }, 2000);
                    Thread.Sleep(600);
                    if (switcher != 0)
                        names.AddRange(BrowserAccessibility.Read(switcher).Select(control => control.Name));
                    Desktop.Press(Keys.Escape);
                }
                finally { Desktop.Key(Keys.Menu, false); }
            });
            reader.Start();
            while (reader.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            Thread.Sleep(300);
            return names;
        }
    }
}
