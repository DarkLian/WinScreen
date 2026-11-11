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
    [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string id, uint flags);
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
        // These profiles test Latin physical keys; do not inherit the desktop's active IME.
        // Request the layout on this test browser only, leaving the owner's windows alone.
        User32.PostMessage(window, 0x50, 0, LoadKeyboardLayout("00000409", 0));
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
            if (Environment.GetEnvironmentVariable("WINSCREEN_TEST_ENHANCED_HOVER") == "1") view.SetEnhancedHover(true);
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
    public void Browser_hover_observations_distinguish_pointer_shapes_from_persistent_effects(string path)
    {
        const string page = """
            <!doctype html><title>HoverState</title>
            <style>body{margin:0}#hover{position:absolute;left:0;top:0;width:100vw;height:90vh;background:red;cursor:pointer}
            #hover:hover{background:rgb(0, 128, 0)}#preview{display:none}#hover:hover #preview{display:block}</style>
            <div id=hover title="Hover tooltip">Hover me<div id=preview>Preview</div></div>
            <script>let enter=0,over=0,move=0,leave=0;
            hover.onmouseenter=()=>enter++;hover.onmouseover=()=>over++;hover.onmousemove=()=>move++;hover.onmouseleave=()=>leave++;
            setInterval(()=>document.title=`HoverState active=${hover.matches(':hover')} preview=${getComputedStyle(preview).display} enter=${enter} over=${over} move=${move} leave=${leave}`,50);</script>
            """;
        var log = new StringBuilder();
        var (browser, window, root) = Open(page, "HoverState", app: true, path);
        try
        {
            Rectangle document = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds;
            Desktop.GainForegroundRights();
            Desktop.Activate(window);
            Point originalPoint = new(document.Left + document.Width / 2, document.Top + document.Height / 2);
            // Successive browser cases can open under the restored pointer at exactly this point.
            // Generate real movement rather than relying on a same-coordinate synthetic event.
            Desktop.SlideMouse(originalPoint - new Size(20, 20));
            Thread.Sleep(80);
            Desktop.SlideMouse(originalPoint);
            Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).Contains("active=true preview=block"), 4000),
                $"Visible hover baseline: {WindowInfo.Title(window)}; window={Desktop.BoundsOf(window)} document={document} point={originalPoint} foreground=0x{User32.GetForegroundWindow():X} source=0x{window:X}");
            Thread.Sleep(1500);
            log.AppendLine("original resting: " + WindowInfo.Title(window));
            Visit(window, (view, at, l) =>
            {
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                foreach (bool sharing in new[] { true, false })
                {
                    PipInteraction.ShareInput = sharing;
                    Point[] points = Enumerable.Range(0, 5).Select(i => at(new Point(140 + i * 20, top + 140))).ToArray();
                    // End and restart so the sharing experiment starts with the requested policy.
                    Desktop.SlideMouse(new Point(view.Left - 50, view.Top + 100));
                    Thread.Sleep(200);
                    Desktop.SlideMouse(points[0]);
                    Thread.Sleep(400);
                    for (int i = 0; i < 5; i++) { Desktop.SlideMouse(points[i]); Thread.Sleep(80); }
                    l.AppendLine($"sharing={sharing} moving: {WindowInfo.Title(window)} cursor={Sample(100)}");
                    Thread.Sleep(1500);
                    l.AppendLine($"sharing={sharing} resting: {WindowInfo.Title(window)} cursor={Sample(100)}");
                }
                if (Environment.GetEnvironmentVariable("WINSCREEN_HOVER_PROBE_DLL") is not null)
                {
                    PipInteraction.ShareInput = true;
                    Point offset = new(220, top + 140);
                    Point hoverPoint = at(offset);
                    var input = new PipMessageInput(window, WindowIdentity.Capture(window));
                    Point mapped = Desktop.BoundsOf(window).Location + (Size)offset;
                    nint target = input.TargetAt(mapped);
                    l.AppendLine($"native target=0x{target:X} class={WindowInfo.ClassName(target)} thread={User32.GetWindowThreadProcessId(target, out _)} rootThread={User32.GetWindowThreadProcessId(window, out _)}");
                    using (var hook = new NativeHoverProbe(target))
                    {
                        Desktop.SlideMouse(new Point(view.Left - 50, view.Top + 100));
                        Thread.Sleep(200);
                        Desktop.SlideMouse(hoverPoint);
                        Thread.Sleep(200);
                        Desktop.NudgeMouse(2, 0);
                        for (int sample = 0; sample < 10; sample++)
                        {
                            Thread.Sleep(500);
                            string state = WindowInfo.Title(window);
                            l.AppendLine($"native resting {sample}: {state}");
                            Assert.Contains("active=true preview=block", state);
                        }
                        l.AppendLine($"native install={hook.InstallMilliseconds:F3}ms");
                    }
                    Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).Contains("active=false preview=none")), WindowInfo.Title(window));
                    l.AppendLine("native removed: " + WindowInfo.Title(window));
                }
            }, log);
        }
        finally { Close(browser, root); PipInteraction.ShareInput = true; }
        string report = log.ToString();
        output.WriteLine(report);
        Assert.DoesNotContain("EXC ", report);
        Assert.Contains("original resting: HoverState active=true preview=block", report);
        Assert.Contains("cursor=hand", report);
        var events = System.Text.RegularExpressions.Regex.Match(report, @"sharing=True resting: HoverState active=(true|false) preview=\w+ enter=(\d+) over=(\d+) move=(\d+) leave=(\d+)");
        Assert.True(events.Success && int.Parse(events.Groups[2].Value) > 0 && int.Parse(events.Groups[3].Value) > 0 &&
            int.Parse(events.Groups[4].Value) > 0, report);
        // Preserve the ordinary-input observations. The separate Enhanced_hover test asserts sustained
        // CSS/DOM effects and cleanup for the native helper's validated rollout.
    }

    [NativeHoverFact(true)]
    public void Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable()
    {
        string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll")), "Build the native x64 helper before this runtime test.");
        const string page = """
            <!doctype html><title>EnhancedState</title><style>body{margin:0}section{position:absolute;top:0;width:40vw;height:70vh}
            #a{left:0}#b{left:50vw}.preview{display:none}section:hover .preview{display:block}</style>
            <section id=a>A<div class=preview>Preview A</div></section><section id=b>B<div class=preview>Dropdown B</div></section>
            <script>let dom='none',enter=0,leave=0;for(const x of [a,b]){x.onmouseenter=()=>{dom=x.id;enter++};x.onmouseleave=()=>{dom='none';leave++}}
            setInterval(()=>{let x=document.querySelector('section:hover');document.title=`EnhancedState css=${x?.id||'none'} dom=${dom} preview=${x?getComputedStyle(x.lastChild).display:'none'} visibility=${document.visibilityState} enter=${enter} leave=${leave}`},30)</script>
            """;
        var (browser, window, root) = Open(page, "EnhancedState", app: true, path);
        var log = new StringBuilder();
        try
        {
            Visit(window, (view, at, l) =>
            {
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                Point first = at(new Point(150, top + 140));
                Point second = at(new Point(Desktop.BoundsOf(window).Width * 3 / 4, top + 140));
                Point outside = new(view.Left - 50, view.Top + 100);
                void State(string id) => Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).Contains($"css={id} dom={id} preview={(id == "none" ? "none" : "block")}"), 3000), WindowInfo.Title(window) + "; " + view.LiveInteraction?.HoverStatus + "; pointer=" + Desktop.CursorPosition() + "; geometry=" + view.LiveInteraction?.CurrentGeometry + "; " + Surroundings(window));
                string Leaves() => System.Text.RegularExpressions.Regex.Match(WindowInfo.Title(window), @"leave=\d+").Value;
                view.Invoke(() => view.SetEnhancedHover(true));
                for (int visit = 0; visit < 3; visit++)
                {
                    Desktop.SlideMouse(first); State("a");
                    string leaves = Leaves();
                    for (int sample = 0; sample < 10; sample++) { Thread.Sleep(500); Assert.True(WindowInfo.Title(window).Contains("css=a dom=a preview=block visibility=visible"), WindowInfo.Title(window) + "; " + view.LiveInteraction?.HoverStatus); Assert.Equal(leaves, Leaves()); }
                    Assert.StartsWith("active", view.LiveInteraction!.HoverStatus);
                    l.AppendLine($"stationary {visit}: {WindowInfo.Title(window)}; {view.LiveInteraction.HoverStatus}");
                    Desktop.SlideMouse(second); State("b");
                    leaves = Leaves();
                    for (int sample = 0; sample < 6; sample++) { Thread.Sleep(500); State("b"); Assert.Equal(leaves, Leaves()); }
                    Desktop.SlideMouse(outside); State("none");
                    Desktop.SlideMouse(first); State("a");
                    view.Invoke(() => view.SetEnhancedHover(false)); State("none");
                    Desktop.NudgeMouse(2, 0); Thread.Sleep(400); State("none");
                    view.Invoke(() => view.SetEnhancedHover(true));
                    Desktop.NudgeMouse(-2, 0); State("a");
                    l.AppendLine($"visit {visit}: {WindowInfo.Title(window)}; {view.LiveInteraction!.HoverStatus}; {view.LiveInteraction.Timing}");
                }
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    view.Invoke(() => view.SetEnhancedHover(false)); State("none");
                    view.Invoke(() => view.SetEnhancedHover(true)); Desktop.NudgeMouse(cycle % 2 == 0 ? 2 : -2, 0); State("a");
                }
                view.Invoke(view.SuspendView); State("none");
            }, log);
        }
        finally { output.WriteLine(log.ToString()); Close(browser, root); }
        Assert.DoesNotContain("EXC ", log.ToString());
    }

    /// <summary>What could be hiding a parked original from itself, for a failure message.</summary>
    private static string Surroundings(nint window) =>
        $"parked={Desktop.BoundsOf(window)} topmost={(Desktop.ExStyle(window) & 8) != 0} alpha={Desktop.Alpha(window)}; " +
        $"foreground={WindowInfo.ClassName(User32.GetForegroundWindow())} zoomed={User32.IsZoomed(User32.GetForegroundWindow())}; above parked: {Above(window)}";

    /// <summary>Every visible top-level window from the top down, until the one asked for.</summary>
    private static string Above(nint window)
    {
        var seen = new List<string>();
        for (nint top = User32.GetWindow(User32.GetDesktopWindow(), 5); top != 0 && top != window; top = User32.GetWindow(top, 2))
        {
            if (!User32.IsWindowVisible(top)) continue;
            Rectangle bounds = Desktop.BoundsOf(top);
            if (bounds.Width == 0 || bounds.Height == 0) continue;
            seen.Add($"{WindowInfo.ClassName(top)}{((Desktop.ExStyle(top) & 8) != 0 ? "(topmost)" : "")}{bounds}");
        }
        return string.Join(" > ", seen);
    }

    /// <summary>
    /// The everyday arrangement: another app maximized and in use, with a small view over it. A maximized window
    /// reaches past every edge of its screen, where a hidden original keeps its strip; the original stays above it,
    /// so the page goes on believing it can be seen. Checked with an ordinary window and with a second browser.
    /// </summary>
    [NativeHoverTheory(true)]
    [InlineData(false)]
    [InlineData(true)]
    public void Enhanced_hover_holds_with_another_window_maximized_in_front(bool anotherBrowser)
    {
        string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
        const string page = """
            <!doctype html><title>CoveredState</title><style>body{margin:0}section{position:absolute;top:0;width:40vw;height:70vh}
            #a{left:0}#b{left:50vw}.preview{display:none}section:hover .preview{display:block}</style>
            <section id=a>A<div class=preview>Preview A</div></section><section id=b>B<div class=preview>Dropdown B</div></section>
            <script>let dom='none',enter=0,leave=0;for(const x of [a,b]){x.onmouseenter=()=>{dom=x.id;enter++};x.onmouseleave=()=>{dom='none';leave++}}
            setInterval(()=>{let x=document.querySelector('section:hover');document.title=`CoveredState css=${x?.id||'none'} dom=${dom} preview=${x?getComputedStyle(x.lastChild).display:'none'} visibility=${document.visibilityState} enter=${enter} leave=${leave}`},30)</script>
            """;
        var (browser, window, root) = Open(page, "CoveredState", app: true, path);
        var log = new StringBuilder();
        (Process Browser, nint Window, string Root)? other = null;
        Form? cover = null;
        var shown = new ManualResetEventSlim();
        Exception? coverFailure = null;
        var coverThread = new Thread(() =>
        {
            try
            {
                Desktop.GainForegroundRights(); // Before this thread shows a window.
                cover = new Form { Text = "WinScreen test cover", WindowState = FormWindowState.Maximized, BackColor = Color.DimGray };
                cover.Shown += (_, _) => { Desktop.Activate(cover.Handle); shown.Set(); };
                Application.Run(cover);
            }
            catch (Exception e) { coverFailure = e; shown.Set(); }
        });
        coverThread.SetApartmentState(ApartmentState.STA);
        try
        {
            nint coverWindow;
            if (anotherBrowser)
            {
                // A second browser of the same kind, with its own profile, as the app in front.
                other = Open("<!doctype html><title>CoverBrowser</title><body style='background:#444'>", "CoverBrowser", app: false, path);
                coverWindow = other.Value.Window;
                Desktop.GainForegroundRights();
                User32.ShowWindow(coverWindow, 3);
                Desktop.Activate(coverWindow);
                Thread.Sleep(800);
            }
            else
            {
                coverThread.Start();
                Assert.True(shown.Wait(130000), "The covering window did not open.");
                Assert.Null(coverFailure);
                coverWindow = cover!.Invoke(() => cover.Handle);
            }
            Visit(window, (view, at, l) =>
            {
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                Point first = at(new Point(150, top + 140));
                Point second = at(new Point(Desktop.BoundsOf(window).Width * 3 / 4, top + 140));
                string Describe() => $"{WindowInfo.Title(window)}; {Surroundings(window)}; {view.LiveInteraction?.HoverStatus}";
                void State(string id) => Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).Contains($"css={id} dom={id} preview={(id == "none" ? "none" : "block")}"), 3000), Describe());
                Assert.Equal(coverWindow, User32.GetForegroundWindow());
                view.Invoke(() => view.SetEnhancedHover(true));
                Desktop.SlideMouse(first);
                Thread.Sleep(600);
                l.AppendLine("resting: " + Describe());
                State("a");
                for (int sample = 0; sample < 6; sample++) { Thread.Sleep(500); Assert.True(WindowInfo.Title(window).Contains("css=a dom=a preview=block visibility=visible"), Describe()); }
                Desktop.SlideMouse(second); State("b");
                // The person goes on using the app in front: it is brought forward again while the view is in use.
                User32.SetForegroundWindow(coverWindow);
                User32.SetWindowPos(coverWindow, 0, 0, 0, 0, 0, 0x13);
                Thread.Sleep(400);
                l.AppendLine("after the cover came forward: " + Describe());
                Desktop.SlideMouse(first); State("a");
                for (int sample = 0; sample < 6; sample++) { Thread.Sleep(500); Assert.True(WindowInfo.Title(window).Contains("css=a dom=a preview=block visibility=visible"), Describe()); }
                Desktop.SlideMouse(new Point(view.Left - 50, view.Top + 100)); State("none");
                Assert.Equal(coverWindow, User32.GetForegroundWindow());
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            if (cover is { IsDisposed: false, IsHandleCreated: true }) cover.Invoke(cover.Close);
            if (coverThread.IsAlive) coverThread.Join(5000);
            if (other is { } front) Close(front.Browser, front.Root);
            Close(browser, root);
        }
        Assert.DoesNotContain("EXC ", log.ToString());
    }

    /// <summary>
    /// A browser that was just in front and is then hidden decides within a second whether it can still be seen,
    /// and keeps that answer until it is clicked. On the strip under the taskbar the answer was "no": the page
    /// stopped taking pointer moves and hover never started. Checked from a fresh browser each time, with the option
    /// on in Settings before the view opens and turned on from the view's menu afterwards.
    /// </summary>
    [NativeHoverTheory(true)]
    [InlineData(true)]
    [InlineData(false)]
    public void Enhanced_hover_starts_on_a_browser_hidden_a_moment_ago(bool fromSettings)
    {
        string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
        const string page = """
            <!doctype html><title>FreshState</title><style>body{margin:0}section{position:absolute;top:0;width:40vw;height:70vh}
            #a{left:0}#b{left:50vw}.preview{display:none}section:hover .preview{display:block}</style>
            <section id=a>A<div class=preview>Preview A</div></section><section id=b>B<div class=preview>Dropdown B</div></section>
            <script>let dom='none',enter=0,leave=0;for(const x of [a,b]){x.onmouseenter=()=>{dom=x.id;enter++};x.onmouseleave=()=>{dom='none';leave++}}
            setInterval(()=>{let x=document.querySelector('section:hover');document.title=`FreshState css=${x?.id||'none'} dom=${dom} preview=${x?getComputedStyle(x.lastChild).display:'none'} visibility=${document.visibilityState} enter=${enter} leave=${leave}`},30)</script>
            """;
        var before = PipHoverApps.Preferences;
        var failures = new StringBuilder();
        try
        {
            if (fromSettings) PipHoverApps.Preferences = new WinScreen.Settings.PipHoverPreferences { Browsers = true };
            for (int run = 0; run < 4; run++)
            {
                var (browser, window, root) = Open(page, "FreshState", app: true, path);
                var log = new StringBuilder();
                try
                {
                    Visit(window, (view, at, l) =>
                    {
                        int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                        if (!fromSettings) view.Invoke(() => view.SetEnhancedHover(true));
                        Assert.True(view.LiveInteraction!.EnhancedHover);
                        // One jump and then stillness, as when a pointer comes to rest: nothing later can repair a missed start.
                        Desktop.SlideMouse(at(new Point(150, top + 140)));
                        Thread.Sleep(2500);
                        if (!WindowInfo.Title(window).Contains("css=a dom=a preview=block visibility=visible"))
                            l.AppendLine($"NOT HELD {WindowInfo.Title(window)}; {Surroundings(window)}; {view.LiveInteraction?.HoverStatus}");
                    }, log);
                }
                finally { Close(browser, root); }
                if (log.ToString().Contains("NOT HELD") || log.ToString().Contains("EXC ")) failures.AppendLine($"run {run}: {log}");
            }
        }
        finally { PipHoverApps.Preferences = before; }
        output.WriteLine(failures.ToString());
        Assert.True(failures.Length == 0, failures.ToString());
    }


    [NativeHoverFact]
    public async Task A_hook_owner_crash_restores_a_parked_browser_and_clears_hover()
    {
        string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
        var (browser, window, root) = Open("""
            <!doctype html><title>CrashHover</title><style>body{margin:0}#h{width:80vw;height:80vh}#p{display:none}#h:hover #p{display:block}</style>
            <div id=h>Hover<div id=p>Preview</div></div><script>setInterval(()=>document.title=`CrashHover active=${h.matches(':hover')} preview=${getComputedStyle(p).display}`,50)</script>
            """, "CrashHover", true, path);
        Process? owner = null;
        Thread? watcher = null;
        Exception? recoveryFailure = null;
        var journal = new ChangeJournal(Path.Combine(root, "journal.json"));
        var windows = new WindowController([], journal);
        Point home = Desktop.CursorPosition();
        try
        {
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 80, 80, 800, 600, 0x14);
            Thread.Sleep(400);
            Rectangle original = Desktop.BoundsOf(window);
            long style = Desktop.ExStyle(window);
            var identity = WindowIdentity.Capture(window);
            windows.Hide(window); windows.Park(window);
            windows.SetParkedHoverSurface(window, true); windows.SetParkedReachable(window, true);
            // Outside both the parked source and its restored bounds. No unrelated app is clicked.
            Desktop.SlideMouse(new Point(1200, 700));
            string dll = Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll");
            string script = Path.Combine(root, "hook-owner.ps1");
            File.WriteAllText(script, $$"""
                Add-Type -TypeDefinition @'
                using System; using System.Runtime.InteropServices;
                public static class HookOwner {
                  [DllImport(@"{{dll}}", EntryPoint="Start", CallingConvention=CallingConvention.Winapi)]
                  private static extern IntPtr Start(uint thread, IntPtr target, long token, IntPtr source, IntPtr marker);
                  [DllImport("user32.dll", EntryPoint="SetPropW", CharSet=CharSet.Unicode)] private static extern bool SetProp(IntPtr target, string name, IntPtr value);
                  public static bool Install() {
                    IntPtr window = new IntPtr({{window.ToInt64()}}L), marker = new IntPtr(System.Diagnostics.Process.GetCurrentProcess().Id);
                    return SetProp(window, "{{WindowController.EnhancedHoverOwnerProperty}}", marker) && SetProp(window, "{{WindowController.EnhancedHoverActiveProperty}}", marker) &&
                      Start({{identity.ThreadId}}, window, {{identity.Token}}L, window, marker) != IntPtr.Zero;
                  }
                }
                '@
                if (-not ([HookOwner]::Install())) { throw 'Native owner failed to install' }
                [Console]::WriteLine('ready'); [Console]::Out.Flush()
                [Threading.Thread]::Sleep(-1)
                """);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
            owner = Process.Start(start)!;
            var ready = owner.StandardOutput.ReadLineAsync();
            Assert.Equal("ready", await ready.WaitAsync(TimeSpan.FromSeconds(20)));
            for (int move = 0; move < 5; move++) { User32.PostMessage(window, 0x200, 0, (nint)((150 << 16) | (150 + move))); Thread.Sleep(80); }
            for (int sample = 0; sample < 10; sample++)
            {
                Thread.Sleep(500);
                Assert.Contains("active=true preview=block", WindowInfo.Title(window));
            }
            int parent = owner.Id;
            watcher = new Thread(() =>
            {
                try { RecoveryWatcher.Watch(parent, () => new WindowController([], journal).RecoverFromJournal(), @"Local\WinScreen.HoverRecoveryTest." + Guid.NewGuid()); }
                catch (Exception e) { recoveryFailure = e; }
            }) { IsBackground = true };
            watcher.Start();
            owner.Kill(); Assert.True(owner.WaitForExit(5000));
            Assert.True(watcher.Join(8000), "Recovery watcher did not finish."); Assert.Null(recoveryFailure);
            Assert.Equal(original, Desktop.BoundsOf(window));
            Assert.Equal(style, Desktop.ExStyle(window));
            Assert.True(Desktop.Alpha(window) is null or 255);
            Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).Contains("active=false preview=none")), WindowInfo.Title(window));
            output.WriteLine("Owner killed: journal restored bounds, styles and opacity; CSS hover and preview cleared.");
        }
        finally
        {
            if (owner is not null) { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } owner.Dispose(); }
            watcher?.Join(8000);
            windows.RestoreAll();
            Desktop.MoveMouse(home);
            Close(browser, root);
        }
    }

    [NativeHoverTheory(true)]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_invalid_hover_helpers_keep_mapped_clicks_functional(bool invalid)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll"), backup = dll + "." + Guid.NewGuid() + ".backup";
        File.Move(dll, backup);
        try
        {
            if (invalid) File.WriteAllBytes(dll, [0x4d, 0x5a, 0, 0]);
            string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
            var (browser, window, root) = Open("""
                <!doctype html><title>Fallback count=0</title><style>button{position:absolute;left:100px;top:100px;width:300px;height:150px}</style>
                <button onclick="document.title='Fallback count=1'">Click</button>
                """, "Fallback", true, path);
            var log = new StringBuilder();
            try
            {
                Visit(window, (view, at, l) =>
                {
                    view.Invoke(() => view.SetEnhancedHover(true));
                    int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                    Desktop.SlideMouse(at(new Point(200, top + 160))); Thread.Sleep(150);
                    l.AppendLine(view.LiveInteraction!.HoverStatus);
                    Assert.StartsWith(invalid ? "failed:" : "unavailable:", view.LiveInteraction.HoverStatus);
                    Desktop.MouseButton(true); Thread.Sleep(40); Desktop.MouseButton(false);
                    Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("Fallback count=1")), WindowInfo.Title(window));
                }, log);
            }
            finally { output.WriteLine(log.ToString()); Close(browser, root); }
            Assert.DoesNotContain("EXC ", log.ToString());
        }
        finally
        {
            if (File.Exists(dll)) File.Delete(dll);
            File.Move(backup, dll);
        }
    }

    [Windows10HoverFallbackFact]
    public void Windows_10_keeps_ordinary_input_and_reports_hover_unavailable()
    {
        string path = BrowserCases().Select(c => (string)c[0]).First(p => Path.GetFileName(p) == "chrome.exe");
        var (browser, window, root) = Open("""
            <!doctype html><title>Win10Fallback count=0</title><style>button{position:absolute;left:100px;top:100px;width:300px;height:150px}</style>
            <button onclick="document.title='Win10Fallback count=1'">Click</button>
            """, "Win10Fallback", true, path);
        var log = new StringBuilder();
        try
        {
            Visit(window, (view, at, l) =>
            {
                Rectangle parked = Desktop.BoundsOf(window);
                view.Invoke(() => view.SetEnhancedHover(true));
                Assert.Equal(parked, Desktop.BoundsOf(window));
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - parked.Top;
                Desktop.SlideMouse(at(new Point(200, top + 160))); Thread.Sleep(150);
                Assert.StartsWith("unavailable: requires Windows 11", view.LiveInteraction!.HoverStatus);
                Assert.Equal(0, User32.GetProp(window, WindowController.EnhancedHoverOwnerProperty));
                Desktop.MouseButton(true); Thread.Sleep(40); Desktop.MouseButton(false);
                Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("Win10Fallback count=1")), WindowInfo.Title(window));
                l.AppendLine(view.LiveInteraction.HoverStatus);
            }, log);
        }
        finally { output.WriteLine(log.ToString()); Close(browser, root); }
        Assert.DoesNotContain("EXC ", log.ToString());
    }

    [Theory]
    [MemberData(nameof(BrowserCases))]
    public void Middle_click_keeps_the_apps_own_action_when_the_scroll_option_is_off(string path)
    {
        const string page = """
            <!doctype html><title>MiddleAction count=0</title><style>body{margin:0;height:10000px}button{position:absolute;left:100px;top:100px;width:400px;height:200px}</style>
            <button id=b>Middle action</button><script>let c=0;b.onmousedown=e=>{if(e.button===1)e.preventDefault()};b.onauxclick=e=>{if(e.button===1){e.preventDefault();document.title=`MiddleAction count=${++c}`}};</script>
            """;
        var log = new StringBuilder();
        var (browser, window, root) = Open(page, "MiddleAction", app: true, path);
        try
        {
            Visit(window, (view, at, l) =>
            {
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
                Point button = at(new Point(250, top + 160));
                for (int count = 1; count <= 2; count++)
                {
                    Desktop.SlideMouse(button); Thread.Sleep(100);
                    mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                    Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window) == $"MiddleAction count={count}", 3000),
                        $"Auxiliary action {count}: {WindowInfo.Title(window)}; outcomes {string.Join(",",view.PressOutcomes)}");
                    Assert.False(view.LiveInteraction?.Scrolling ?? false);
                    // Toggling the option on and off restores native behavior too.
                    view.Invoke(() => { view.SetMiddleClickScroll(true); view.SetMiddleClickScroll(false); });
                }
                Assert.DoesNotContain(PressOutcome.ScrollMode, view.PressOutcomes);
            }, log);
        }
        finally { Close(browser, root); }
        output.WriteLine(log.ToString());
        Assert.DoesNotContain("EXC ", log.ToString());
    }

    /// <summary>
    /// With Enhanced hover on, a browser's own middle-click scrolling works through a view: click, move away, click
    /// again to stop. The view's own scrolling stands aside for it even when its option is on.
    /// </summary>
    [NativeHoverTheory(true)]
    [InlineData(false)]
    [InlineData(true)]
    public void A_browsers_own_middle_click_scrolling_works_with_enhanced_hover(bool viewOptionOn)
    {
        string path = BrowserCases().Select(c => (string)c[0]).FirstOrDefault(p => Path.GetFileName(p) == "chrome.exe") ?? "";
        if (!File.Exists(path)) return;
        const string page = """
            <!doctype html><title>AutoScroll y=0</title><style>body{margin:0;height:60000px;background:repeating-linear-gradient(white 0px,white 90px,gray 100px)}</style>
            <script>const report=()=>document.title=`AutoScroll y=${Math.round(scrollY)}`;onscroll=report;setInterval(report,20)</script>
            """;
        var log = new StringBuilder();
        var (browser, window, root) = Open(page, "AutoScroll", app: true, path);
        try
        {
            Visit(window, (view, at, l) =>
            {
                view.Invoke(() => { view.SetEnhancedHover(true); view.SetMiddleClickScroll(viewOptionOn); });
                Point anchor = new(view.Left + view.Width / 2, view.Top + view.Height / 2);
                Desktop.SlideMouse(anchor);
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.HoverStatus.StartsWith("active") == true, 3000), view.LiveInteraction?.HoverStatus);
                int Y() => int.Parse(WindowInfo.Title(window).Split("y=")[1]);
                mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                Thread.Sleep(300);
                Assert.False(view.LiveInteraction?.Scrolling ?? false);
                for (int i = 1; i <= 6; i++) { Desktop.SlideMouse(anchor + new Size(0, i * 10)); Thread.Sleep(30); }
                Thread.Sleep(1200);
                int down = Y();
                l.AppendLine($"after 1.2 s below the click: y={down}");
                Assert.True(down > 500, $"The page didn't scroll down: y={down}; {view.LiveInteraction?.HoverStatus}; {string.Join(",", view.PressOutcomes)}");
                for (int i = 5; i >= -6; i--) { Desktop.SlideMouse(anchor + new Size(0, i * 10)); Thread.Sleep(30); }
                Thread.Sleep(600);
                int turned = Y(); Thread.Sleep(600);
                Assert.True(Y() < turned, $"The page didn't scroll back up: {turned} then {Y()}");
                Desktop.SlideMouse(anchor); Thread.Sleep(150);
                mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                Thread.Sleep(500);
                int stopped = Y();
                Desktop.SlideMouse(anchor + new Size(0, 60)); Thread.Sleep(700);
                Assert.Equal(stopped, Y());
                Assert.DoesNotContain(PressOutcome.ScrollMode, view.PressOutcomes);
            }, log);
        }
        finally { Close(browser, root); }
        output.WriteLine(log.ToString());
        Assert.DoesNotContain("EXC ", log.ToString());
    }

    [Theory]
    [MemberData(nameof(BrowserCases))]
    public void Browser_middle_click_autoscroll_moves_content_and_cancels(string path)
    {
        const string page = """
            <!doctype html><title>AutoScroll y=0</title><style>body{margin:0;height:12000px;background:repeating-linear-gradient(white 0px,white 90px,gray 100px)}</style>
            <script>const report=()=>document.title=`AutoScroll y=${Math.round(scrollY)}`;onscroll=report;setInterval(report,50)</script>
            """;
        var log = new StringBuilder();
        var (browser, window, root) = Open(page, "AutoScroll", app: true, path);
        try
        {
            Visit(window, (view, at, l) =>
            {
                view.Invoke(() => view.SetMiddleClickScroll(true));
                Point anchor = new(view.Left + view.Width / 2, view.Top + view.Height / 2);
                Desktop.SlideMouse(anchor);
                Thread.Sleep(200);
                mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == true, 300), "No prompt scroll-mode feedback.");
                l.AppendLine("middle feedback: " + Sample(500));
                Desktop.SlideMouse(anchor + new Size(0, 60));
                Thread.Sleep(1000);
                l.AppendLine("after displacement: " + WindowInfo.Title(window) + " cursor=" + Sample(100));
                Thread.Sleep(2500);
                l.AppendLine("after wait: " + WindowInfo.Title(window) + " cursor=" + Sample(100));
                Desktop.Press(Keys.Escape);
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == false, 500));
                Thread.Sleep(600); // The browser may finish its current smooth-wheel animation.
                string stopped = WindowInfo.Title(window);
                Desktop.SlideMouse(anchor + new Size(0, 80)); Thread.Sleep(500);
                l.AppendLine("after escape: " + WindowInfo.Title(window));
                Assert.Equal(stopped, WindowInfo.Title(window));
                Assert.False(view.LiveInteraction?.Scrolling ?? false);
                foreach (bool middle in new[] { true, false })
                {
                    Desktop.SlideMouse(anchor);
                    mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == true, 500));
                    Desktop.SlideMouse(anchor + new Size(0, 40)); Thread.Sleep(400);
                    mouse_event(middle ? 0x20u : 2u, 0, 0, 0, 0); Thread.Sleep(40);
                    mouse_event(middle ? 0x40u : 4u, 0, 0, 0, 0);
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == false, 500));
                    Thread.Sleep(600);
                    stopped = WindowInfo.Title(window); Thread.Sleep(400);
                    Assert.Equal(stopped, WindowInfo.Title(window));
                }
                foreach (bool leave in new[] { false, true })
                {
                    Desktop.SlideMouse(anchor);
                    mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == true, 500));
                    Desktop.SlideMouse(anchor + new Size(0, 30)); Thread.Sleep(400);
                    if (leave) Desktop.SlideMouse(new Point(view.Left - 60, view.Top + 100));
                    else view.Invoke(() => view.SetMiddleClickScroll(false));
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling != true, 500));
                    Thread.Sleep(600);
                    stopped = WindowInfo.Title(window); Thread.Sleep(400);
                    Assert.Equal(stopped, WindowInfo.Title(window));
                    if (!leave) view.Invoke(() => view.SetMiddleClickScroll(true));
                }
                Desktop.SlideMouse(anchor);
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null, 1000));
                mouse_event(0x20, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(0x40, 0, 0, 0, 0);
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.Scrolling == true, 500));
                Thread.Sleep(100); // Let the view claim Escape for this mode.
                view.Invoke(view.SuspendView);
                // A minimized/suspended view must release its temporary global Escape registration immediately.
                bool escapeFree = User32.RegisterHotKey(0, 0x575F, User32.MOD_NOREPEAT, (uint)Keys.Escape);
                if (escapeFree) User32.UnregisterHotKey(0, 0x575F);
                Assert.True(escapeFree, "Suspending the view left Escape claimed by the scrolling mode.");
            }, log);
        }
        finally { Close(browser, root); }
        string report = log.ToString();
        output.WriteLine(report);
        Assert.DoesNotContain("EXC ", report);
        var match = System.Text.RegularExpressions.Regex.Match(report, @"after displacement: AutoScroll y=(\d+)");
        Assert.True(match.Success && int.Parse(match.Groups[1].Value) > 100, report);
    }

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
            <script>let c=0,last='';const r=()=>document.title=`Probe c=${c} sel=${getSelection().toString().length} val=${i.value}`+last+` visibility=${document.visibilityState}`;
            b.onclick=()=>{c++;r()};document.addEventListener('selectionchange',r);i.oninput=r;
            addEventListener('mousemove',e=>{last=` id=${e.target.id} xy=${e.clientX},${e.clientY} css=${getComputedStyle(e.target).cursor}`;r()});setInterval(r,100);</script>
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
                        l.AppendLine($"{name}: {Sample(500)} {WindowInfo.Title(window)} {view.LiveInteraction?.HoverStatus} sharing={view.LiveInteraction?.SharesInput}");
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
                    Assert.Equal(window, User32.GetForegroundWindow());
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
        Assert.Matches(@"val=abc .*fg=True", report);
        Assert.DoesNotContain("EXC ", report);
    }

    /// <summary>
    /// Two pages, each used through its own view at the same time. The pointer goes straight from one view to the
    /// other and back, and every click, key and wheel turn reaches the page under it and only that page. Both
    /// originals are hidden on the same strip at the screen's edge, and a browser only takes a wheel turn when its
    /// own window is at the point, so the one not in use must never take what was meant for the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrowserCases))]
    public void Two_pages_used_through_two_views_at_once_each_get_only_their_own_input(string path)
    {
        const string page = """
            <!doctype html><title>NAME c=0 val= y=0</title>
            <style>body{margin:0;font:20px sans-serif;height:6000px}#b{position:fixed;left:40px;top:40px;width:220px;height:70px}
            #i{position:fixed;left:40px;top:160px;width:240px;font-size:24px}</style>
            <button id=b>Press</button><input id=i>
            <script>let c=0;const r=()=>document.title=`NAME c=${c} val=${i.value} y=${Math.round(scrollY)}`;
            b.onclick=()=>{c++;r()};i.oninput=r;onscroll=r;setInterval(r,100);</script>
            """;
        var log = new StringBuilder();
        var (browserA, windowA, rootA) = Open(page.Replace("NAME", "PageA"), "PageA", app: true, path);
        Process? browserB = null;
        string rootB = "";
        nint handleA = 0, handleB = 0;
        try
        {
            (browserB, nint windowB, rootB) = Open(page.Replace("NAME", "PageB"), "PageB", app: true, path);
            // Where the page starts under each app window's title bar, measured while the windows are still shown.
            int PageTop(nint window) => BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - Desktop.BoundsOf(window).Top;
            int topA = PageTop(windowA), topB = PageTop(windowB);
            // No lasting foreground rights for this process, as in use: that is when a view used to take the keys back.
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]); // One controller for both, as in the app.
                using var viewA = new PipWindow(windowA, windows) { AllowInteractive = true };
                using var viewB = new PipWindow(windowB, windows) { AllowInteractive = true };
                viewA.ShowOnTop();
                viewA.Location = new Point(160, 220);
                viewA.SetInteractive(true);
                viewB.ShowOnTop();
                viewB.Location = new Point(viewA.Right + 12, 220);
                viewB.SetInteractive(true);
                (handleA, handleB) = (viewA.Handle, viewB.Handle);
                Assert.True(viewA.KeepsUpdating && viewB.KeepsUpdating, $"Not both hidden: {viewA.StateDescription}; {viewB.StateDescription}");
                Point home = Desktop.CursorPosition();
                log.AppendLine("start: front " + WindowInfo.ClassName(User32.GetForegroundWindow()) + (User32.GetForegroundWindow() == viewA.Handle ? " viewA" : User32.GetForegroundWindow() == viewB.Handle ? " viewB" : User32.GetForegroundWindow() == windowA ? " A" : User32.GetForegroundWindow() == windowB ? " B" : ""));
                var driver = new Thread(() =>
                {
                    try
                    {
                        Use(viewA, windowA, topA, "ab", "A1");
                        Use(viewB, windowB, topB, "cd", "B1");
                        Use(viewA, windowA, topA, "e", "A2");
                        log.AppendLine("parked A " + Desktop.BoundsOf(windowA) + " B " + Desktop.BoundsOf(windowB));
                    }
                    catch (Exception e) { log.AppendLine("EXC " + e); }
                    finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                viewA.Close();
                viewB.Close();
            });
            log.AppendLine($"after closing: A hidden={WindowInfo.IsConcealed(windowA)} B hidden={WindowInfo.IsConcealed(windowB)}");

            void Use(PipWindow view, nint window, int top, string keys, string step)
            {
                // Straight in from wherever the pointer is, the other view included.
                Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                if (!Desktop.WaitFor(() => view.LiveInteraction is not null)) throw new InvalidOperationException("No visit started for " + step);
                Point At(Point offset)
                {
                    var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                    return new Point(area.Left + (int)Math.Round((offset.X - shown.Left) * (double)area.Width / shown.Width),
                        area.Top + (int)Math.Round((offset.Y - shown.Top) * (double)area.Height / shown.Height));
                }
                void ClickAt(Point offset)
                {
                    Desktop.SlideMouse(At(offset));
                    Thread.Sleep(100);
                    string Name(nint w) => w == windowA ? "A" : w == windowB ? "B" : w == handleA ? "viewA" : w == handleB ? "viewB" : w == 0 ? "none" : WindowInfo.ClassName(w);
                    var seen = new List<string>();
                    bool sampling = true;
                    var sampler = new Thread(() => { while (Volatile.Read(ref sampling)) { string n = Name(User32.GetForegroundWindow()); if (seen.Count == 0 || seen[^1] != n) seen.Add(n); Thread.Sleep(2); } });
                    sampler.Start();
                    Desktop.MouseButton(true); Thread.Sleep(40); Desktop.MouseButton(false);
                    Thread.Sleep(350);
                    Volatile.Write(ref sampling, false);
                    sampler.Join();
                    log.AppendLine("    foreground: " + string.Join(">", seen));
                    nint front = User32.GetForegroundWindow();
                    log.AppendLine($"  {step} click {offset}: front={(front == windowA ? "A" : front == windowB ? "B" : front == handleA ? "viewA" : front == handleB ? "viewB" : WindowInfo.ClassName(front))} title '{WindowInfo.Title(window)}' visit={view.LiveInteraction is not null}");
                }
                ClickAt(new Point(150, top + 75)); // The button.
                ClickAt(new Point(150, top + 175)); // The text box.
                foreach (char key in keys) Desktop.Press((Keys)char.ToUpperInvariant(key));
                Thread.Sleep(300);
                Desktop.SlideMouse(At(new Point(500, top + 420)));
                Thread.Sleep(150);
                mouse_event(0x0800, 0, 0, unchecked((uint)-360), 0); // Three notches down.
                Thread.Sleep(800);
                log.AppendLine($"{step}: A '{WindowInfo.Title(windowA)}' B '{WindowInfo.Title(windowB)}' outcomes {string.Join(",", view.PressOutcomes)}");
            }
        }
        finally
        {
            Close(browserA, rootA);
            if (browserB is not null) Close(browserB, rootB);
        }

        string report = log.ToString();
        output.WriteLine(report);
        Assert.DoesNotContain("EXC ", report);
        (int Clicks, string Text, int Y) Page(string step, char which)
        {
            var match = System.Text.RegularExpressions.Regex.Match(report,
                $@"{step}: .*?{(which == 'A' ? "A 'PageA" : "B 'PageB")} c=(\d+) val=(\w*) y=(\d+)'");
            Assert.True(match.Success, $"No {which} title at {step}: {report}");
            return (int.Parse(match.Groups[1].Value), match.Groups[2].Value, int.Parse(match.Groups[3].Value));
        }
        // Each page changes only while its own view is in use.
        var a1 = Page("A1", 'A'); var b1 = Page("A1", 'B');
        Assert.True(a1.Clicks == 1 && a1.Text == "ab" && a1.Y > 0, report);
        Assert.True(b1 == (0, "", 0), "Page B took input meant for A: " + report);
        var a2 = Page("B1", 'A'); var b2 = Page("B1", 'B');
        Assert.True(a2 == a1, "Page A took input meant for B: " + report);
        Assert.True(b2.Clicks == 1 && b2.Text == "cd" && b2.Y > 0, report);
        var a3 = Page("A2", 'A'); var b3 = Page("A2", 'B');
        Assert.True(a3.Clicks == 2 && a3.Text == "abe" && a3.Y > a1.Y, report);
        Assert.True(b3 == b2, "Page B took input meant for A on the way back: " + report);
        Assert.Contains("after closing: A hidden=False B hidden=False", report);
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
                User32.GetWindowThreadProcessId(window, out uint pid);
                using var measured = Process.GetProcessById((int)pid);
                using var host = Process.GetCurrentProcess();
                TimeSpan sourceCpu = measured.TotalProcessorTime, hostCpu = host.TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
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
                measured.Refresh(); host.Refresh();
                l.AppendLine($"load wall={elapsed.Elapsed.TotalSeconds:F2}s sourceCpu={(measured.TotalProcessorTime-sourceCpu).TotalMilliseconds:F1}ms hostCpu={(host.TotalProcessorTime-hostCpu).TotalMilliseconds:F1}ms; {live.HoverStatus}");
            }, log);
        }
        finally { Close(browser, root); }
        output.WriteLine(log.ToString());
        Assert.DoesNotContain("EXC ", log.ToString());
        Assert.NotNull(timing);
    }

    public static IEnumerable<object[]> DoubleClickCases() =>
        BrowserCases().SelectMany(c => new[] { new object[] { c[0], 900 }, [c[0], 1800] });

    /// <summary>
    /// A double-click on a word in a page selects that word, as with a real mouse, also with a pixel of hand jitter
    /// and in a view shown at about a quarter of the page's size (where one pixel is four in the page). A triple-click
    /// selects the line.
    /// </summary>
    /// <remarks>
    /// It used to arrive as two single clicks: stopping input sharing for the press made Windows tell the page, just
    /// after the press, that the real pointer was elsewhere (see PipInteraction.MaxRecheckWait). 1 in 10 worked then.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DoubleClickCases))]
    public void A_double_click_selects_a_word_in_a_page_used_through_a_view(string path, int width)
    {
        const string page = """
            <!doctype html><title>Dbl n=0 sel=0</title><style>body{margin:0;font:28px sans-serif}p{position:absolute;left:40px;margin:0}</style>
            <p style=top:40px>alpha bravo charlie</p><p style=top:120px>delta echo foxtrot</p>
            <script>let n=0;addEventListener('dblclick',()=>n++);setInterval(()=>document.title=`Dbl n=${n} sel=${getSelection().toString().trim().length}`,30)</script>
            """;
        var (browser, window, root) = Open(page, "Dbl", app: true, path);
        var log = new StringBuilder();
        try
        {
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 0, 100, width, 700, 0x14);
            Thread.Sleep(800);
            Visit(window, (view, at, l) =>
            {
                Rectangle client = Desktop.BoundsOf(window);
                int top = BrowserAccessibility.Read(window).First(c => c.Type == 50030).Bounds.Top - client.Top;
                for (int i = 0; i < 5; i++)
                {
                    // A click in blank space clears the selection; then a double-click on a word, some with jitter.
                    Desktop.SlideMouse(at(new Point(600, top + 400))); Thread.Sleep(80);
                    mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(300);
                    Desktop.SlideMouse(at(new Point(60 + (i % 3) * 90, top + (i % 2 == 0 ? 55 : 135)))); Thread.Sleep(150 + (i % 3) * 100);
                    string before = WindowInfo.Title(window);
                    mouse_event(2, 0, 0, 0, 0); Thread.Sleep(45); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(70 + (i % 2) * 60);
                    if (i % 2 == 1) Desktop.NudgeMouse(1, 0);
                    mouse_event(2, 0, 0, 0, 0); Thread.Sleep(45); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(350);
                    l.AppendLine($"{i}: {before} -> {WindowInfo.Title(window)} (the press waited {view.LiveInteraction?.LastRecheckWait:F1} ms)");
                }
                // A triple-click, with jitter before the third press, selects the whole line.
                Desktop.SlideMouse(at(new Point(600, top + 400))); Thread.Sleep(80);
                mouse_event(2, 0, 0, 0, 0); Thread.Sleep(30); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(300);
                Desktop.SlideMouse(at(new Point(150, top + 55))); Thread.Sleep(200);
                for (int click = 0; click < 3; click++)
                {
                    if (click == 2) Desktop.NudgeMouse(0, 1);
                    mouse_event(2, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(4, 0, 0, 0, 0); Thread.Sleep(70);
                }
                Thread.Sleep(350);
                l.AppendLine("triple: " + WindowInfo.Title(window));
            }, log);
        }
        finally { Close(browser, root); }
        output.WriteLine(log.ToString());
        for (int i = 0; i < 5; i++)
            Assert.Matches($@"(?m)^{i}: Dbl n={i} sel=0 -> Dbl n={i + 1} sel=[4-7]([^0-9]|$)", log.ToString());
        Assert.Matches(@"(?m)^triple: Dbl n=\d+ sel=19([^0-9]|$)", log.ToString()); // "alpha bravo charlie"
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
                    Assert.Equal(window, User32.GetForegroundWindow());
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
