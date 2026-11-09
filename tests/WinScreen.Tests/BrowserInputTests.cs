using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>A Chromium browser used through a view: pointer shapes, clicks, selecting, typing and its tab strip.</summary>
[Trait("Category", "Interactive")]
public sealed class BrowserInputTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public int Flags; public nint Cursor; public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private static string Shape(nint cursor) =>
        cursor == LoadCursor(0, 32512) ? "arrow" : cursor == LoadCursor(0, 32513) ? "ibeam" :
        cursor == LoadCursor(0, 32649) ? "hand" : cursor == 0 ? "none" : $"0x{cursor:X}";

    private static (Process Browser, nint Window, string Root) Open(string html, string title, bool app, string? path = null, double scale = 1)
    {
        string root = Directory.CreateTempSubdirectory("winscreen-browser").FullName;
        string page = Path.Combine(root, "page.html");
        File.WriteAllText(page, html);
        var browser = new Process { StartInfo = new ProcessStartInfo(path ?? EdgePath) { UseShellExecute = false } };
        foreach (string arg in new[] { "--user-data-dir=" + Path.Combine(root, "profile"), "--no-first-run", "--no-default-browser-check",
            "--force-device-scale-factor=" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "--force-renderer-accessibility", "--disable-sync", "--window-size=900,700", "--window-position=160,100", app ? "--app=" + new Uri(page).AbsoluteUri : new Uri(page).AbsoluteUri })
            browser.StartInfo.ArgumentList.Add(arg);
        browser.Start();
        nint window = 0;
        Desktop.WaitFor(() =>
        {
            // Only this run's browser: its profile folder is in its command line. Another run's window can have
            // the same title.
            window = 0;
            foreach (int id in ProcessesUsing(root))
            {
                try
                {
                    using Process p = Process.GetProcessById(id);
                    if (p.MainWindowHandle != 0 && WindowInfo.Title(p.MainWindowHandle).StartsWith(title)) window = p.MainWindowHandle;
                }
                catch (ArgumentException) { }
            }
            return window != 0;
        }, 20000);
        Thread.Sleep(1500);
        return (browser, window, root);
    }

    private static void Close(Process browser, string root)
    {
        try { if (!browser.HasExited) browser.Kill(true); } catch (InvalidOperationException) { }
        browser.WaitForExit(5000);
        // The first process can hand the window to others and leave; close every process of this run's profile.
        foreach (int id in ProcessesUsing(root))
        {
            try { using Process p = Process.GetProcessById(id); p.Kill(); p.WaitForExit(3000); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static void Visit(nint source, Action<PipWindow, Func<Point, Point>, StringBuilder> steps, StringBuilder log)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(1000, 220);
            view.SetInteractive(true);
            Point home = Desktop.CursorPosition();
            var driver = new Thread(() =>
            {
                try
                {
                    Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                    if (!Desktop.WaitFor(() => view.LiveInteraction is not null)) throw new InvalidOperationException("No direct-input visit started.");
                    Rectangle window = Desktop.BoundsOf(source);
                    Point ToView(Point offset)
                    {
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        return new Point(area.Left + (int)Math.Round((offset.X - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((offset.Y - shown.Top) * (double)area.Height / shown.Height));
                    }
                    log.AppendLine($"window {window} view {view.Bounds} geometry {view.LiveInteraction!.CurrentGeometry}");
                    steps(view, ToView, log);
                    log.AppendLine("outcomes " + string.Join(",", view.PressOutcomes) + " ended '" + view.LastVisitEnd + "'");
                }
                catch (Exception e) { log.AppendLine("EXC " + e); }
                finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            view.Close();
        });
    }

    private static string Sample(int ms)
    {
        var seen = new List<string>();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            GetCursorInfo(ref info);
            string shape = Shape(info.Cursor);
            if (seen.Count == 0 || seen[^1] != shape) seen.Add(shape);
            Thread.Sleep(5);
        }
        return string.Join(">", seen);
    }

    public static IEnumerable<object[]> BrowserCases() => ToolbarCases().Select(c => (string)c[0]).Distinct().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(BrowserCases))]
    public void A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys(string path)
    {
        var log = new StringBuilder();
        const string page = """
            <!doctype html><title>Probe c=0 sel=0 val=</title>
            <style>body{margin:0;font:20px sans-serif}#t{position:absolute;left:20px;top:20px;width:400px}#l{position:absolute;left:520px;top:30px;font-size:40px}
            #b{position:absolute;left:520px;top:200px;width:200px;height:80px}#i{position:absolute;left:520px;top:350px;width:250px;font-size:24px}</style>
            <p id=t>Some ordinary text to select with the pointer. It runs over several lines so a drag can cover a good part of it here.</p>
            <a id=l href="#x">A link</a><button id=b>Press</button><input id=i>
            <script>let c=0;const r=()=>document.title=`Probe c=${c} sel=${getSelection().toString().length} val=${i.value}`;
            b.onclick=()=>{c++;r()};document.addEventListener('selectionchange',r);i.oninput=r;</script>
            """;
        foreach (bool share in new[] { true })
        {
            var (browser, window, root) = Open(page, "Probe", app: true, path);
            try
            {
                PipInteraction.ShareInput = share;
                log.AppendLine($"--- share={share}");
                Visit(window, (view, at, l) =>
                {
                    Rectangle client = Desktop.BoundsOf(window);
                    // Offsets are window-relative; the page starts under the app window's title bar.
                    int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - client.Top;
                    foreach (var (name, spot) in new[] { ("text", new Point(120, top + 40)), ("link", new Point(560, top + 50)),
                        ("plain", new Point(300, top + 450)), ("button", new Point(600, top + 230)), ("text2", new Point(200, top + 60)) })
                    {
                        Desktop.SlideMouse(at(spot));
                        Thread.Sleep(80);
                        for (int i = 0; i < 4; i++) { Desktop.SlideMouse(at(spot + new Size(i * 3, 1))); Thread.Sleep(60); }
                        l.AppendLine($"{name}: {Sample(500)}");
                    }
                    Desktop.SlideMouse(at(new Point(600, top + 230)));
                    Thread.Sleep(100);
                    for (int i = 0; i < 3; i++) { Desktop.MouseButton(true); Thread.Sleep(40); Desktop.MouseButton(false); Thread.Sleep(400); }
                    l.AppendLine("after 3 button clicks: " + WindowInfo.Title(window));
                    Desktop.SlideMouse(at(new Point(30, top + 30)));
                    Thread.Sleep(100);
                    Desktop.MouseButton(true);
                    for (int i = 1; i <= 8; i++) { Desktop.SlideMouse(at(new Point(30 + i * 40, top + 30 + i * 6))); Thread.Sleep(60); }
                    l.AppendLine("during drag: " + Sample(300));
                    Desktop.MouseButton(false);
                    Thread.Sleep(300);
                    l.AppendLine("after drag select: " + WindowInfo.Title(window));
                    Desktop.SlideMouse(at(new Point(600, top + 365)));
                    Thread.Sleep(100);
                    Desktop.MouseButton(true); Thread.Sleep(40); Desktop.MouseButton(false);
                    Thread.Sleep(400);
                    Desktop.Press(Keys.A); Desktop.Press(Keys.B); Desktop.Press(Keys.C);
                    Thread.Sleep(300);
                    l.AppendLine("over input while typing: " + Sample(400));
                    l.AppendLine("after typing: " + WindowInfo.Title(window) + " fg=" + (User32.GetForegroundWindow() == window));
                }, log);
            }
            finally { Close(browser, root); PipInteraction.ShareInput = true; }
        }

        string report = log.ToString();
        output.WriteLine(report);
        Assert.True(report.Contains("text: ibeam") && report.Contains("text2: ibeam"), report);
        Assert.Contains("link: hand", report);
        Assert.Contains("plain: arrow", report);
        Assert.Contains("after 3 button clicks: Probe c=3 ", report);
        var selected = System.Text.RegularExpressions.Regex.Match(report, @"after drag select: Probe c=3 sel=(\d+)");
        Assert.True(selected.Success && int.Parse(selected.Groups[1].Value) > 50, report);
        Assert.Contains("val=abc fg=True", report);
        Assert.DoesNotContain("EXC ", report);
    }

    /// <summary>
    /// Windows drops a low-level mouse hook that answers slowly, and every pointer move on the desktop waits for
    /// it. The hook must stay cheap while a busy page is used through a view: moves, presses and wheel turns.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrowserCases))]
    public void The_hook_answers_quickly_while_a_browser_is_used_through_a_view(string path)
    {
        var log = new StringBuilder();
        string cells = string.Concat(Enumerable.Range(0, 600).Select(i => $"<a href=#{i} title=t{i}>cell {i}</a> "));
        string page = "<!doctype html><title>LatencyProbe</title><style>body{margin:0;font:14px sans-serif}a{display:inline-block;width:80px;height:22px}a:hover{background:#cde}</style>" +
            "<div style='height:3000px'>" + cells + "</div>";
        var (browser, window, root) = Open(page, "LatencyProbe", app: true, path);
        string? timing = null;
        try
        {
            Visit(window, (view, at, l) =>
            {
                var live = view.LiveInteraction!;
                var random = new Random(7);
                Rectangle bounds = Desktop.BoundsOf(window);
                for (int i = 0; i < 1500; i++)
                {
                    Desktop.SlideMouse(at(new Point(40 + random.Next(bounds.Width - 80), 80 + random.Next(bounds.Height - 160))));
                    Thread.Sleep(2);
                    if (i % 150 == 75) { mouse_event(0x800, 0, 0, unchecked((uint)-120), 0); Thread.Sleep(5); mouse_event(0x800, 0, 0, 120, 0); }
                    if (i % 300 == 150) { mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); }
                    if (view.LiveInteraction is null) { l.AppendLine("EXC visit ended early: " + view.LastVisitEnd + " at " + i); break; }
                }
                timing = live.Timing.ToString();
                l.AppendLine(timing);
            }, log);
        }
        finally { Close(browser, root); }
        output.WriteLine(log.ToString());
        Assert.DoesNotContain("EXC ", log.ToString());
        Assert.NotNull(timing);
    }

    public static IEnumerable<object[]> ToolbarCases()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "chrome-user", "chrome.exe");
        if (!File.Exists(bundled)) bundled = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        string chrome = new[] { bundled, @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" }.FirstOrDefault(File.Exists) ?? bundled;
        foreach (string path in new[] { EdgePath, chrome }.Where(File.Exists))
        foreach (bool maximized in new[] { false, true })
        foreach (double scale in new[] { 1.0, 1.5 }) yield return [path, maximized, scale];
    }

    [Theory]
    [MemberData(nameof(ToolbarCases))]
    public void Normal_browser_toolbar_takes_mapped_clicks(string path, bool maximized, double scale)
    {
        var (browser, window, root) = Open("<!doctype html><title>ToolbarProbe</title><script>document.title='ToolbarProbe '+(sessionStorage.loads=+(sessionStorage.loads||0)+1)</script>", "ToolbarProbe", app: false, path, scale);
        var log = new StringBuilder();
        PipInteraction.DiagnoseInput = true;
        try
        {
            File.WriteAllText(Path.Combine(root, "next.html"), "<!doctype html><title>NextProbe</title>");
            // A new browser can inherit a maximized placement from its environment.
            User32.ShowWindow(window, maximized ? 3 : 9);
            Thread.Sleep(500);
            Rectangle original = Desktop.BoundsOf(window);
            Visit(window, (view, at, l) =>
            {
                Rectangle parked = Desktop.BoundsOf(window);
                Point Spot(Func<BrowserAccessibility.Control, bool> match)
                {
                    var controls = BrowserAccessibility.Read(window);
                    if (!controls.Any(c => match(c))) foreach (var item in controls.Where(c => c.Type == 50000)) l.AppendLine("available " + item);
                    var control = Assert.Single(controls, c => match(c));
                    Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, control.ToString());
                    log.AppendLine("target " + control);
                    return at(new Point(control.Bounds.Left + control.Bounds.Width / 2 - parked.Left,
                        control.Bounds.Top + control.Bounds.Height / 2 - parked.Top));
                }
                bool Omnibox(BrowserAccessibility.Control c) => c.Type == 50004 && c.Name.Contains("address", StringComparison.OrdinalIgnoreCase);
                void Click(Point point, bool right = false)
                {
                    Desktop.SlideMouse(point); Thread.Sleep(120);
                    mouse_event(right ? 8u : 2u, 0, 0, 0, 0); Thread.Sleep(50);
                    mouse_event(right ? 16u : 4u, 0, 0, 0, 0); Thread.Sleep(250);
                }
                void Button(params string[] names) => Click(Spot(c => c.Type == 50000 && names.Any(name => c.Name.StartsWith(name, StringComparison.Ordinal))));
                void Check(bool condition, string effect)
                {
                    l.AppendLine(effect + "=" + condition);
                    Assert.True(condition, effect);
                    Assert.Equal(parked, Desktop.BoundsOf(window));
                    Assert.NotNull(view.LiveInteraction);
                }
                try
                {
                    Click(Spot(Omnibox));
                    Check(Desktop.WaitFor(() => BrowserAccessibility.Read(window).Any(c => Omnibox(c) && c.Focused)), "omnibox focus");
                    Desktop.Press(Keys.A, Keys.ControlKey); Thread.Sleep(200);
                    Desktop.Press(Keys.A); Desktop.Press(Keys.B); Desktop.Press(Keys.C); Thread.Sleep(200);
                    Desktop.Press(Keys.A, Keys.ControlKey); Desktop.Press(Keys.C, Keys.ControlKey); Thread.Sleep(200);
                    string typed = (string)view.Invoke(() => Clipboard.GetText());
                    Check(typed == "abc", "omnibox physical keyboard");
                    // Enter a controlled local URL using the real keyboard after mouse focus, without Ctrl+L.
                    Desktop.Press(Keys.A, Keys.ControlKey);
                    Thread.Sleep(300); // Let physical key input finish before posting the URL characters.
                    foreach (char ch in new Uri(Path.Combine(root, "next.html")).AbsoluteUri)
                        SendCharacter(window, ch);
                    Thread.Sleep(300);
                    Desktop.Press(Keys.Enter);
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("NextProbe")), "omnibox typing and navigation");
                    Button("Back");
                    l.AppendLine("after back title=" + WindowInfo.Title(window));
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("ToolbarProbe")), "back");
                    Button("Forward");
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("NextProbe")), "forward");
                    Button("Back");
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("ToolbarProbe")), "back again");
                    string beforeReload = WindowInfo.Title(window);
                    Button("Reload", "Refresh");
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("ToolbarProbe") && WindowInfo.Title(window) != beforeReload), "reload");
                    Button("New Tab");
                    Check(Desktop.WaitFor(() => BrowserAccessibility.Read(window).Count(c => c.Type == 50019) == 2), "new tab");
                    Click(Spot(c => c.Type == 50019 && c.Name.StartsWith("ToolbarProbe")));
                    Check(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("ToolbarProbe")), "existing tab");
                    foreach (bool tab in new[] { true, false })
                    {
                        Click(Spot(c => tab ? c.Type == 50019 && c.Name.StartsWith("ToolbarProbe") : Omnibox(c)), right: true);
                        Check(Desktop.WaitFor(() => BrowserAccessibility.Read(window).Any(c => c.Type == 50009) || HasMenu(window)), tab ? "tab context menu" : "omnibox context menu");
                        Desktop.Press(Keys.Escape); Thread.Sleep(300);
                    }
                    // A toolbar menu is observable independently of message queuing.
                    Button("Chromium", "Customize and control Google Chrome", "Settings and more (Alt+F)", "Google Chrome", "Chrome");
                    Check(Desktop.WaitFor(() => HasMenu(window)), "toolbar menu");
                    Desktop.Press(Keys.Escape); Thread.Sleep(300);
                    var buttons = BrowserAccessibility.Read(window);
                    var minimize = Assert.Single(buttons, c => c.Type == 50000 && c.Name == "Minimize");
                    var tabControl = buttons.First(c => c.Type == 50019);
                    int y = tabControl.Bounds.Top + tabControl.Bounds.Height / 2;
                    Point caption = default;
                    for (int x = minimize.Bounds.Left - 12; x > parked.Left; x -= 4)
                    {
                        User32.SendMessageTimeout(window, User32.WM_NCHITTEST, 0, (nint)((y << 16) | (x & 0xffff)), 2, 30, out nint hit);
                        if (hit == 2) { caption = new Point(x, y); break; }
                    }
                    Assert.NotEqual(default, caption);
                    Click(at(new Point(caption.X - parked.Left, caption.Y - parked.Top)));
                    Check(view.PressOutcomes.Last() == PressOutcome.MovedView, "blank caption manages view");
                }
                finally
                {
                    if (view.LiveInteraction is { } live)
                        foreach (string entry in live.InputDiagnostics) l.AppendLine(entry);
                }
            }, log);
            Assert.Equal(original, Desktop.BoundsOf(window));
            Assert.Equal(maximized, User32.IsZoomed(window));
            Assert.True(Desktop.Alpha(window) is null or 255);
            Assert.DoesNotContain("EXC ", log.ToString());
        }
        finally
        {
            output.WriteLine(log.ToString());
            PipInteraction.DiagnoseInput = false;
            Close(browser, root);
        }
    }

    /// <summary>Processes whose command line names this run's folder, which holds its browser profile.</summary>
    private static List<int> ProcessesUsing(string root)
    {
        var ids = new List<int>();
        using var search = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='chrome.exe' OR Name='msedge.exe'");
        foreach (var item in search.Get())
            using (item)
                if (item["CommandLine"] is string line && line.Contains(root, StringComparison.OrdinalIgnoreCase))
                    ids.Add(Convert.ToInt32(item["ProcessId"]));
        return ids;
    }


    private static void SendCharacter(nint window, char ch) => User32.PostMessage(window, 0x102, ch, 0);
    private static bool HasMenu(nint owner)
    {
        bool found = false;
        uint thread = User32.GetWindowThreadProcessId(owner, out _);
        EnumThreadWindows(thread, (window, _) =>
        {
            if (window != owner && User32.IsWindowVisible(window) &&
                (WindowInfo.ClassName(window) == "#32768" ||
                 WindowInfo.IsOwnedBy(window, owner) && SourcePopups.KindOf(window) == SourcePopups.Kind.Interactive)) found = true;
            return true;
        }, 0);
        return found;
    }
    private delegate bool WindowVisitor(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowVisitor visitor, nint data);
}
