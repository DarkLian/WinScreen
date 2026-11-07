using System.Diagnostics;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipLiveBenchmarkTests(ITestOutputHelper output)
{
    [WgcFact]
    public void Live_capture_memory_soak() =>
        Changing_1080p_pattern_has_bounded_frame_age_and_records_observed_performance(PipBackendPreference.Wgc, 60, 576, 324);

    [WgcTheory]
    [InlineData(PipBackendPreference.Dwm, 60, 576, 324)]
    [InlineData(PipBackendPreference.Wgc, 60, 576, 324)]
    [InlineData(PipBackendPreference.Wgc, 144, 576, 324)]
    [InlineData(PipBackendPreference.Wgc, 60, 1280, 720)]
    public void Changing_1080p_pattern_has_bounded_frame_age_and_records_observed_performance(
        PipBackendPreference preference, int rate, int width, int height)
    {
        using var source = new TargetProcess(new(40, 40, 1920, 1080));
        source.Animate(144);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                static void Pump(int ms)
                {
                    long end = Environment.TickCount64 + ms;
                    while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(1); }
                }
                var windows = new WindowController([]);
                using var pip = new PipWindow(source.Handle, windows);
                pip.ApplyPipPreferences(new() { Backend = preference, FrameRate = rate, ShowDiagnostics = true });
                pip.ShowOnTop(); pip.Location = new(900, 160);
                if (preference != PipBackendPreference.Dwm)
                    Assert.True(Desktop.WaitFor(() => pip.BackendKind != PipBackendKind.DwmThumbnail, 15000, () => Pump(20)), pip.DiagnosticSnapshot);
                // A wheel-size operation is unnecessary for this fixture: set the viewport directly through its owner.
                var selector = (PipBackendSelector)typeof(PipWindow).GetField("_thumbnail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pip)!;
                pip.ClientSize = new(width + 4, height + 4);
                selector.SetViewport(new(2, 2, width, height), new(0, 0, 1920, 1080));
                Pump(700);
                using var process = Process.GetCurrentProcess();
                TimeSpan cpu = process.TotalProcessorTime;
                long memory = process.PrivateMemorySize64;
                var samples = new List<long>();
                double seconds = int.TryParse(Environment.GetEnvironmentVariable("WINSCREEN_PIP_SOAK_SECONDS"), out int requested)
                    ? Math.Clamp(requested, 3, 900) : 3;
                bool isolatedSoak = seconds > 10;
                if (isolatedSoak)
                {
                    // Keep a long memory fixture away from the user's pointer and global desktop input.
                    // Warmup above proves real presentation; this phase measures capture/GPU-processing memory,
                    // with the host hidden. Occluded Present is not counted as a displayed new frame.
                    pip.SetOriginalHidden(true);
                    pip.Location = new(-30000, -30000);
                    pip.Hide();
                }
                long started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds)
                {
                    Pump(1000); process.Refresh(); samples.Add(process.PrivateMemorySize64);
                    Assert.False(pip.IsDisposed, pip.DiagnosticSnapshot);
                    if (preference != PipBackendPreference.Dwm) Assert.Equal(PipBackendKind.WgcHardware, pip.BackendKind);
                }
                double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
                double cpuCore = (process.TotalProcessorTime - cpu).TotalMilliseconds / (duration * 1000) * 100;
                string result = $"Fixture: source 1920x1080 @ requested 144 Hz; output {width}x{height}; preference {preference}; cap {rate}\n" +
                    (isolatedSoak ? "Isolated memory soak: source concealed, host hidden after successful native presentation; no sustained visible-present FPS claim.\n" : "") +
                    $"Observation {duration:F2} s; testhost CPU {cpuCore:F2}% of one core (includes harness); private bytes start/end/min/max " +
                    $"{memory}/{samples[^1]}/{samples.Min()}/{samples.Max()}\n" + pip.DiagnosticSnapshot;
                if (samples.Count >= 90)
                {
                    long early = samples.Skip(30).Take(30).Order().ElementAt(15);
                    long late = samples.TakeLast(30).Order().ElementAt(15);
                    Assert.True(late - early < 64 * 1024 * 1024, $"Private memory grew by {late - early} bytes after warmup.");
                    result += $"Post-warmup median private-byte growth: {late - early}\n";
                }
                output.WriteLine(result);
                string? artifacts = Environment.GetEnvironmentVariable("WINSCREEN_PIP_EVIDENCE");
                if (artifacts is not null)
                {
                    Directory.CreateDirectory(artifacts);
                    File.WriteAllText(Path.Combine(artifacts, $"benchmark-{preference}-{rate}-{width}x{height}.txt"), result);
                    if (!isolatedSoak)
                    {
                        using var image = new Bitmap(pip.Width, pip.Height);
                        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(pip.Location, Point.Empty, image.Size);
                        image.Save(Path.Combine(artifacts, $"benchmark-{preference}-{rate}-{width}x{height}.png"));
                    }
                }
                pip.Close(); Pump(150);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
