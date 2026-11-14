using System.Diagnostics;
using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Glass and picture-keeping Dark make the original invisible and draw it again from a window that lets clicks
/// through, so every press has to reach the invisible original: its page, its title bar and its buttons. A window
/// of the tests' own sits behind, so a press that falls through lands there and says so, and nothing else is touched.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class ConcealedPressTests
{
    private static readonly Rectangle Spot = new(300, 220, 480, 320);
    private const int HTCAPTION = 2, HTCLOSE = 20, WM_NCHITTEST = 0x0084;

    /// <summary>Where the window itself says its title bar or close button is, scanning its top row from the right.</summary>
    private static Point Find(nint window, int hit)
    {
        Rectangle bounds = Desktop.BoundsOf(window);
        int Hit(int x, int y)
        {
            User32.SendMessageTimeout(window, WM_NCHITTEST, 0, (nint)((y << 16) | (x & 0xFFFF)), 2, 500, out nint answer);
            return (int)answer;
        }
        for (int y = bounds.Top + 10; y < bounds.Top + 60; y += 2)
            for (int x = bounds.Right - 6; x > bounds.Left + 6; x -= 4)
                if (Hit(x, y) == hit)
                {
                    if (hit != HTCAPTION) return new Point(x - 6, y);
                    // Well inside the title bar, a third of the way across and clear of the buttons; Windows can
                    // answer "title bar" right up to them.
                    for (int y2 = y; y2 < y + 30; y2 += 2)
                        for (int inside = bounds.Left + bounds.Width / 3; inside < bounds.Right - 200; inside += 10)
                            if (Hit(inside, y2) == HTCAPTION && Hit(inside, y2 + 6) == HTCAPTION && Hit(inside + 30, y2) == HTCAPTION)
                                return new Point(inside + 15, y2 + 3);
                    throw new InvalidOperationException("No room in the title bar to drag it by.");
                }
        throw new InvalidOperationException($"The window answered {hit} nowhere in its top row.");
    }

    /// <summary>Refuses to press anywhere but on the tests' own two windows.</summary>
    private static void RequireOurs(Point point, TargetProcess target, TargetProcess behind)
    {
        nint top = User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);
        if (top != target.Handle && top != behind.Handle && !OwnedByUs(top))
            throw new InvalidOperationException($"Refusing to press: {WindowInfo.ClassName(top)} is at {point}.");
    }

    private static bool OwnedByUs(nint window) =>
        User32.GetWindowThreadProcessId(window, out uint process) != 0 && process == Environment.ProcessId;

    private static string Under(Point point)
    {
        nint top = User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);
        return $"{WindowInfo.ClassName(top)} 0x{top:X} ex=0x{Desktop.ExStyle(top):X} alpha={Desktop.Alpha(top)}";
    }

    private static void Drain(TargetProcess process)
    {
        while (process.NextReport(100) is not null) { }
    }

    /// <summary>Content, title bar and close button, in that order, each judged by what the window did.</summary>
    private static void PressEveryPart(TargetProcess target, TargetProcess behind, Action pump)
    {
        Point page = new(target.Bounds.Left + target.Bounds.Width / 2, target.Bounds.Top + target.Bounds.Height * 2 / 3);
        RequireOurs(page, target, behind);
        string under = Under(page);
        Drain(target);
        Drain(behind);
        Desktop.MoveMouse(page);
        Thread.Sleep(50);
        Desktop.MouseButton(true);
        Desktop.MouseButton(false);
        string? report = null;
        Desktop.WaitFor(() => (report = target.NextReport(50)) is not null, 2000, pump);
        Assert.True(report?.StartsWith("click", StringComparison.Ordinal) == true,
            $"A press on the page didn't reach the window (under the pointer: {under}; behind said {behind.NextReport(200) ?? "nothing"}).");

        DragAndClose(target.Handle, point => RequireOurs(point, target, behind), pump);
    }

    /// <summary>Drags the title bar, then presses the close button, each judged by what the window did.</summary>
    private static void DragAndClose(nint window, Action<Point> require, Action pump, bool drawsItsOwnTitleBar = false)
    {
        Rectangle before = Desktop.BoundsOf(window);
        // A window that draws its own title bar answers "client" everywhere; its empty tab strip and its last
        // corner are where a person drags and closes it.
        double scale = User32.GetDpiForWindow(window) / 96.0;
        Point title = drawsItsOwnTitleBar ? new Point(before.Right - (int)(260 * scale), before.Top + (int)(16 * scale)) : Find(window, HTCAPTION);
        require(title);
        string under = Under(title);
        Desktop.MoveMouse(title);
        Thread.Sleep(50);
        Desktop.MouseButton(true);
        for (int i = 1; i <= 8; i++)
        {
            Desktop.MoveMouse(new Point(title.X + 60 * i / 8, title.Y + 40 * i / 8));
            Thread.Sleep(20);
            pump();
        }
        Thread.Sleep(150);
        Desktop.MouseButton(false);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window).Location != before.Location, 2000, pump),
            $"Dragging the title bar at {title} of {before} didn't move the window (under the pointer: {under}; now {Desktop.BoundsOf(window)}, cursor {Desktop.CursorPosition()}).");

        Rectangle moved = Desktop.BoundsOf(window);
        Point close = drawsItsOwnTitleBar ? new Point(moved.Right - (int)(22 * scale), moved.Top + (int)(16 * scale)) : Find(window, HTCLOSE);
        require(close);
        under = Under(close);
        Desktop.MoveMouse(close);
        Thread.Sleep(100);
        Desktop.MouseButton(true);
        Desktop.MouseButton(false);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(window), 5000, pump),
            $"The close button didn't close the window (under the pointer: {under}).");
    }

    /// <summary>The core, with no graphics card needed: the original made invisible the way both effects do it.</summary>
    [Fact]
    public void Every_part_of_a_window_concealed_for_an_effect_still_takes_a_press()
    {
        using var behind = new TargetProcess(new Rectangle(Spot.Left - 60, Spot.Top - 60, Spot.Width + 220, Spot.Height + 160), "1E64C8", "WinScreen behind");
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            try
            {
                windows.ConcealSolid(target.Handle);
                Assert.True(WindowInfo.IsConcealed(target.Handle));
                PressEveryPart(target, behind, () => Application.DoEvents());
            }
            finally { windows.RestoreAll(); }
        });
    }

    [GraphicsCardTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_part_of_a_window_under_a_picture_keeping_effect_still_takes_a_press(bool dark)
    {
        using var behind = new TargetProcess(new Rectangle(Spot.Left - 60, Spot.Top - 60, Spot.Width + 220, Spot.Height + 160), "1E64C8", "WinScreen behind");
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, dark ? ContentLook.Dark : ContentLook.Glass));
                long end = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                PressEveryPart(target, behind, () => Application.DoEvents());
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    /// <summary>
    /// The effect is put straight above its window, and the window stays where it was among the others, even when
    /// WinScreen has a window of its own further down (Settings left open, say): Windows tends to place a new window
    /// next to its program's others, and the window used to be moved down under it (the owner, 15 Nov: Chrome "jumped
    /// to the bottom" under Glass, Notepad++ "disappeared" under Dark).
    /// </summary>
    [GraphicsCardTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_effect_keeps_its_window_where_it_was_among_the_others(bool dark)
    {
        using var below = new TargetProcess(new Rectangle(Spot.Left + 200, Spot.Top + 100, 500, 400), "1E64C8", "Window below");
        using var target = new TargetProcess(Spot, "F0F0F0", "Window above");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var own = new Form { Text = "WinScreen's own window, low down", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(20, 20, 300, 200), ShowInTaskbar = false };
            own.Show();
            Desktop.GainForegroundRights();
            Desktop.Activate(below.Handle);
            Desktop.Activate(target.Handle);
            User32.SetWindowPos(own.Handle, User32.HWND_BOTTOM, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            var overlap = new Point(Spot.Left + 260, Spot.Top + 160);
            Assert.Equal(target.Handle, User32.GetAncestor(Desktop.TopWindowAt(overlap), User32.GA_ROOT));
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, dark ? ContentLook.Dark : ContentLook.Glass));
                long end = Environment.TickCount64 + 800;
                while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                nint top = User32.GetAncestor(Desktop.TopWindowAt(overlap), User32.GA_ROOT);
                Assert.True(top == target.Handle, $"The window went down the stack: \"{WindowInfo.Title(top)}\" is over it now.");
                Assert.True(Above(target.Handle, below.Handle), "The window is under the one it was above.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    /// <summary>Whether <paramref name="upper"/> comes before <paramref name="lower"/> in the order of windows.</summary>
    private static bool Above(nint upper, nint lower)
    {
        for (nint w = User32.GetWindow(lower, 3); w != 0; w = User32.GetWindow(w, 3)) // GW_HWNDPREV
            if (w == upper) return true;
        return false;
    }

    /// <summary>
    /// The two looks that need no graphics card: plain Glass (one colour of the window made see-through with a colour
    /// key) and the filter kind of Dark (Windows' magnifier over the window).
    /// </summary>
    [Theory]
    [InlineData("plainGlass")]
    [InlineData("filterDark")]
    public void Every_part_of_a_window_under_a_look_without_a_graphics_card_still_takes_a_press(string look)
    {
        using var behind = new TargetProcess(new Rectangle(Spot.Left - 60, Spot.Top - 60, Spot.Width + 220, Spot.Height + 160), "1E64C8", "WinScreen behind");
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var dark = new WinScreen.Dark.DarkMode();
            try
            {
                Assert.True(look == "plainGlass" ? windows.ToggleGlass(target.Handle) : dark.Toggle(target.Handle));
                long end = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                if (look == "plainGlass")
                    // Windows passes presses through a colour key's see-through colour by design: the page is a choice
                    // for the owner (see the roadmap, F3). Its frame stays usable.
                    DragAndClose(target.Handle, point => RequireOurs(point, target, behind), () => Application.DoEvents());
                else
                    PressEveryPart(target, behind, () => Application.DoEvents());
            }
            finally { windows.RestoreAll(); }
        });
    }

    /// <summary>
    /// The same on real programs the owner saw it on: a browser's page and its own title bar, and a File Explorer
    /// window, each under picture-keeping Dark. Only windows this test opens are pressed.
    /// </summary>
    [GraphicsCardTheory]
    [InlineData("chrome")]
    [InlineData("explorer")]
    public void A_real_program_under_picture_keeping_Dark_still_takes_presses(string program)
    {
        Process? browser = null;
        string root = Directory.CreateTempSubdirectory("WinScreenConcealedPress-").FullName;
        nint window = 0;
        try
        {
            if (program == "chrome")
            {
                string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
                if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
                Directory.Delete(root, true);
                (browser, window, root) = BrowserInputTests.Open(
                    "<!doctype html><title>PressProbe</title><body style='background:#fff;height:100vh;margin:0' onmousedown=\"document.title='PressProbe pressed'\">",
                    "PressProbe", app: false, chrome);
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true })?.Dispose();
                Assert.True(Desktop.WaitFor(() =>
                {
                    foreach (var process in Process.GetProcessesByName("explorer"))
                        using (process)
                            if (process.MainWindowTitle.StartsWith(Path.GetFileName(root), StringComparison.Ordinal)) window = process.MainWindowHandle;
                    return window != 0;
                }, 15000), "File Explorer didn't open the folder.");
                Thread.Sleep(1500);
            }
            Assert.NotEqual(0, window);
            User32.SetWindowPos(window, 0, 200, 120, 900, 640, User32.SWP_NOACTIVATE);
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                Desktop.GainForegroundRights();
                Desktop.Activate(window);
                var windows = new WindowController([]);
                using var effects = new ContentEffects(windows);
                try
                {
                    Assert.True(effects.Toggle(window, ContentLook.Dark));
                    long end = Environment.TickCount64 + 800;
                    while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                    void Require(Point point)
                    {
                        if (User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT) != window)
                            throw new InvalidOperationException($"Refusing to press: {Under(point)} is at {point}.");
                    }
                    if (program == "chrome")
                    {
                        Rectangle bounds = Desktop.BoundsOf(window);
                        var page = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height * 2 / 3);
                        Require(page);
                        string under = Under(page);
                        Desktop.MoveMouse(page);
                        Thread.Sleep(50);
                        Desktop.MouseButton(true);
                        Desktop.MouseButton(false);
                        Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("PressProbe pressed", StringComparison.Ordinal), 3000, Application.DoEvents),
                            $"A press on the page didn't reach the browser (under the pointer: {under}).");
                    }
                    DragAndClose(window, Require, Application.DoEvents, drawsItsOwnTitleBar: program == "explorer");
                }
                finally
                {
                    effects.TurnAllOff();
                    windows.RestoreAll();
                }
            });
        }
        finally
        {
            if (browser is not null) BrowserInputTests.Close(browser, root);
            else
            {
                if (window != 0 && User32.IsWindow(window)) User32.PostMessage(window, 0x0010, 0, 0);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
