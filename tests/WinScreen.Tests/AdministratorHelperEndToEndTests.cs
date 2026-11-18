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
    private nint ElevatedTarget(string title, Rectangle bounds)
    {
        Elevated(Path.Combine(AppContext.BaseDirectory, "WinScreen.TestTarget.exe"),
            $"--left {bounds.Left} --top {bounds.Top} --width {bounds.Width} --height {bounds.Height} --colors 1E64C8 --title \"{title}\"");
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
