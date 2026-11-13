using System.Diagnostics;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

public sealed class PackagedFactAttribute : FactAttribute
{
    public PackagedFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINSCREEN_EXE")))
            Skip = "Set WINSCREEN_EXE to test a packaged build.";
    }
}

[Trait("Category", "RealWindows")]
public sealed class PackagedSmokeTests(ITestOutputHelper output)
{
    [PackagedFact]
    public void Packaged_program_starts_and_exits_with_new_features_off()
    {
        string? program = Environment.GetEnvironmentVariable("WINSCREEN_EXE");
        Assert.False(string.IsNullOrEmpty(program), "Set WINSCREEN_EXE to the packaged WinScreen.exe for this smoke test.");
        Assert.Equal(0, Desktop.FindWindow(null, HotkeyHost.WindowTitle));
        string data = Directory.CreateTempSubdirectory("winscreen-packaged").FullName;
        var settings = new AppSettings { ShowToolbar = false, WelcomeShown = true };
        foreach (AppCommand command in CommandCatalog.All) settings.Hotkeys[command] = "";
        settings.Save(Path.Combine(data, "settings.json"));
        var start = new ProcessStartInfo(program!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["WINSCREEN_DATA"] = data;
        using Process process = Process.Start(start)!;
        nint host = 0;
        try
        {
            Assert.True(Desktop.WaitFor(() =>
            {
                host = Desktop.FindWindow(null, HotkeyHost.WindowTitle);
                User32.GetWindowThreadProcessId(host, out uint pid);
                return host != 0 && pid == process.Id;
            }, 10000));
            int warmup = int.TryParse(Environment.GetEnvironmentVariable("WINSCREEN_IDLE_WARMUP_SECONDS"), out int w) ? Math.Clamp(w, 1, 30) : 1;
            int sample = int.TryParse(Environment.GetEnvironmentVariable("WINSCREEN_IDLE_SAMPLE_SECONDS"), out int s) ? Math.Clamp(s, 1, 30) : 3;
            Thread.Sleep(warmup * 1000);
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            long started = Stopwatch.GetTimestamp();
            Thread.Sleep(sample * 1000);
            process.Refresh();
            double core = (process.TotalProcessorTime - cpu).TotalSeconds / Stopwatch.GetElapsedTime(started).TotalSeconds * 100;
            string observation = $"Packaged, new features off: {process.Threads.Count} threads; {process.HandleCount} handles; {process.PrivateMemorySize64} private bytes; {core:F3}% of one core over {sample} seconds after {warmup} seconds warmup. Short observation, not a sustained benchmark.";
            output.WriteLine(observation);
            if (Environment.GetEnvironmentVariable("WINSCREEN_IDLE_EVIDENCE") is { } evidence) File.WriteAllText(evidence, observation);
            Assert.False(File.Exists(Path.Combine(data, "changed-windows.json")));
            Desktop.PostMessage(host, User32.WM_CLOSE, 0, 0);
            Assert.True(process.WaitForExit(8000));
            Assert.Equal(0, process.ExitCode);

            // Its own short log: what a report needs about this PC, and how it ended. Nothing about the person.
            string log = File.ReadAllText(Path.Combine(data, "winscreen.log"));
            output.WriteLine(log);
            Assert.Contains("log.started role=app", log);
            Assert.Matches(@"system windows=1[01] build=\d+", log);
            Assert.Matches(@"display number=1 width=\d+ height=\d+ x=-?\d+ y=-?\d+ scale=\d+ main=yes", log);
            Assert.Contains("graphics name=", log);
            Assert.Contains("app.exited", log);
            Assert.DoesNotContain(Environment.UserName, log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", log);
        }
        finally
        {
            if (!process.HasExited)
            {
                User32.GetWindowThreadProcessId(host, out uint pid);
                if (pid == process.Id) Desktop.PostMessage(host, User32.WM_CLOSE, 0, 0);
                if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(3000); }
            }
            Directory.Delete(data, true);
        }
    }
}
