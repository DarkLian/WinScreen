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
public sealed class BrowserInputTests
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

    private static (Process Browser, nint Window, string Root) Open(string html, string title, bool app)
    {
        string root = Directory.CreateTempSubdirectory("winscreen-browser").FullName;
        string page = Path.Combine(root, "page.html");
        File.WriteAllText(page, html);
        var browser = new Process { StartInfo = new ProcessStartInfo(EdgePath) { UseShellExecute = false } };
        foreach (string arg in new[] { "--user-data-dir=" + Path.Combine(root, "profile"), "--no-first-run", "--no-default-browser-check",
            "--disable-sync", "--window-size=900,700", "--window-position=160,100", app ? "--app=" + new Uri(page).AbsoluteUri : new Uri(page).AbsoluteUri })
            browser.StartInfo.ArgumentList.Add(arg);
        browser.Start();
        nint window = 0;
        Desktop.WaitFor(() =>
        {
            window = 0;
            foreach (Process p in Process.GetProcessesByName("msedge"))
                if (p.MainWindowHandle != 0 && WindowInfo.Title(p.MainWindowHandle).StartsWith(title)) window = p.MainWindowHandle;
            return window != 0;
        }, 20000);
        Thread.Sleep(1500);
        return (browser, window, root);
    }

    private static void Close(Process browser, string root)
    {
        try { foreach (Process p in Process.GetProcessesByName("msedge")) p.Kill(true); } catch (Exception) { }
        browser.WaitForExit(5000);
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
                    if (!Desktop.WaitFor(() => view.LiveInteraction is not null)) { log.AppendLine("no visit"); return; }
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
                catch (Exception e) { log.AppendLine("EXC " + e.Message); }
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

    [Fact]
    public void A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys()
    {
        if (!File.Exists(EdgePath)) return;
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
            var (browser, window, root) = Open(page, "Probe", app: true);
            try
            {
                PipInteraction.ShareInput = share;
                log.AppendLine($"--- share={share}");
                Visit(window, (view, at, l) =>
                {
                    Rectangle client = Desktop.BoundsOf(window);
                    // Offsets are window-relative; the page starts under the app window's title bar.
                    int top = 40;
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

        // The tab strip of a normal browser window.
        var (tabs, tabWindow, tabRoot) = Open("<!doctype html><title>TabProbe</title><p>tabs</p>", "TabProbe", app: false);
        try
        {
            Rectangle r = Desktop.BoundsOf(tabWindow);
            var runs = new StringBuilder();
            int lastHit = -99, runStart = 0;
            var clientRuns = new List<(int From, int To)>();
            for (int x = 0; x < r.Width / 2; x += 2)
            {
                var screen = new Point(r.Left + x, r.Top + 22);
                User32.SendMessageTimeout(tabWindow, User32.WM_NCHITTEST, 0, (nint)((screen.Y << 16) | (screen.X & 0xFFFF)), 2, 200, out nint hit);
                if ((int)hit != lastHit)
                {
                    if (lastHit == 1) clientRuns.Add((runStart, x - 2));
                    runs.Append($" {x}:{hit}");
                    lastHit = (int)hit; runStart = x;
                }
            }
            log.AppendLine($"tab window 0x{tabWindow:X} {r} title '{WindowInfo.Title(tabWindow)}' hits at y=22:{runs}");
            if (clientRuns.Count > 0)
            {
                var plus = clientRuns[^1];
                var spot = new Point((plus.From + plus.To) / 2, 22);
                log.AppendLine($"clicking new-tab candidate at {spot}");
                Visit(tabWindow, (view, at, l) =>
                {
                    Desktop.SlideMouse(at(spot));
                    Thread.Sleep(300);
                    Desktop.MouseButton(true); Thread.Sleep(50); Desktop.MouseButton(false);
                    Thread.Sleep(1500);
                    l.AppendLine("after click: " + WindowInfo.Title(tabWindow));
                }, log);
            }
        }
        finally { Close(tabs, tabRoot); }
        string report = log.ToString();
        Assert.True(report.Contains("text: ibeam") && report.Contains("text2: ibeam"), report);
        Assert.Contains("link: hand", report);
        Assert.Contains("plain: arrow", report);
        Assert.Contains("after 3 button clicks: Probe c=3 ", report);
        var selected = System.Text.RegularExpressions.Regex.Match(report, @"after drag select: Probe c=3 sel=(\d+)");
        Assert.True(selected.Success && int.Parse(selected.Groups[1].Value) > 50, report);
        Assert.Contains("val=abc fg=True", report);
        Assert.Contains("after click: New tab", report); // The new-tab button in the tab strip, near the view's top edge.
        Assert.DoesNotContain("EXC ", report);
    }
}
