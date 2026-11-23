using System.Diagnostics;
using Microsoft.Win32;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// The real WinScreen.exe with Run as administrator and its helper (10-2): it runs as the person and starts an elevated
/// helper; a command for an administrator app's window is the helper's; a kill of either copy puts back only that
/// copy's windows, and the helper never outlives the ordinary copy. Needs a PC where Windows elevates without asking
/// (the owner's), and runs from a test process that isn't elevated.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class AdministratorHelperEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "WinScreenTest-" + Path.GetRandomFileName());
    private readonly List<int> _elevated = [];
    private Process? _app;

    private static string Program =>
        Environment.GetEnvironmentVariable("WINSCREEN_EXE") ?? Path.Combine(AppContext.BaseDirectory, "WinScreen.exe");

    private static bool ElevatesWithoutAsking =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 5) is 0;

    private Process Start()
    {
        Setup.QuietNotices(_data);
        var start = new ProcessStartInfo(Program) { UseShellExecute = false };
        start.Environment[AppRoles.DataFolderVariable] = _data;
        return Process.Start(start)!;
    }

    private int Run(string command, string window)
    {
        var start = new ProcessStartInfo(Program) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment[AppRoles.DataFolderVariable] = _data;
        foreach (string argument in new[] { "--run", command, "--window", window }) start.ArgumentList.Add(argument);
        using Process cli = Process.Start(start)!;
        Assert.True(cli.WaitForExit(15000), "The command line didn't finish.");
        return cli.ExitCode;
    }

    /// <summary>Starts something as administrator: Windows doesn't ask on this PC.</summary>
    private static void Elevated(string program, string arguments)
    {
        Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
    }

    private static void KillElevated(uint process) => Elevated("taskkill.exe", $"/PID {process} /F");

    /// <summary>A test window in a program running as administrator.</summary>
    private nint ElevatedTarget(string title, Rectangle bounds, bool framed = false)
    {
        Elevated(Path.Combine(AppContext.BaseDirectory, "WinScreen.TestTarget.exe"),
            $"--left {bounds.Left} --top {bounds.Top} --width {bounds.Width} --height {bounds.Height} --colors 1E64C8 --title \"{title}\"" + (framed ? " --alone-for 900000" : ""));
        nint window = 0;
        Assert.True(Desktop.WaitFor(() => (window = Desktop.FindWindow(null, title)) != 0, 10000), "The administrator test window didn't open.");
        User32.GetWindowThreadProcessId(window, out uint process);
        Assert.True(ProcessInfo.IsElevated(process), "The test window isn't running as administrator.");
        _elevated.Add((int)process);
        return window;
    }

    private static uint HelperProcess()
    {
        nint window = Desktop.FindWindow(null, AppRoles.HelperHostTitle);
        if (window == 0) return 0;
        User32.GetWindowThreadProcessId(window, out uint process);
        return process;
    }

    private string Log(string file) => File.Exists(Path.Combine(_data, file)) ? ReadShared(Path.Combine(_data, file)) : "";

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private static bool Topmost(nint window) => ((long)User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;

    [Fact]
    public void The_helper_takes_administrator_windows_and_each_copy_puts_back_only_its_own()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, RunAsAdministrator = true }.Save(Path.Combine(_data, "settings.json"));
        using var ordinaryTarget = new TargetProcess(new Rectangle(140, 160, 420, 300), title: "WinScreen helper test ordinary");
        nint adminTarget = ElevatedTarget("WinScreen helper test admin", new Rectangle(620, 160, 420, 300));

        // It runs as the person, and starts its helper, elevated, which keeps its own log in the same data folder.
        _app = Start();
        Assert.True(Desktop.WaitFor(() => Desktop.FindWindow(null, HotkeyHost.WindowTitle) != 0, 10000), "WinScreen didn't start.");
        Assert.False(ProcessInfo.IsElevated((uint)_app.Id), "The ordinary copy runs elevated.");
        uint helper = 0;
        Assert.True(Desktop.WaitFor(() => (helper = HelperProcess()) != 0, 20000), "No helper started.");
        Assert.True(ProcessInfo.IsElevated(helper), "The helper isn't elevated.");
        Assert.Equal(ProcessInfo.PathOf((uint)_app.Id), ProcessInfo.PathOf(helper), StringComparer.OrdinalIgnoreCase);
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect:\n" + Log("winscreen.log"));
        Assert.Contains("log.started", Log("winscreen-administrator.log"));

        // Each window to its own copy: the ordinary one pins its window itself, the helper pins the administrator one.
        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper test ordinary"));
        Assert.True(Desktop.WaitFor(() => Topmost(ordinaryTarget.Handle), 3000), "The ordinary window wasn't pinned.");
        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper test admin"));
        Assert.True(Desktop.WaitFor(() => Topmost(adminTarget), 3000), "The helper didn't pin the administrator window:\n" + Log("winscreen-administrator.log"));
        Assert.Contains("command.forwarded", Log("winscreen.log"));

        // A view hides the administrator window: the helper's, with its own watcher. Killed, only its windows come back,
        // and the ordinary copy starts a new helper.
        Assert.Equal(0, Run("OpenPip", "title:WinScreen helper test admin"));
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(adminTarget) == 0, 5000), "The helper's view didn't hide the administrator window.");
        Thread.Sleep(1500); // Its watcher starts with the next look.
        KillElevated(helper);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(adminTarget) is null or 255, 8000), "The helper's watcher didn't bring the administrator window back.");
        Assert.False(_app.HasExited, "The ordinary copy went with the helper.");
        Assert.True(Topmost(ordinaryTarget.Handle), "The ordinary copy's pin was undone by the helper's recovery.");
        uint second = 0;
        Assert.True(Desktop.WaitFor(() => (second = HelperProcess()) != 0 && second != helper, 20000), "The ordinary copy didn't start a new helper.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Split("helper.connected").Length >= 3, 15000), "The new helper didn't connect.");

        // The ordinary copy killed: the helper puts back what it changed and goes (never left running alone), and the
        // ordinary copy's own watcher brings back its windows.
        Assert.Equal(0, Run("OpenPip", "title:WinScreen helper test admin"));
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(adminTarget) == 0, 5000), "The new helper's view didn't hide the administrator window.");
        _app.Kill();
        Assert.True(Desktop.WaitFor(() => HelperProcess() == 0, 10000), "The helper outlived the ordinary copy.");
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(adminTarget) is null or 255, 5000), "The helper didn't bring its window back as it went.");
    }

    private static bool ClickThrough(nint window) => ((long)User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) != 0;

    // Owner, 22 Nov: an administrator app made click-through (the helper's), then Ctrl+Alt+R: Restore all put back only
    // the ordinary copy's windows, and the app stayed click-through.
    [Fact]
    public void Restore_all_and_Restore_window_reach_the_helpers_windows()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, RunAsAdministrator = true }.Save(Path.Combine(_data, "settings.json"));
        using var ordinaryTarget = new TargetProcess(new Rectangle(140, 160, 420, 300), title: "WinScreen helper restore ordinary");
        nint adminTarget = ElevatedTarget("WinScreen helper restore admin", new Rectangle(620, 160, 420, 300));
        _app = Start();
        Assert.True(Desktop.WaitFor(() => HelperProcess() != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");

        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper restore ordinary"));
        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper restore admin"));
        Assert.Equal(0, Run("ToggleClickThrough", "title:WinScreen helper restore admin"));
        Assert.True(Desktop.WaitFor(() => Topmost(ordinaryTarget.Handle) && Topmost(adminTarget) && ClickThrough(adminTarget), 3000),
            "The windows weren't changed:\n" + Log("winscreen-administrator.log"));

        Assert.Equal(0, Run("RestoreAll", "active"));
        Assert.True(Desktop.WaitFor(() => !Topmost(ordinaryTarget.Handle), 3000), "Restore all left the ordinary copy's window pinned.");
        Assert.True(Desktop.WaitFor(() => !Topmost(adminTarget) && !ClickThrough(adminTarget), 3000),
            "Restore all didn't reach the helper's window:\n" + Log("winscreen-administrator.log"));

        Assert.Equal(0, Run("ToggleClickThrough", "title:WinScreen helper restore admin"));
        Assert.True(Desktop.WaitFor(() => ClickThrough(adminTarget), 3000), "The window wasn't made click-through again.");
        Assert.Equal(0, Run("RestoreWindow", "title:WinScreen helper restore admin"));
        Assert.True(Desktop.WaitFor(() => !ClickThrough(adminTarget), 3000), "Restore window didn't reach the helper's window.");
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    // The ordinary copy has the keys, the helper has an administrator app's view, which never takes the focus: a key
    // pressed with the pointer on that view, or last on it, is for it.
    [Fact]
    public void A_view_key_reaches_the_helpers_view_under_the_pointer_or_last_pointed_at()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        Dictionary<AppCommand, string> keys = CommandCatalog.All.ToDictionary(command => command, _ => "");
        keys[AppCommand.ViewFreeze] = "Ctrl+Alt+J";
        new AppSettings { WelcomeShown = true, ShowToolbar = false, ShowNotifications = false, RunAsAdministrator = true, Hotkeys = keys }
            .Save(Path.Combine(_data, "settings.json"));
        using var ordinaryTarget = new TargetProcess(new Rectangle(140, 520, 420, 260), title: "WinScreen helper keys ordinary");
        nint adminTarget = ElevatedTarget("WinScreen helper keys admin", new Rectangle(620, 160, 420, 300));
        _app = Start();
        uint helper = 0;
        Assert.True(Desktop.WaitFor(() => (helper = HelperProcess()) != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");

        Assert.Equal(0, Run("OpenPip", "title:WinScreen helper keys admin"));
        nint view = 0;
        Assert.True(Desktop.WaitFor(() => (view = Desktop.FindWindow(null, "PiP - WinScreen helper keys admin")) != 0 && User32.IsWindowVisible(view), 8000),
            "The helper's view didn't open:\n" + Log("winscreen-administrator.log"));
        User32.GetWindowThreadProcessId(view, out uint viewProcess);
        Assert.Equal(helper, viewProcess);
        Assert.True(User32.GetWindowRect(view, out var rect));
        var onView = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        Thread.Sleep(1500);

        // On the view, with the ordinary window in front.
        Desktop.GainForegroundRights();
        Desktop.Activate(ordinaryTarget.Handle);
        Desktop.MoveMouse(onView);
        Thread.Sleep(600);
        Desktop.Press(Keys.J, Keys.ControlKey, Keys.Menu);
        Assert.True(Desktop.WaitFor(() => Log("winscreen-administrator.log").Contains("view.frozen on=yes"), 4000),
            "The key didn't reach the helper's view under the pointer:\n" + Log("winscreen.log"));

        // Off every view now: the view the pointer was last on.
        Desktop.MoveMouse(new Point(ordinaryTarget.Bounds.Left + 40, ordinaryTarget.Bounds.Top + 60));
        Thread.Sleep(1500);
        Desktop.Press(Keys.J, Keys.ControlKey, Keys.Menu);
        Assert.True(Desktop.WaitFor(() => Log("winscreen-administrator.log").Contains("view.frozen on=no"), 4000),
            "The key didn't reach the helper's view the pointer was last on:\n" + Log("winscreen.log"));
        Assert.DoesNotContain("command.noView", Log("winscreen.log"));

        // Restore all closes the helper's view too, and its original is back.
        Assert.Equal(0, Run("RestoreAll", "active"));
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(view) || !User32.IsWindowVisible(view), 4000), "Restore all left the helper's view open.");
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(adminTarget) is null or 255, 4000), "The administrator window stayed hidden.");
    }

    // Owner, 22 Nov: Claude, an administrator app, made click-through. The tray's "Turn off click-through everywhere"
    // and "Unpin everywhere" come and go with the helper's windows too, and each says it's done once.
    [Fact]
    public void Everywhere_commands_reach_the_helpers_windows_and_say_so_once()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, RunAsAdministrator = true }.Save(Path.Combine(_data, "settings.json"));
        nint adminTarget = ElevatedTarget("WinScreen helper everywhere admin", new Rectangle(620, 160, 420, 300));
        _app = Start();
        Assert.True(Desktop.WaitFor(() => HelperProcess() != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");

        // In front, the tray shows it: the helper says what is on for it.
        Desktop.GainForegroundRights();
        Desktop.Activate(adminTarget);
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("watched=yes"), 4000), "The helper wasn't asked about the window in front:\n" + Log("winscreen.log"));
        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper everywhere admin"));
        Assert.Equal(0, Run("ToggleClickThrough", "title:WinScreen helper everywhere admin"));
        Assert.True(Desktop.WaitFor(() => Topmost(adminTarget) && ClickThrough(adminTarget), 3000), "The window wasn't changed.");
        Assert.True(Desktop.WaitFor(() => System.Text.RegularExpressions.Regex.IsMatch(Log("winscreen.log"), @"watched=yes on=([2-9]|\d\d)"), 4000),
            "The helper didn't say the window is pinned and click-through:\n" + Log("winscreen.log"));
        // The ordinary copy hears what the helper holds.
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.state clickThrough=1 pinned=1"), 4000),
            "The ordinary copy doesn't know the helper's window is changed:\n" + Log("winscreen.log"));

        Thread.Sleep(500);
        int notices = Count(Log("winscreen-administrator.log"), "notice.forwarded");
        Assert.Equal(0, Run("ClickThroughOffEverywhere", "active"));
        Assert.True(Desktop.WaitFor(() => !ClickThrough(adminTarget), 3000), "Click-through stayed on the helper's window.");
        Assert.Equal(0, Run("UnpinEverywhere", "active"));
        Assert.True(Desktop.WaitFor(() => !Topmost(adminTarget), 3000), "The helper's window stayed pinned.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.state clickThrough=0 pinned=0"), 4000),
            "The ordinary copy still thinks the helper's window is changed.");
        Thread.Sleep(500);
        Assert.Equal(notices, Count(Log("winscreen-administrator.log"), "notice.forwarded")); // The ordinary copy's notice only.

        // The watched window closes: the helper carries on.
        uint helperBefore = HelperProcess();
        User32.GetWindowThreadProcessId(adminTarget, out uint adminProcess);
        KillElevated(adminProcess);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(adminTarget), 5000), "The administrator window didn't close.");
        Thread.Sleep(2000);
        Assert.Equal(helperBefore, HelperProcess());
        Assert.DoesNotContain("helper.gone", Log("winscreen.log"));
        Assert.DoesNotContain("app.stopped", Log("winscreen-administrator.log"));

        // Hide toolbar flips in both copies. A helper started after it was hidden hides it too, and the next press shows
        // it in both: neither ends up the other way.
        Assert.Equal(0, Run("HideToolbar", "active"));
        uint first = HelperProcess();
        KillElevated(first);
        uint second = 0;
        Assert.True(Desktop.WaitFor(() => (second = HelperProcess()) != 0 && second != first, 20000), "No new helper started.");
        Assert.True(Desktop.WaitFor(() => Count(Log("winscreen.log"), "helper.connected") >= 2, 15000), "The new helper didn't connect.");
        string NewHelpersLog() => Log("winscreen-administrator.log")[Log("winscreen-administrator.log").LastIndexOf("log.started", StringComparison.Ordinal)..];
        Assert.True(Desktop.WaitFor(() => NewHelpersLog().Contains("command name=HideToolbar"), 4000), "The new helper wasn't told the toolbar is hidden.");
        Assert.Equal(0, Run("HideToolbar", "active"));
        Thread.Sleep(500);
        Assert.Equal(0, Run("UnpinEverywhere", "active")); // The log writes a repeated line once another comes.
        Assert.True(Desktop.WaitFor(() => Count(NewHelpersLog(), "command name=HideToolbar") == 2, 4000), "The new helper didn't show the toolbar again.");
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    private static int Dwm(nint window, int attribute) => DwmGetWindowAttribute(window, attribute, out int value, sizeof(int)) == 0 ? value : -1;

    private sealed record Look(Rectangle Bounds, long Style, long ExStyle, byte? Alpha, int DarkTitle, int Cloaked, bool Iconic, bool Visible)
    {
        public string Without(Rectangle bounds) => $"style {Style:X} ex {ExStyle:X} alpha {Alpha} dark {DarkTitle} cloaked {Cloaked} iconic {Iconic} visible {Visible}";
    }

    private static Look LookOf(nint window) => new(Desktop.BoundsOf(window), User32.GetWindowLongPtr(window, User32.GWL_STYLE),
        Desktop.ExStyle(window), Desktop.Alpha(window), Dwm(window, 20), Dwm(window, 14), User32.IsIconic(window), User32.IsWindowVisible(window));

    private static HashSet<nint> WindowsOf(uint process)
    {
        var windows = new HashSet<nint>();
        Desktop.EachProcessWindow((int)process, window => windows.Add(window));
        return windows;
    }

    private sealed record Outcome(int Exit, bool Opened, bool Closed, string Change, Rectangle After, Size Moved, int NewWindows, bool Back, string Note);

    // Every window command on an administrator app's window (the helper's) does what it does on an ordinary one (the
    // ordinary copy's): the same change, the same new windows, a dialog or menu in front that Esc closes, and Restore
    // window puts both back the same.
    [Fact]
    public void Every_window_command_does_the_same_on_an_administrator_window_as_on_an_ordinary_one()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, ShowNotifications = false, RunAsAdministrator = true, ExperimentalFeatures = true }
            .Save(Path.Combine(_data, "settings.json"));
        const string ordinaryTitle = "WinScreen matrix ordinary", adminTitle = "WinScreen matrix admin";
        using var ordinaryTarget = new TargetProcess(new Rectangle(120, 150, 420, 300), "1E64C8", ordinaryTitle); // As ElevatedTarget: Dark judges colours.
        ordinaryTarget.UseFrame(); // Title bars, so Borderless, a dark title bar and Roll up have something to work on.
        nint adminTarget = ElevatedTarget(adminTitle, new Rectangle(700, 150, 420, 300), framed: true);
        ElevatedPlace(adminTarget, Desktop.BoundsOf(ordinaryTarget.Handle) with { X = 700 });
        _app = Start();
        uint helper = 0;
        Assert.True(Desktop.WaitFor(() => (helper = HelperProcess()) != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");
        Thread.Sleep(1000);
        Look ordinaryStart = LookOf(ordinaryTarget.Handle), adminStart = LookOf(adminTarget);
        output.WriteLine($"start ordinary {ordinaryStart}\nstart admin    {adminStart}");

        AppCommand[] commands =
        [
            AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.OpacityUp, AppCommand.ToggleDefaultOpacity, AppCommand.ToggleClickThrough,
            AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar, AppCommand.ToggleRollUp, AppCommand.SendToBottom,
            AppCommand.AlignTopLeft, AppCommand.AlignCenter, AppCommand.AlignBottomRight, AppCommand.MoveToNextDisplay, AppCommand.RestorePlacement,
            AppCommand.OpenPip, AppCommand.ToggleShrink, AppCommand.ToggleDark, AppCommand.ToggleGlass, AppCommand.ToggleAnchor, AppCommand.ToggleCrop,
            AppCommand.ShowWindowInfo, AppCommand.Arrange, AppCommand.PlaceExactly, AppCommand.KeepAreas, AppCommand.GlassLevels,
        ];
        uint ordinaryCopy = (uint)_app.Id;
        var problems = new List<string>();
        foreach (AppCommand command in commands)
        {
            Outcome ordinary = Try(command, ordinaryTarget.Handle, ordinaryTitle, ordinaryCopy, elevated: false);
            Outcome admin = Try(command, adminTarget, adminTitle, helper, elevated: true);
            output.WriteLine($"{command}\n  ordinary {ordinary}\n  admin    {admin}");
            if (ordinary.Exit != admin.Exit) problems.Add($"{command}: exit {ordinary.Exit} vs {admin.Exit}");
            if (ordinary.Opened != admin.Opened) problems.Add($"{command}: opened something in front {ordinary.Opened} vs {admin.Opened} ({admin.Note})");
            if (ordinary.Opened && !ordinary.Closed || admin.Opened && !admin.Closed) problems.Add($"{command}: Esc didn't close it (ordinary {ordinary.Closed}, admin {admin.Closed})");
            if (ordinary.Change != admin.Change) problems.Add($"{command}: changed differently\n    ordinary {ordinary.Change}\n    admin    {admin.Change}");
            if (ordinary.Moved != admin.Moved && ordinary.After != admin.After) problems.Add($"{command}: placed differently {ordinary.After} vs {admin.After}");
            if (ordinary.NewWindows != admin.NewWindows) problems.Add($"{command}: new windows {ordinary.NewWindows} vs {admin.NewWindows}");
            if (!ordinary.Back || !admin.Back) problems.Add($"{command}: Restore window didn't put it back (ordinary {ordinary.Back}, admin {admin.Back}) {admin.Note} {ordinary.Note}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems) + "\n--- admin log\n" + Log("winscreen-administrator.log"));

        Outcome Try(AppCommand command, nint window, string title, uint copy, bool elevated)
        {
            // As from a key pressed with that window in front: the command line passes its right to the front on. A click
            // first, as keys from a test don't reach anything while an administrator window is in front.
            Rectangle plain = Desktop.BoundsOf(ordinaryTarget.Handle);
            var middle = new Point(plain.Left + plain.Width / 2, plain.Top + plain.Height / 2);
            User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint frontNow);
            if (ProcessInfo.IsElevated(frontNow))
            {
                ElevatedClick(middle); // Only input from an administrator program gets past it.
                Desktop.WaitFor(() => User32.GetForegroundWindow() == ordinaryTarget.Handle, 4000);
            }
            Desktop.GainForegroundRights();
            Desktop.Activate(window);
            Thread.Sleep(300);
            Look before = LookOf(window);
            HashSet<nint> windowsBefore = WindowsOf(copy);
            int exit = Run(command.ToString(), "title:" + title);
            Thread.Sleep(900);
            nint front = User32.GetForegroundWindow();
            User32.GetWindowThreadProcessId(front, out uint frontProcess);
            // A view or tile takes the focus from the window it hides, by design: not something Esc closes.
            bool opened = frontProcess == copy && !windowsBefore.Contains(front) && command is not (AppCommand.OpenPip or AppCommand.ToggleShrink);
            Look after = LookOf(window);
            int newWindows = WindowsOf(copy).Count - windowsBefore.Count;
            // The helper's tile and look are listed in the ordinary copy's tray.
            if (elevated && command == AppCommand.ToggleShrink && !Desktop.WaitFor(() => Log("winscreen.log").Contains("tiles=1"), 3000))
                problems.Add("The ordinary copy doesn't know the helper's tile.");
            if (elevated && command == AppCommand.ToggleDark && !Desktop.WaitFor(() => Log("winscreen.log").Contains("effects=1"), 3000))
                problems.Add("The ordinary copy doesn't know the helper's Dark.");
            bool closed = false;
            string note = "";
            if (opened)
            {
                note = $"{WinScreen.Windows.WindowInfo.ClassName(front)} '{WinScreen.Windows.WindowInfo.Title(front)}'";
                if (elevated) Elevated("powershell.exe", "-NoProfile -Command \"(New-Object -ComObject WScript.Shell).SendKeys('{ESC}')\"");
                else Desktop.Press(Keys.Escape);
                closed = Desktop.WaitFor(() => !User32.IsWindow(front) || !User32.IsWindowVisible(front), 5000);
                if (!closed && !elevated) Desktop.PostMessage(front, User32.WM_CLOSE, 0, 0);
            }
            // A place is put back by Restore placement; everything else by Restore window.
            bool placement = CommandCatalog.AnchorOf(command) is not null || command is AppCommand.MoveToNextDisplay or AppCommand.RestorePlacement;
            if (command != AppCommand.RestorePlacement) Run(placement ? "RestorePlacement" : "RestoreWindow", "title:" + title);
            Thread.Sleep(900);
            Look back = LookOf(window);
            bool restored = (back == before || command == AppCommand.RestorePlacement) && WindowsOf(copy).Count <= windowsBefore.Count;
            if (!restored) note += $" back {back} vs {before}, windows {WindowsOf(copy).Count} vs {windowsBefore.Count}";
            var moved = new Size(after.Bounds.X - before.Bounds.X + after.Bounds.Width - before.Bounds.Width,
                after.Bounds.Y - before.Bounds.Y + after.Bounds.Height - before.Bounds.Height);
            return new Outcome(exit, opened, closed, after.Without(after.Bounds), after.Bounds, moved, newWindows, restored, note);
        }
    }

    // A command line run by a person comes from the window in front, which may put windows in front; a test has to win
    // that right first, and keys from it don't get past an administrator window in front.
    private static void TakeFront(TargetProcess plain)
    {
        Rectangle bounds = Desktop.BoundsOf(plain.Handle);
        var middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint front);
        if (ProcessInfo.IsElevated(front))
        {
            ElevatedClick(middle);
            Desktop.WaitFor(() => User32.GetForegroundWindow() == plain.Handle, 4000);
        }
        Desktop.GainForegroundRights();
    }
    // A setup saved from an administrator app's window is the helper's to make and the ordinary copy's to write; the
    // next window of that program, administrator or not, gets it from the copy that reaches it, with nothing failing.
    [Fact]
    public void A_setup_saved_on_an_administrator_window_is_written_once_and_applied_to_its_next_windows()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        string settingsFile = Path.Combine(_data, "settings.json");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, ShowNotifications = false, RunAsAdministrator = true }.Save(settingsFile);
        using var plain = new TargetProcess(new Rectangle(140, 520, 420, 260), "1E64C8", "WinScreen setup plain");
        nint first = ElevatedTarget("WinScreen setup admin one", new Rectangle(620, 160, 420, 300));
        _app = Start();
        Assert.True(Desktop.WaitFor(() => HelperProcess() != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");

        TakeFront(plain);
        Assert.Equal(0, Run("TogglePin", "title:WinScreen setup admin one"));
        Assert.Equal(0, Run("OpacityDown", "title:WinScreen setup admin one"));
        Assert.True(Desktop.WaitFor(() => Topmost(first) && Desktop.Alpha(first) is < 255, 3000), "The window wasn't changed.");
        Assert.Equal(0, Run("SaveWindowSetup", "title:WinScreen setup admin one"));
        Assert.True(Desktop.WaitFor(() => ReadShared(settingsFile).Contains("\"Pinned\": true"), 5000),
            "The ordinary copy didn't write the helper's setup:\n" + ReadShared(settingsFile));
        AppSettings written = AppSettings.Parse(ReadShared(settingsFile));
        ProgramSetup setup = Assert.Single(written.SavedSetups);
        Assert.True(setup.Pinned && setup.Alpha < 255, setup.ToString());

        // The next window of that program, as administrator: the helper gives it the setup.
        nint second = ElevatedTarget("WinScreen setup admin two", new Rectangle(660, 200, 420, 300));
        TakeFront(plain);
        Desktop.Activate(second);
        Assert.True(Desktop.WaitFor(() => Topmost(second) && Desktop.Alpha(second) is < 255, 6000),
            "The helper didn't give the next administrator window its setup:\n" + Log("winscreen-administrator.log"));
        // And not as administrator: the ordinary copy does.
        using var ordinary = new TargetProcess(new Rectangle(600, 560, 420, 260), "1E64C8", "WinScreen setup ordinary");
        TakeFront(plain);
        Desktop.Activate(ordinary.Handle);
        Assert.True(Desktop.WaitFor(() => Topmost(ordinary.Handle) && Desktop.Alpha(ordinary.Handle) is < 255, 6000),
            "The ordinary copy didn't give the ordinary window the setup:\n" + Log("winscreen.log"));
        Thread.Sleep(1500);
        Assert.DoesNotContain("command.failed", Log("winscreen.log"));
        Assert.DoesNotContain("setup.failed", Log("winscreen.log"));
        Assert.Single(AppSettings.Parse(ReadShared(settingsFile)).SavedSetups);
    }

    // Owner, 1.3.0: Ctrl+right-click on a pinned administrator app opened the helper's menu, which closed again at once
    // each time, the log saying the app took the focus back.
    [Fact]
    public void The_helpers_window_menu_stays_open_on_a_pinned_administrator_window()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, RunAsAdministrator = true, WindowMenu = true, HighlightPinned = true }
            .Save(Path.Combine(_data, "settings.json"));
        var bounds = new Rectangle(300, 160, 520, 380);
        nint target = ElevatedTarget("WinScreen helper menu test", bounds);
        _app = Start();
        uint helper = 0;
        Assert.True(Desktop.WaitFor(() => (helper = HelperProcess()) != 0, 20000), "No helper started.");
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "The helper didn't connect.");
        Assert.Equal(0, Run("TogglePin", "title:WinScreen helper menu test"));
        Assert.True(Desktop.WaitFor(() => Topmost(target), 3000), "The helper didn't pin the window.");
        Thread.Sleep(500);

        var point = new Point(bounds.Left + 120, bounds.Top + 120);
        for (int round = 0; round < 2; round++)
        {
            ElevatedGesture(point);
            nint host = 0;
            Assert.True(Desktop.WaitFor(() => (host = Desktop.FindWindow(null, "WinScreen.WindowMenu")) != 0 && User32.IsWindowVisible(host), 3000),
                $"Round {round}: the menu didn't open.\n" + Log("winscreen-administrator.log"));
            Thread.Sleep(1200);
            Assert.True(User32.IsWindow(host) && User32.IsWindowVisible(host), $"Round {round}: the menu closed by itself.\n" + Log("winscreen-administrator.log"));
            User32.GetWindowThreadProcessId(Desktop.TopWindowAt(new Point(point.X + 30, point.Y + 14)), out uint shown);
            Assert.True(shown == helper, $"Round {round}: the menu is behind the window.");
            Elevated("powershell.exe", "-NoProfile -Command \"(New-Object -ComObject WScript.Shell).SendKeys('{ESC}')\"");
            Assert.True(Desktop.WaitFor(() => !User32.IsWindow(host) || !User32.IsWindowVisible(host), 5000), $"Round {round}: Escape left the menu open.");
            Thread.Sleep(500);
        }
    }

    // A framed administrator window may open maximized; this program can't move it, an administrator one can.
    private static void ElevatedPlace(nint window, Rectangle bounds)
    {
        string script = $$"""
            $k = Add-Type -Name P -PassThru -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool r);'
            [void]$k::ShowWindow([IntPtr]{{(long)window}}, 9); Start-Sleep -Milliseconds 200
            [void]$k::MoveWindow([IntPtr]{{(long)window}}, {{bounds.X}}, {{bounds.Y}}, {{bounds.Width}}, {{bounds.Height}}, $true)
            """;
        Elevated("powershell.exe", "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        Desktop.WaitFor(() => !User32.IsZoomed(window) && Desktop.BoundsOf(window).Width == bounds.Width, 8000);
    }

    private static void ElevatedClick(Point point)
    {
        string script = $$"""
            $k = Add-Type -Name C -PassThru -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);'
            [void]$k::SetCursorPos({{point.X}}, {{point.Y}}); Start-Sleep -Milliseconds 100
            $k::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50
            $k::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            """;
        Elevated("powershell.exe", "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
    }

    // Input into an administrator app's window has to come from a program running as administrator too.
    private static void ElevatedGesture(Point point)
    {
        string script = $$"""
            $k = Add-Type -Name G -PassThru -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e); [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);'
            [void]$k::SetCursorPos({{point.X}}, {{point.Y}}); Start-Sleep -Milliseconds 150
            $k::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
            $k::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
            $k::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
            $k::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
            """;
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Elevated("powershell.exe", "-NoProfile -EncodedCommand " + encoded);
    }

    /// <summary>
    /// At sign-in the task starts the helper first (an install from before 10-2 too, whose task gives no argument): it
    /// starts WinScreen as the person through the shell, they connect, and it goes when WinScreen is closed.
    /// </summary>
    [Fact]
    public void Started_first_at_sign_in_the_helper_starts_WinScreen_as_the_person()
    {
        if (!ElevatesWithoutAsking) { Skipped.Because("Windows asks before running as administrator here, and a test can't answer."); return; }
        if (ProcessInfo.SelfElevated || !ProcessInfo.CanElevate) { Skipped.Because("Needs an administrator account and a test run that isn't elevated."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0 && HelperProcess() == 0, "Close the running WinScreen before these tests.");
        new AppSettings { WelcomeShown = true, ShowToolbar = false, RunAsAdministrator = true }.Save(Path.Combine(_data, "settings.json"));

        Elevated(Program, $"{AppRoles.DataFolderArgument} \"{_data}\""); // As the old sign-in task does: no helper argument.
        uint helper = 0;
        Assert.True(Desktop.WaitFor(() => (helper = HelperProcess()) != 0, 15000), "The elevated start didn't become the helper.");
        nint host = 0;
        Assert.True(Desktop.WaitFor(() => (host = Desktop.FindWindow(null, HotkeyHost.WindowTitle)) != 0, 20000),
            "The helper didn't start WinScreen:\n" + Log("winscreen-administrator.log"));
        User32.GetWindowThreadProcessId(host, out uint ordinary);
        _app = Process.GetProcessById((int)ordinary);
        Assert.False(ProcessInfo.IsElevated(ordinary), "The copy the helper started is elevated.");
        Assert.Equal(ProcessInfo.PathOf(helper), ProcessInfo.PathOf(ordinary), StringComparer.OrdinalIgnoreCase);
        Assert.True(Desktop.WaitFor(() => Log("winscreen.log").Contains("helper.connected"), 15000), "They didn't connect:\n" + Log("winscreen.log"));
        Assert.Contains("helper.startedOrdinaryCopy", Log("winscreen-administrator.log"));

        Desktop.PostMessage(host, User32.WM_CLOSE, 0, 0);
        Assert.True(_app.WaitForExit(8000), "WinScreen didn't close.");
        Assert.True(Desktop.WaitFor(() => HelperProcess() == 0, 10000), "The helper outlived WinScreen.");
    }

    public void Dispose()
    {
        output.WriteLine("--- winscreen.log" + Environment.NewLine + Log("winscreen.log"));
        output.WriteLine("--- winscreen-administrator.log" + Environment.NewLine + Log("winscreen-administrator.log"));
        if (_app is { HasExited: false })
        {
            nint host = Desktop.FindWindow(null, HotkeyHost.WindowTitle);
            if (host != 0) Desktop.PostMessage(host, User32.WM_CLOSE, 0, 0);
            if (!_app.WaitForExit(6000)) _app.Kill();
        }
        _app?.Dispose();
        if (HelperProcess() is var helper and not 0) KillElevated(helper);
        foreach (int process in _elevated) KillElevated((uint)process);
        Thread.Sleep(1500);
        try { if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true); }
        catch (IOException) { output.WriteLine("The data folder was still in use; the temp folder is cleaned later."); }
    }
}
