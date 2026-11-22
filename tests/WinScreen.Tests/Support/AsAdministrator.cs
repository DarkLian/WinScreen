using System.Diagnostics;
using Microsoft.Win32;
using WinScreen.Native;
using Xunit;

namespace WinScreen.Tests.Support;

/// <summary>
/// Programs and input from a program running as administrator, for a test run that isn't. Windows doesn't ask on the
/// owner's PC; elsewhere the tests that need it skip.
/// </summary>
internal sealed class AsAdministrator : IDisposable
{
    private readonly List<(int Process, nint Window)> _started = [];

    public static bool ElevatesWithoutAsking =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 5) is 0;

    public static bool Usable => ElevatesWithoutAsking && !ProcessInfo.SelfElevated && ProcessInfo.CanElevate;

    public static void Start(string program, string arguments, bool hidden = true) =>
        Process.Start(new ProcessStartInfo(program, arguments)
            { UseShellExecute = true, Verb = "runas", WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal })?.Dispose();

    /// <summary>A test window in a program running as administrator, closed with this.</summary>
    public nint Target(string title, Rectangle bounds)
    {
        Start(Path.Combine(AppContext.BaseDirectory, "WinScreen.TestTarget.exe"),
            $"--left {bounds.Left} --top {bounds.Top} --width {bounds.Width} --height {bounds.Height} --colors 1E64C8 --title \"{title}\"",
            hidden: false); // Started hidden, Windows would keep it hidden: there would be nothing to click.
        nint window = 0;
        Assert.True(Desktop.WaitFor(() => (window = Desktop.FindWindow(null, title)) != 0 && User32.IsWindowVisible(window), 10000),
            "The administrator test window didn't open.");
        User32.GetWindowThreadProcessId(window, out uint process);
        Assert.True(ProcessInfo.IsElevated(process), "The test window isn't running as administrator.");
        _started.Add(((int)process, window));
        return window;
    }

    /// <summary>Runs a PowerShell script as administrator and waits for it.</summary>
    public static void Script(string script, int waitMilliseconds = 15000)
    {
        using Process? shell = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        shell?.WaitForExit(waitMilliseconds);
    }

    private const string Input = "$k = Add-Type -Name I -PassThru -MemberDefinition '" +
        "[DllImport(\"user32.dll\")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e); " +
        "[DllImport(\"user32.dll\")] public static extern bool SetCursorPos(int x, int y);'";

    /// <summary>A left click there, as a person's own would be: it gets past an administrator window in front.</summary>
    public static void Click(Point point) => Script($$"""
        {{Input}}
        [void]$k::SetCursorPos({{point.X}}, {{point.Y}}); Start-Sleep -Milliseconds 100
        $k::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50
        $k::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        """);

    /// <summary>Small pointer moves around a point for about as long as asked, started now and not waited for.</summary>
    public static void JiggleInBackground(Point point, int milliseconds)
    {
        string script = $$"""
            {{Input}}
            [void]$k::SetCursorPos({{point.X}}, {{point.Y}}); Start-Sleep -Milliseconds 50
            for ($i = 0; $i -lt {{milliseconds / 50}}; $i++) {
                $k::mouse_event(1, 3, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 25
                $k::mouse_event(1, -3, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 25
            }
            """;
        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
    }

    /// <summary>
    /// When the administrator window goes, Windows may give the focus to another one (a minimized one too), and then
    /// nothing in a test that isn't elevated reaches it. A click on an empty part of the taskbar, made as administrator,
    /// gives the focus to the taskbar.
    /// </summary>
    public static void FreeTheFront()
    {
        if (!ElevatedInFront()) return;
        nint taskbar = User32.FindWindow("Shell_TrayWnd", null);
        Rectangle bar = Desktop.BoundsOf(taskbar);
        List<Rectangle> buttons = BrowserAccessibility.Read(taskbar, 24)
            .Where(control => control.Type == 50000 && !control.Bounds.IsEmpty && bar.Contains(control.Bounds))
            .Select(control => control.Bounds).OrderBy(bounds => bounds.Left).ToList();
        int from = bar.Left, gapFrom = 0, gapTo = 0;
        foreach (Rectangle button in buttons.Append(new Rectangle(bar.Right, bar.Top, 0, bar.Height)))
        {
            if (button.Left - from > gapTo - gapFrom) (gapFrom, gapTo) = (from, button.Left);
            from = Math.Max(from, button.Right);
        }
        Assert.True(gapTo - gapFrom > 40, "No empty spot on the taskbar to click.");
        Click(new Point((gapFrom + gapTo) / 2, bar.Top + bar.Height / 2));
        Assert.True(Desktop.WaitFor(() => !ElevatedInFront(), 3000), "An administrator window kept the focus.");
    }

    private static bool ElevatedInFront()
    {
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint process);
        return process != 0 && ProcessInfo.IsElevated(process);
    }

    public void Dispose()
    {
        foreach ((int process, nint window) in _started)
        {
            Start("taskkill.exe", $"/PID {process} /F");
            Desktop.WaitFor(() => !User32.IsWindow(window), 5000);
        }
        FreeTheFront();
    }
}
