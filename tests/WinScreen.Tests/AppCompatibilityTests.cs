using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Real apps of other UI frameworks used through a view: WPF (PowerShell ISE), Firefox, Electron (VS Code) and Qt
/// (VLC). Each checks what the app actually did. The portable apps live in the test folder's test-apps directory;
/// a test whose app isn't there returns without doing anything. Every app runs with a throwaway profile and every
/// process it starts is closed.
/// </summary>
[Trait("Category", "Interactive")]
public sealed partial class AppCompatibilityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Apps => Path.Combine(AppContext.BaseDirectory, "test-apps");

    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

    private static void Click(Point at, bool right = false, int times = 1)
    {
        Desktop.SlideMouse(at); Thread.Sleep(150);
        for (int i = 0; i < times; i++) { mouse_event(right ? 8u : 2u, 0, 0, 0, 0); Thread.Sleep(40); mouse_event(right ? 16u : 4u, 0, 0, 0, 0); Thread.Sleep(60); }
        Thread.Sleep(300);
    }

    private static void Drag(Point from, Point to)
    {
        Desktop.SlideMouse(from); Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, 0); Thread.Sleep(60);
        for (int i = 1; i <= 8; i++) { Desktop.SlideMouse(new Point(from.X + (to.X - from.X) * i / 8, from.Y + (to.Y - from.Y) * i / 8)); Thread.Sleep(50); }
        mouse_event(4, 0, 0, 0, 0); Thread.Sleep(300);
    }

    /// <summary>Visible top-level windows of a process other than the main one: its menus and popups.</summary>
    private static List<nint> Popups(int process, nint main)
    {
        var found = new List<nint>();
        for (nint w = User32.GetWindow(User32.GetDesktopWindow(), 5); w != 0; w = User32.GetWindow(w, 2))
        {
            User32.GetWindowThreadProcessId(w, out uint pid);
            if (pid == process && w != main && User32.IsWindowVisible(w) && Desktop.BoundsOf(w).Width > 8) found.Add(w);
        }
        return found;
    }

    /// <summary>
    /// Opens a view of the window, starts a visit, and runs the steps with a mapping from a point in the window
    /// (window-relative, physical) to the view.
    /// </summary>
    private static void Visit(nint window, Action<PipWindow, Func<Point, Point>> steps, StringBuilder log, bool? enhancedHover = null)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Point home = Desktop.CursorPosition();
            Desktop.MoveMouse(new Point(860, 180));
            using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
            var problems = new List<string>();
            view.Problem += problems.Add;
            view.ShowOnTop();
            view.Location = new Point(900, 200);
            view.SetInteractive(true);
            view.SetEnhancedHover(enhancedHover ?? Environment.GetEnvironmentVariable("WINSCREEN_TEST_ENHANCED_HOVER") == "1");
            Exception? failure = null;
            PipInteraction.DiagnoseInput = true;
            var driver = new Thread(() =>
            {
                try
                {
                    Assert.True(view.KeepsUpdating, "Not hidden: " + string.Join("; ", problems));
                    Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit started.");
                    Thread.Sleep(300);
                    log.AppendLine("Enhanced hover: " + view.LiveInteraction?.HoverStatus);
                    Point ToView(Point offset)
                    {
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        return new Point(area.Left + (int)Math.Round((offset.X - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((offset.Y - shown.Top) * (double)area.Height / shown.Height));
                    }
                    log.AppendLine($"window {Desktop.BoundsOf(window)} view {view.Bounds}");
                    steps(view, ToView);
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    Desktop.MouseButton(false);
                    log.AppendLine("outcomes " + string.Join(",", view.PressOutcomes) + "; ended '" + view.LastVisitEnd + "'; problems " + string.Join("; ", problems));
                    foreach (string entry in view.LiveInteraction?.InputDiagnostics ?? []) log.AppendLine(entry);
                    Desktop.MoveMouse(home);
                    PipInteraction.DiagnoseInput = false;
                }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            if (!view.IsDisposed) view.Close();
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure + "\n" + log);
        });
    }

    private static nint WaitForWindow(Process process, Func<string, bool> title, int milliseconds = 30000)
    {
        nint window = 0;
        Desktop.WaitFor(() =>
        {
            foreach (int id in ProcessTree(process.Id))
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    if (p.MainWindowHandle != 0 && User32.IsWindowVisible(p.MainWindowHandle) && title(WindowInfo.Title(p.MainWindowHandle)))
                        window = p.MainWindowHandle;
                }
                catch (ArgumentException) { }
            }
            return window != 0;
        }, milliseconds);
        return window;
    }

    /// <summary>The process and everything it started, so they can all be closed. A quick system snapshot.</summary>
    private static List<int> ProcessTree(int root)
    {
        var parents = new List<(int Id, int Parent)>();
        nint snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == -1) return [root];
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                parents.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }
        finally { CloseHandle(snapshot); }
        var ids = new List<int> { root };
        for (int i = 0; i < ids.Count; i++) ids.AddRange(parents.Where(p => p.Parent == ids[i] && !ids.Contains(p.Id)).Select(p => p.Id));
        return ids;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size; public uint Usage, ProcessId; public nint DefaultHeap; public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    private static void CloseTree(Process process)
    {
        foreach (int id in ProcessTree(process.Id).AsEnumerable().Reverse())
        {
            try { using var p = Process.GetProcessById(id); p.Kill(); p.WaitForExit(3000); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static void Settle(int milliseconds) { Thread.Sleep(milliseconds); }

    private static Bitmap Picture(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return bitmap;
    }

    /// <summary>Going down one column from a point, the last row of the block that differs between the pictures.</summary>
    private static int MenuBottom(Bitmap before, Bitmap after, int x, int y)
    {
        int last = y, unchanged = 0;
        for (; y < Math.Min(before.Height, after.Height) && unchanged <= 8; y++)
            if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb()) { last = y; unchanged = 0; }
            else unchanged++;
        return last;
    }

    /// <summary>The file as saved so far; empty while the app is still writing it.</summary>
    private static string Saved(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }

    private const string Page = """
        <!doctype html><title>Probe c=0 sel=0 val= s=0</title>
        <style>body{margin:0;font:20px sans-serif;height:3000px}#t{position:absolute;left:20px;top:20px;width:400px}
        #b{position:absolute;left:520px;top:200px;width:200px;height:80px}#i{position:absolute;left:520px;top:350px;width:250px;font-size:24px}</style>
        <p id=t>Someword ordinary text to select with the pointer. It runs over several lines so a drag can cover a good part of it here.</p>
        <button id=b>Press</button><input id=i>
        <script>let c=0;const r=()=>document.title=`Probe c=${c} sel=${getSelection().toString().length} val=${i.value} s=${Math.round(scrollY)} at=${mozInnerScreenX},${mozInnerScreenY}`;
        b.onclick=()=>{c++;r()};document.addEventListener('selectionchange',r);i.oninput=r;addEventListener('scroll',r);setInterval(r,100);</script>
        """;

    /// <remarks>
    /// A new profile opens with a first-run dialog that covers the page and takes every click, real ones included; the
    /// throwaway profile turns it off with Firefox's own preference. (Phase 3 measured with that dialog open.)
    /// </remarks>
    [Fact]
    public void Firefox_takes_clicks_double_clicks_selection_typing_wheel_and_its_menu_through_a_view()
    {
        string firefox = Path.Combine(Apps, "firefox", "firefox.exe");
        if (!File.Exists(firefox)) { Skipped.Because("Firefox isn't in test-apps."); return; }
        string root = Directory.CreateTempSubdirectory("winscreen-firefox").FullName;
        string profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
        File.WriteAllText(Path.Combine(profile, "user.js"), """
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.aboutwelcome.enabled", false);
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
            user_pref("browser.startup.homepage_override.mstone", "ignore");
            user_pref("app.update.enabled", false);
            user_pref("browser.tabs.warnOnClose", false);
            user_pref("browser.preonboarding.enabled", false);
            user_pref("browser.bypassAutoTriggerActions", true);
            user_pref("browser.shell.skipDefaultBrowserCheckOnFirstRun", true);
            user_pref("browser.shell.didSkipDefaultBrowserCheckOnFirstRun", true);
            user_pref("browser.startup.upgradeDialog.enabled", false);
            user_pref("browser.messaging-system.whatsNewPanel.enabled", false);
            user_pref("browser.newtabpage.activity-stream.asrouter.userprefs.cfr.features", false);
            user_pref("browser.newtabpage.activity-stream.asrouter.userprefs.cfr.addons", false);
            user_pref("browser.newtabpage.activity-stream.asrouter.providers.cfr", "null");
            user_pref("browser.newtabpage.activity-stream.asrouter.providers.message-groups", "null");
            user_pref("browser.newtabpage.activity-stream.asrouter.providers.messaging-experiments", "null");
            user_pref("messaging-system.rsexperimentloader.enabled", false);
            user_pref("app.normandy.enabled", false);
            user_pref("browser.tabs.firefox-view", false);
            """);
        File.WriteAllText(Path.Combine(root, "page.html"), Page);
        var start = new ProcessStartInfo(firefox) { UseShellExecute = false };
        foreach (string arg in new[] { "-profile", profile, "-no-remote", "-new-instance", "-width", "900", "-height", "700", new Uri(Path.Combine(root, "page.html")).AbsoluteUri })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            // The process started here hands over to another and leaves, so its tree doesn't lead to the window: look
            // for a Firefox window of this test's own copy.
            nint window = 0;
            Desktop.WaitFor(() => (window = FirefoxWindow(firefox, "Probe")) != 0, 40000);
            Assert.True(window != 0, "Firefox didn't open the page.");
            User32.SetWindowPos(window, 0, 120, 80, 900, 700, 0x14);
            Settle(1500);
            string Title() => WindowInfo.Title(window);
            // The page's top-left inside the window, as the page itself reports it (its title ends "at=x,y").
            Rectangle home = Desktop.BoundsOf(window);
            string[] at0 = Title().Split("at=")[1].Split(' ')[0].Split(',');
            Point page = new((int)double.Parse(at0[0], System.Globalization.CultureInfo.InvariantCulture) - home.Left,
                (int)double.Parse(at0[1], System.Globalization.CultureInfo.InvariantCulture) - home.Top);
            log.AppendLine($"page offset {page}");
            User32.GetWindowThreadProcessId(window, out uint browserProcess);
            int owner = (int)browserProcess; // Its menus belong to the process that has the window.
            Visit(window, (view, at) =>
            {
                Point P(int x, int y) => at(new Point(page.X + x, page.Y + y));
                Click(P(600, 230), times: 1); Click(P(600, 230)); Click(P(600, 230));
                Assert.True(Desktop.WaitFor(() => Title().Contains("c=3 "), 2000), "Clicks: " + Title());
                Click(P(45, 32), times: 2); // A double-click selects a word.
                Assert.True(Desktop.WaitFor(() => Title().Contains("sel=8 ") || Title().Contains("sel=9 "), 2000), "Double-click: " + Title());
                Drag(P(150, 52), P(380, 97)); // From the first line, past the word just selected, to the third.
                Assert.True(Desktop.WaitFor(() => int.Parse(Title().Split("sel=")[1].Split(' ')[0]) > 25, 2000), "Drag selection: " + Title());
                Click(P(600, 365));
                Desktop.Press(Keys.A); Desktop.Press(Keys.B); Desktop.Press(Keys.C);
                Assert.True(Desktop.WaitFor(() => Title().Contains("val=abc"), 2000), "Typing: " + Title());
                Desktop.SlideMouse(P(300, 300)); Thread.Sleep(150);
                for (int i = 0; i < 3; i++) { mouse_event(0x800, 0, 0, unchecked((uint)-120), 0); Thread.Sleep(100); }
                Assert.True(Desktop.WaitFor(() => !Title().Contains(" s=0"), 2000), "Wheel: " + Title());
                int before = Popups(owner, window).Count;
                Point menuAt = P(300, 250);
                Click(menuAt, right: true);
                List<nint> menus = [];
                Assert.True(Desktop.WaitFor(() => (menus = Popups(owner, window)).Count > before, 2000), "No context menu.");
                Rectangle menu = Desktop.BoundsOf(menus[^1]);
                log.AppendLine($"menu {menu} pointer {Desktop.CursorPosition()}");
                Assert.True(Rectangle.Inflate(menu, 60, 60).Contains(Desktop.CursorPosition()), $"The menu isn't by the pointer: {menu}.");
                Desktop.Press(Keys.Escape);
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseTree(process);
            CloseCopy(firefox);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>A visible Firefox window of the given copy whose title starts so.</summary>
    private static nint FirefoxWindow(string executable, string title)
    {
        for (nint w = User32.GetWindow(User32.GetDesktopWindow(), 5); w != 0; w = User32.GetWindow(w, 2))
        {
            if (!User32.IsWindowVisible(w) || WindowInfo.ClassName(w) != "MozillaWindowClass" || !WindowInfo.Title(w).StartsWith(title)) continue;
            User32.GetWindowThreadProcessId(w, out uint pid);
            if (string.Equals(ImageOf((int)pid), executable, StringComparison.OrdinalIgnoreCase)) return w;
        }
        return 0;
    }

    /// <summary>Closes every process running this executable: the tests' own portable copy, nobody else's.</summary>
    private static void CloseCopy(string executable)
    {
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (p)
                try { if (string.Equals(ImageOf(p.Id), executable, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static string ImageOf(int process)
    {
        nint handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process);
        if (handle == 0) return "";
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder path, ref int size);

    [Fact]
    public void An_electron_editor_takes_a_double_click_typing_and_its_context_menu_through_a_view()
    {
        string code = Path.Combine(Apps, "vscode", "Code.exe");
        if (!File.Exists(code)) { Skipped.Because("VS Code isn't in test-apps."); return; }
        string root = Directory.CreateTempSubdirectory("winscreen-electron").FullName;
        string file = Path.Combine(root, "probe.txt");
        File.WriteAllText(file, "alpha beta gamma\n");
        File.WriteAllText(Directory.CreateDirectory(Path.Combine(root, "data", "User")).FullName + "\\settings.json",
            """{"workbench.startupEditor":"none","security.workspace.trust.enabled":false,"update.mode":"none","telemetry.telemetryLevel":"off","window.restoreWindows":"none","window.menuStyle":"native"}""");
        var start = new ProcessStartInfo(code) { UseShellExecute = false };
        foreach (string arg in new[] { "--user-data-dir", Path.Combine(root, "data"), "--extensions-dir", Path.Combine(root, "ext"),
                     "--disable-extensions", "--skip-welcome", "--skip-release-notes", "--disable-workspace-trust", "--new-window",
                     "--force-renderer-accessibility", file })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("probe.txt"));
            Assert.True(window != 0, "The editor didn't open the file.");
            User32.ShowWindow(window, 9); // It may open maximized.
            Thread.Sleep(300);
            User32.SetWindowPos(window, 0, 120, 80, 1000, 700, 0x14);
            Settle(2500);
            // Its editor doesn't show its lines to this test's accessibility client, so aim by its fixed layout: at 100%
            // the first line's text starts 121 px in and is centred 99 px down the client area (title bar, tab and
            // path rows above it; measured 9 Nov). The saved file shows whether the press landed.
            var client = new NativePoint();
            ClientToScreen(window, ref client);
            Rectangle shownAt = Desktop.BoundsOf(window);
            double scale = GetDpiForWindow(window) / 96.0;
            Point text = new(client.X - shownAt.Left + (int)Math.Round(135 * scale), client.Y - shownAt.Top + (int)Math.Round(99 * scale));
            log.AppendLine($"first word at {text} (scale {scale})");
            Visit(window, (view, at) =>
            {
                // A double-click selects the first word; typing replaces it. A right-click opens the editor's context
                // menu, which it draws inside its own window, and a click on its last item (Command Palette) opens the
                // palette, where Transform to Uppercase changes the word the right-click selected (the one now under
                // the pointer). The saved file shows each step happened.
                Click(at(text), times: 2);
                Desktop.Press(Keys.Z);
                Rectangle area = view.RectangleToScreen(view.ClientRectangle);
                Desktop.SlideMouse(at(text)); Thread.Sleep(300);
                using Bitmap before = Picture(area);
                Click(at(text), right: true);
                Thread.Sleep(300);
                using Bitmap after = Picture(area);
                // The menu opens down and right of the pointer: follow it down from there until the picture is unchanged.
                double shown = (double)area.Width / Desktop.BoundsOf(window).Width;
                Point pointer = at(text) - new Size(area.Location);
                int column = pointer.X + (int)Math.Round(40 * scale * shown), bottom = MenuBottom(before, after, column, pointer.Y + 2);
                log.AppendLine($"menu from {pointer} down to {bottom} in the view");
                Assert.True(bottom - pointer.Y > 40, "No context menu appeared in the picture.");
                // Its last item's row: VS Code's menu rows are 26 px high at 100%, with 4 px of padding below them.
                Click(new Point(area.Left + column, area.Top + bottom - (int)Math.Round(17 * scale * shown)));
                foreach (char c in "transform to uppercase") Desktop.Press(c == ' ' ? Keys.Space : (Keys)char.ToUpperInvariant(c));
                Thread.Sleep(400);
                Desktop.Press(Keys.Enter);
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                Assert.True(Desktop.WaitFor(() => Saved(file).StartsWith("z BETA gamma"), 3000),
                    "After the double-click, Z, the menu's Command Palette, Transform to Uppercase and Ctrl+S the file is: '" + Saved(file) + "'");
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseTree(process);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_qt_app_opens_its_menus_by_the_pointer_and_a_dialog_shows_the_original()
    {
        string vlc = Path.Combine(Apps, "vlc", "vlc.exe");
        if (!File.Exists(vlc)) { Skipped.Because("VLC isn't in test-apps."); return; }
        var start = new ProcessStartInfo(vlc) { UseShellExecute = false };
        foreach (string arg in new[] { "--ignore-config", "--no-qt-privacy-ask", "--no-qt-updates-notif", "--no-qt-recentplay", "--qt-continue=0" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("VLC"));
            Assert.True(window != 0, "VLC didn't open.");
            User32.SetWindowPos(window, 0, 120, 80, 800, 560, 0x14);
            Settle(1500);
            Visit(window, (view, at) =>
            {
                // VLC tells accessibility nothing, so places come from its client area: the menu bar is its top row.
                Rectangle bounds = Desktop.BoundsOf(window);
                var client = new NativePoint();
                ClientToScreen(window, ref client);
                Point origin = new(client.X - bounds.Left, client.Y - bounds.Top);
                // A right-click on the video area opens its menu by the pointer; Escape closes it.
                int before = Popups(process.Id, window).Count;
                Click(at(new Point(origin.X + 300, origin.Y + 200)), right: true);
                List<nint> menus = [];
                Assert.True(Desktop.WaitFor(() => (menus = Popups(process.Id, window)).Count > before, 2000), "No context menu.");
                Rectangle menu = Desktop.BoundsOf(menus[^1]);
                Assert.True(Rectangle.Inflate(menu, 80, 80).Contains(Desktop.CursorPosition()), $"The menu isn't by the pointer: {menu}, pointer {Desktop.CursorPosition()}.");
                Desktop.Press(Keys.Escape);
                Assert.True(Desktop.WaitFor(() => Popups(process.Id, window).Count == before, 2000), "The menu stayed open.");
                // Media, the first menu: its last item is Quit, chosen with a real click on the real menu.
                Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit after the menu.");
                Click(at(new Point(origin.X + 22, origin.Y + 10)));
                Assert.True(Desktop.WaitFor(() => (menus = Popups(process.Id, window)).Count > before, 2000), "The Media menu didn't open.");
                menu = Desktop.BoundsOf(menus[^1]);
                log.AppendLine($"media menu {menu}");
                Click(new Point(menu.Left + 40, menu.Bottom - 14));
                Assert.Contains(PressOutcome.Popup, view.PressOutcomes);
                Assert.True(process.WaitForExit(5000), "Quit in the menu didn't close VLC.");
                Assert.True(Desktop.WaitFor(() => view.IsDisposed || !view.Visible, 4000), "The view outlived its app.");
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseTree(process);
        }
    }

    /// <summary>
    /// WPF windows manage their own extended styles and drop the one that makes a window see-through, so WinScreen
    /// can't hide them, keep them updating or use them directly. That is said plainly, and the app is left as it was.
    /// </summary>
    [Fact]
    public void A_wpf_app_that_refuses_to_be_hidden_is_told_plainly_and_left_alone()
    {
        string ise = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell_ise.exe");
        if (!File.Exists(ise)) { Skipped.Because("PowerShell ISE isn't installed."); return; }
        var start = new ProcessStartInfo(ise) { UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile");
        using var process = Process.Start(start)!;
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("PowerShell ISE"), 40000);
            Assert.True(window != 0, "PowerShell ISE didn't open.");
            Settle(1500);
            Rectangle before = Desktop.BoundsOf(window);
            long style = Desktop.ExStyle(window);
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]);
                using var view = new PipWindow(window, windows) { AllowInteractive = true };
                var problems = new List<string>();
                view.Problem += problems.Add;
                view.ShowOnTop();
                view.SetInteractive(true);
                Assert.Contains(Localization.UiText.T("errors.opacity.refused"), problems);
                // Nothing is recorded as hidden: no recovery record or watcher for a window that was never hidden.
                Assert.False(view.KeepsUpdating);
                Assert.False(windows.IsHidden(window));
                Assert.False(windows.NeedsRecoveryWatcher);
                Assert.Null(view.LiveInteraction);
                view.Close();
            });
            Assert.Equal(before, Desktop.BoundsOf(window));
            Assert.Equal(style, Desktop.ExStyle(window));
            Assert.False(WindowInfo.IsConcealed(window));
        }
        finally { CloseTree(process); }
    }
}
