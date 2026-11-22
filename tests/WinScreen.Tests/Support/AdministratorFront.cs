using System.IO.Pipes;
using System.Reflection;
using WinScreen.Native;
using WinScreen.Pip;
using Xunit;
using Xunit.Sdk;

[assembly: WinScreen.Tests.Support.AdministratorFront.BeforeEachTest]

namespace WinScreen.Tests.Support;

/// <summary>
/// A run with an administrator window in front (Invoke-HostRun.ps1 -AdministratorInFront): before each test a small
/// window of a program running as administrator takes the focus, as when the person was just typing in one. Windows
/// keeps input from a normal program out of it, so the tests' input is sent from that program too.
/// </summary>
internal static class AdministratorFront
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("WINSCREEN_TEST_ADMINISTRATOR_FRONT") == "1";

    private const string Title = "WinScreen test: administrator in front";
    private static readonly object Gate = new();
    private static NamedPipeServerStream? s_pipe;
    private static StreamReader? s_reader;
    private static StreamWriter? s_writer;

    // Bottom right, clear of where the tests put their windows and views.
    public static Rectangle Spot
    {
        get
        {
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            return new Rectangle(area.Right - 260, area.Bottom - 190, 240, 170);
        }
    }

    public static nint Window { get; private set; }

    /// <summary>Sends one command and waits until it is done; returns the answer, "ok" and maybe more.</summary>
    public static string Do(string command)
    {
        lock (Gate)
        {
            Start();
            s_writer!.WriteLine(command);
            string? answer = s_reader!.ReadLine();
            if (answer is null || !answer.StartsWith("ok", StringComparison.Ordinal))
                throw new InvalidOperationException($"The administrator program answered \"{answer}\" to \"{command}\".");
            return answer;
        }
    }

    /// <summary>
    /// Waits for a visit to start. With an administrator window in front a view starts none until it is pressed: a
    /// person whose hover does nothing clicks, so here the view gets that first press where the pointer is, and the test
    /// carries on. The press is a real click in the app, which a test counting clicks sees too.
    /// </summary>
    public static bool WaitForVisit(PipWindow view, int milliseconds = 3000)
    {
        if (!On) return Desktop.WaitFor(() => view.LiveInteraction is not null, milliseconds);
        if (Desktop.WaitFor(() => view.LiveInteraction is not null, Math.Min(milliseconds, 1500)))
        {
            LastFirstPress = "none needed";
            return true;
        }
        string before = $"no visit because '{view.NoVisitBecause}', waiting for a press {view.WaitsForPress}, " +
            $"left button held for the view's thread {view.Invoke(() => (User32.GetKeyState(1) & 0x8000) != 0)}, held for Windows '{Do("held")}'";
        Desktop.MouseButton(true);
        bool started = Desktop.WaitFor(() => view.LiveInteraction is not null, 1000);
        Thread.Sleep(50);
        Desktop.MouseButton(false);
        // Not part of a double click the test makes next.
        Thread.Sleep(SystemInformation.DoubleClickTime + 50);
        LastFirstPress = $"given at {Desktop.CursorPosition()} ({before}): visit {(started ? "started" : "didn't start")}, " +
            $"now {(view.LiveInteraction is null ? "none" : "on")}, last end '{view.LastVisitEnd}', in front {Desktop.FrontDescription()}";
        bool visit = Desktop.WaitFor(() => view.LiveInteraction is not null, milliseconds);
        // Every first press of the run, beside the other run notes.
        if (Path.GetDirectoryName(Environment.GetEnvironmentVariable(Skipped.FileVariable)) is { Length: > 0 } results)
            File.AppendAllText(Path.Combine(results, "first-presses.txt"),
                $"{TestName()}\t{LastFirstPress}\tvisit after: {visit}{Environment.NewLine}");
        return visit;
    }

    private static string TestName() =>
        new System.Diagnostics.StackTrace().GetFrames().Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.DeclaringType?.Namespace == "WinScreen.Tests" && method.Name.Contains('_'))?.Name ?? "?";

    /// <summary>What <see cref="WaitForVisit"/> did last, for failure messages.</summary>
    public static string LastFirstPress { get; private set; } = "";

    /// <summary>What the administrator program found held and let go before this test.</summary>
    public static string LastReleased { get; private set; } = "";

    public static void TakeTheFront()
    {
        // A test that failed holding a button down leaves it held for the next one. Read where Windows doesn't hide it.
        LastReleased = Do("release");
        Do("front");
        if (!Desktop.WaitFor(() => User32.GetForegroundWindow() == Window, 2000))
            throw new InvalidOperationException("The administrator window didn't take the focus.");
    }

    private static void Start()
    {
        if (s_pipe is not null) return;
        if (!AsAdministrator.Usable)
            throw new InvalidOperationException("An administrator window in front needs Windows to run programs as administrator without asking, from a test that isn't.");
        string name = "WinScreenTestFront-" + Environment.ProcessId;
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
        Rectangle spot = Spot;
        AsAdministrator.Start(Path.Combine(AppContext.BaseDirectory, "WinScreen.TestTarget.exe"),
            $"--left {spot.Left} --top {spot.Top} --width {spot.Width} --height {spot.Height} --colors 7A3CB4 --title \"{Title}\" --relay {name}",
            hidden: false);
        Task connected = pipe.WaitForConnectionAsync();
        if (!connected.Wait(20000)) throw new InvalidOperationException("The administrator program never connected.");
        nint window = 0;
        if (!Desktop.WaitFor(() => (window = Desktop.FindWindow(null, Title)) != 0 && User32.IsWindowVisible(window), 10000))
            throw new InvalidOperationException("The administrator window didn't open.");
        User32.GetWindowThreadProcessId(window, out uint process);
        if (!ProcessInfo.IsElevated(process)) throw new InvalidOperationException("The administrator window isn't running as administrator.");
        Window = window;
        s_pipe = pipe;
        s_reader = new StreamReader(pipe);
        s_writer = new StreamWriter(pipe) { AutoFlush = true };
        // The program ends when the pipe closes, also when the test host is stopped.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { s_writer.WriteLine("exit"); } catch (IOException) { } };
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class BeforeEachTestAttribute : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest)
        {
            if (On) TakeTheFront();
        }
    }
}
