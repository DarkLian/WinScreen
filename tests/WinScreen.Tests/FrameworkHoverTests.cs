using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Enhanced hover in apps other than Chrome, turned on from Settings rather than from the view's menu.</summary>
[Trait("Category", "Interactive")]
public sealed class FrameworkHoverTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [NativeHoverFact]
    public void Firefox_keeps_css_hover_when_chosen_in_settings() =>
        Run(Path.Combine("firefox", "firefox.exe"), new PipHoverPreferences { ChooseForMe = false, Firefox = true }, PipHoverApp.Firefox);

    [NativeHoverFact]
    public void An_electron_app_keeps_css_hover_when_chosen_in_settings() =>
        Run(Path.Combine("vscode", "Code.exe"), new PipHoverPreferences { ChooseForMe = false, Electron = true }, PipHoverApp.Electron);

    /// <summary>
    /// Any Electron app, not just one WinScreen knows by name: Electron's own download, running a page of its own
    /// under a name nothing in WinScreen mentions, is recognized by its folder alone.
    /// </summary>
    [NativeHoverFact]
    public void An_electron_app_known_only_by_its_folder_keeps_css_hover() =>
        Run(Path.Combine("electron", ElectronFixture), new PipHoverPreferences { ChooseForMe = false, Electron = true }, PipHoverApp.Electron);

    private const string ElectronFixture = "HoverFixture.exe";

    /// <summary>Puts a small app into Electron's "resources\app" folder and gives Electron another name.</summary>
    private static bool PrepareElectronFixture(string exe)
    {
        string folder = Path.GetDirectoryName(exe)!, electron = Path.Combine(folder, "electron.exe");
        if (!File.Exists(electron)) return false;
        string app = Directory.CreateDirectory(Path.Combine(folder, "resources", "app")).FullName;
        File.WriteAllText(Path.Combine(app, "package.json"), """{"name":"hover-fixture","version":"1.0.0","main":"main.js"}""");
        File.WriteAllText(Path.Combine(app, "main.js"), """
            const {app, BrowserWindow} = require('electron');
            const page = process.argv.find(a => a.endsWith('.html'));
            const data = process.argv.find(a => a.startsWith('--fixture-data='));
            if (data) app.setPath('userData', data.slice('--fixture-data='.length));
            app.whenReady().then(() => new BrowserWindow({width: 1000, height: 700}).loadFile(page));
            app.on('window-all-closed', () => app.quit());
            """);
        if (!File.Exists(exe) || new FileInfo(exe).Length != new FileInfo(electron).Length) File.Copy(electron, exe, true);
        return true;
    }

    private void Run(string app, PipHoverPreferences preferences, PipHoverApp kind)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "test-apps", app);
        bool fixture = Path.GetFileName(exe) == ElectronFixture;
        if (fixture ? !PrepareElectronFixture(exe) : !File.Exists(exe)) { Skipped.Because($"{app} isn't in test-apps."); return; } // Optional downloads.
        bool electron = kind == PipHoverApp.Electron && !fixture;
        string root = Directory.CreateTempSubdirectory("winscreen-framework-hover").FullName;
        const string html = """
            <!doctype html><title>FrameworkHoverDocument</title><style>body{margin:0}section{position:absolute;top:0;width:40vw;height:80vh;background:red}#a{left:0}#b{left:50vw}section:hover{background:green}.preview{display:none}section:hover .preview{display:block}</style>
            <section id=a>A<div class=preview>Preview A</div></section><section id=b>B<div class=preview>Dropdown B</div></section>
            <script>let dom='none',enter=0,leave=0;for(const x of[a,b]){x.onmouseenter=()=>{dom=x.id;enter++};x.onmouseleave=()=>{dom='none';leave++}}
            const api=typeof acquireVsCodeApi==='function'?acquireVsCodeApi():null;
            setInterval(()=>{let x=document.querySelector('section:hover'),s=`FrameworkHover css=${x?.id||'none'} dom=${dom} preview=${x?getComputedStyle(x.lastChild).display:'none'} visibility=${document.visibilityState} enter=${enter} leave=${leave}`;if(api)api.postMessage(s);else document.title=s},50)</script>
            """;
        var launch = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (electron)
        {
            string extension = Directory.CreateDirectory(Path.Combine(root, "extension")).FullName;
            File.WriteAllText(Path.Combine(extension, "package.json"), """{"name":"winscreen-hover-fixture","publisher":"winscreen-test","version":"1.0.0","engines":{"vscode":"^1.80.0"},"activationEvents":["*"],"main":"./extension.js","capabilities":{"untrustedWorkspaces":{"supported":true}}}""");
            File.WriteAllText(Path.Combine(extension, "extension.js"), "const vscode=require('vscode');exports.activate=()=>{const p=vscode.window.createWebviewPanel('hover','FrameworkHover',vscode.ViewColumn.One,{enableScripts:true,retainContextWhenHidden:true});p.webview.onDidReceiveMessage(s=>p.title=s);p.webview.html=" + System.Text.Json.JsonSerializer.Serialize(html) + ";};");
            string settings = Directory.CreateDirectory(Path.Combine(root, "data", "User")).FullName;
            File.WriteAllText(Path.Combine(settings, "settings.json"), """{"workbench.startupEditor":"none","security.workspace.trust.enabled":false,"update.mode":"none","telemetry.telemetryLevel":"off","window.restoreWindows":"none"}""");
            foreach (string arg in new[] { "--user-data-dir=" + Path.Combine(root, "data"), "--extensions-dir=" + Path.Combine(root, "ext"), "--extensionDevelopmentPath=" + extension, "--disable-extensions", "--skip-welcome", "--skip-release-notes", "--disable-workspace-trust", "--force-renderer-accessibility", "--new-window" })
                launch.ArgumentList.Add(arg);
        }
        else if (fixture)
        {
            string page = Path.Combine(root, "page.html");
            File.WriteAllText(page, html);
            launch.ArgumentList.Add("--fixture-data=" + Path.Combine(root, "data"));
            launch.ArgumentList.Add(page);
        }
        else
        {
            string profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            File.WriteAllText(Path.Combine(profile, "user.js"), """user_pref("browser.preonboarding.enabled",false);user_pref("browser.bypassAutoTriggerActions",true);user_pref("browser.aboutwelcome.enabled",false);user_pref("browser.shell.checkDefaultBrowser",false);user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification",true);user_pref("browser.startup.homepage_override.mstone","ignore");""");
            string page = Path.Combine(root, "page.html");
            File.WriteAllText(page, html);
            foreach (string arg in new[] { "-no-remote", "-profile", profile, "-new-window", new Uri(page).AbsoluteUri })
                launch.ArgumentList.Add(arg);
        }

        var log = new StringBuilder();
        PipHoverPreferences before = PipHoverApps.Preferences;
        using var process = Process.Start(launch)!;
        nint window = 0;
        try
        {
            Assert.True(Desktop.WaitFor(() =>
            {
                foreach (int pid in Owned(root, Path.GetFileName(exe)))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p.MainWindowHandle != 0 && WindowInfo.Title(p.MainWindowHandle).Contains("FrameworkHover css=")) window = p.MainWindowHandle;
                    }
                    catch (ArgumentException) { }
                }
                return window != 0;
            }, 40000), "The test app's page did not open.");
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 160, 100, 1000, 700, 0x14);
            Thread.Sleep(1200);

            WindowMenuBuilderTests.OnUiThread(() =>
            {
                // Off in Settings: a view of this app starts without it.
                PipHoverApps.Preferences = new PipHoverPreferences { ChooseForMe = false };
                Assert.False(PipEnhancedHover.StartsOn(window));
                PipHoverApps.Preferences = preferences;
                Assert.Equal(kind, PipEnhancedHover.Describe(window).App);
                Assert.True(PipEnhancedHover.StartsOn(window));

                using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = true };
                view.ShowOnTop();
                view.Location = new Point(1000, 220);
                view.SetInteractive(true); // Enhanced hover is never set here: it comes from Settings.
                Exception? failure = null;
                Point home = Desktop.CursorPosition();
                var driver = new Thread(() =>
                {
                    try
                    {
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No direct-input visit started.");
                        Assert.True(view.LiveInteraction!.EnhancedHover, "The view did not take Enhanced hover from Settings.");
                        Point Map(Point offset)
                        {
                            var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                            return new Point(area.Left + (int)Math.Round((offset.X - shown.Left) * (double)area.Width / shown.Width),
                                area.Top + (int)Math.Round((offset.Y - shown.Top) * (double)area.Height / shown.Height));
                        }
                        string Title() => WindowInfo.Title(window);
                        string Leaves() => Regex.Match(Title(), @"leave=\d+").Value;
                        string Status() => view.LiveInteraction?.HoverStatus ?? "no visit";
                        // What the person sees: the view's own picture, a little above the pointer. Red at rest, green hovered.
                        Color Shown(Point at)
                        {
                            using var pixel = new Bitmap(1, 1);
                            using (Graphics g = Graphics.FromImage(pixel))
                                g.CopyFromScreen(at.X, at.Y - 12, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
                            return pixel.GetPixel(0, 0);
                        }
                        void State(string id) => Assert.True(Desktop.WaitFor(() => Title().Contains($"css={id} dom={id} preview={(id == "none" ? "none" : "block")}"), 4000), Title() + "; " + Status());

                        // The page sits somewhere inside the app's own frame; find each element by what the page reports.
                        var found = new Dictionary<string, Point>();
                        for (int y = 200; y <= 500 && found.Count < 2; y += 150)
                        {
                            for (int x = 150; x <= 900 && found.Count < 2; x += 100)
                            {
                                Point candidate = Map(new Point(x, y));
                                Desktop.SlideMouse(candidate);
                                Desktop.NudgeMouse(1, 0);
                                // A page that believes it is out of sight reports only once a second.
                                Thread.Sleep(Title().Contains("visibility=hidden") ? 1300 : 250);
                                // Two readings that agree: the title can still hold the element the pointer just left.
                                Match seen = Regex.Match(Title(), @"css=(a|b) dom=\1");
                                Thread.Sleep(90);
                                if (seen.Success && Regex.Match(Title(), @"css=(a|b) dom=\1").Value == seen.Value) found.TryAdd(seen.Groups[1].Value, candidate);
                                log.AppendLine($"scan {x},{y} at {candidate}: {Title()}");
                            }
                        }
                        Assert.True(found.Count == 2, "Both elements were not found: " + Title() + "; " + Status());

                        foreach (string name in new[] { "a", "b", "a" })
                        {
                            Desktop.SlideMouse(found[name]);
                            Desktop.NudgeMouse(2, 0);
                            State(name);
                            string leaves = Leaves();
                            for (int sample = 0; sample < 10; sample++)
                            {
                                Thread.Sleep(500);
                                Assert.True(Title().Contains($"css={name} dom={name} preview=block"), Title() + "; " + Status());
                                Assert.Equal(leaves, Leaves());
                            }
                            Assert.StartsWith("active", Status());
                            Color shown = Shown(found[name]);
                            log.AppendLine($"resting {name}: shown={shown.R},{shown.G},{shown.B} {Title()}; {Status()}");
                            Assert.True(shown.G > 90 && shown.R < 60, $"The view does not show the hover effect: {shown}");
                        }

                        Desktop.SlideMouse(new Point(view.Left - 50, view.Top + 100));
                        State("none");
                        Thread.Sleep(300);
                        Color rest = Shown(found["a"]);
                        log.AppendLine($"left: shown={rest.R},{rest.G},{rest.B} {Title()}");
                        Assert.True(rest.R > 180 && rest.G < 60, $"The view still shows the hover effect: {rest}");
                        // Back in, a little away from where it left: an app can ignore a move to the very same point.
                        Desktop.SlideMouse(new Point(found["a"].X + 6, found["a"].Y + 4));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit started on the way back.");
                        Desktop.NudgeMouse(2, 0);
                        State("a");
                        view.Invoke(() => view.SetEnhancedHover(false));
                        State("none");
                        log.AppendLine("turned off: " + Title());
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
            PipHoverApps.Preferences = before;
            output.WriteLine(log.ToString());
            foreach (int pid in Owned(root, Path.GetFileName(exe)))
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(true); p.WaitForExit(5000); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>This run's processes only: its own folder is in their command lines.</summary>
    private static int[] Owned(string root, string name)
    {
        var ids = new List<int>();
        using var search = new System.Management.ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='{name}'");
        foreach (var item in search.Get())
        {
            using (item)
                if (item["CommandLine"] is string line && line.Contains(root, StringComparison.OrdinalIgnoreCase)) ids.Add(Convert.ToInt32(item["ProcessId"]));
        }
        return ids.ToArray();
    }
}
