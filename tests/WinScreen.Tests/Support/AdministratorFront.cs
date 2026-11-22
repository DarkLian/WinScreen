using System.IO.Pipes;
using System.Reflection;
using WinScreen.Native;
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

    /// <summary>Sends one command and waits until it is done.</summary>
    public static void Do(string command)
    {
        lock (Gate)
        {
            Start();
            s_writer!.WriteLine(command);
            string? answer = s_reader!.ReadLine();
            if (answer != "ok") throw new InvalidOperationException($"The administrator program answered \"{answer}\" to \"{command}\".");
        }
    }

    public static void TakeTheFront()
    {
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
