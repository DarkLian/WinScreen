using System.Diagnostics;

namespace WinScreen.Pip;

internal sealed record PipCapabilities(bool WgcSupported, bool HardwareDeviceCreated, string FeatureLevel,
    bool ComputeAreaSupported, bool FormatSupported, bool? WarpUsable, bool MinUpdateIntervalSupported,
    string Adapter, long AdapterLuid, bool? CrossAdapterDirectCopySupported = null, bool? TearingSupported = null,
    bool HdrSupported = false);

internal sealed class PipDiagnostics
{
    private readonly object _gate = new();
    private readonly double[] _latencies = new double[512];
    private readonly double[] _arrivalLatencies = new double[512];
    private int _arrivalCount, _arrivalIndex;
    private bool _captureClockUncorrelated;
    private int _latencyCount, _latencyIndex;
    private readonly long _start = Stopwatch.GetTimestamp();
    internal long Received, Accepted, Presented, Dropped, Resizes;
    internal long QueueDrops;
    internal long LastArrival;
    internal Size Source, Target;
    internal double LastPresentMilliseconds;
    internal PipCapabilities? Capabilities;
    internal string? Failure;
    internal Exception? Exception;
    internal string FilterMode = "OS-managed";
    internal bool CaptureBorderHidden;

    internal void PresentedFrame(long captureTimestamp, long arrivalTimestamp, double presentMs)
    {
        lock (_gate)
        {
            Presented++;
            LastPresentMilliseconds = presentMs;
            double age = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency) - captureTimestamp / 10_000.0;
            if (age is >= 0 and < 60_000)
            {
                _latencies[_latencyIndex] = age;
                _latencyIndex = (_latencyIndex + 1) % _latencies.Length;
                _latencyCount = Math.Min(_latencies.Length, _latencyCount + 1);
            }
            else _captureClockUncorrelated = true;
            if (arrivalTimestamp != 0)
            {
                _arrivalLatencies[_arrivalIndex] = Stopwatch.GetElapsedTime(arrivalTimestamp).TotalMilliseconds;
                _arrivalIndex = (_arrivalIndex + 1) % _arrivalLatencies.Length;
                _arrivalCount = Math.Min(_arrivalLatencies.Length, _arrivalCount + 1);
            }
        }
    }

    internal string Snapshot(PipBackendKind backend, PipSessionState state, int requested, int sourceHz, int targetHz,
        bool includeHardware)
    {
        lock (_gate)
        {
            double seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_start).TotalSeconds);
            double[] sorted = _latencies.Take(_latencyCount).Order().ToArray();
            string Percentile(double p) => sorted.Length == 0 || _captureClockUncorrelated ? "unavailable" : $"{sorted[(int)((sorted.Length - 1) * p)]:F2} ms";
            double[] arrivals = _arrivalLatencies.Take(_arrivalCount).Order().ToArray();
            string Arrival(double p) => arrivals.Length == 0 ? "unavailable" : $"{arrivals[(int)((arrivals.Length - 1) * p)]:F2} ms";
            string result = $"Backend: {backend}; state: {state}; SDR\nSource: {Source}; target: {Target}\n";
            if (backend == PipBackendKind.DwmThumbnail)
                result += "OS-managed cadence — capture timings unavailable; WinScreen can't tell whether the picture is current\n";
            else
                result += $"Requested: {(requested == 0 ? "Match Monitor" : requested)}; source Hz: {sourceHz}; destination Hz: {targetHz}\n" +
                    $"Callback/accepted/presented FPS: {Received / seconds:F2}/{Accepted / seconds:F2}/{Presented / seconds:F2}; dropped: {Dropped + QueueDrops}\n" +
                    $"Capture-to-Present return p50/p95/p99: {Percentile(.50)}/{Percentile(.95)}/{Percentile(.99)} (not scanout latency)\n" +
                    $"Callback-to-Present return p50/p95/p99: {Arrival(.50)}/{Arrival(.95)}/{Arrival(.99)}\n" +
                    (_captureClockUncorrelated ? "Capture timestamp correlation failed; capture latency is unavailable.\n" : "") +
                    $"Last Present: {LastPresentMilliseconds:F2} ms; resizes: {Resizes}; pending capacity: 1\nGPU duration: unavailable (no GPU timestamp query)\n";
            if (backend != PipBackendKind.DwmThumbnail && LastArrival != 0)
                result += $"Frame arrival age: {Stopwatch.GetElapsedTime(LastArrival).TotalMilliseconds:F1} ms; GPU in-flight capacity: 1; filter: {FilterMode}\n";
            if (backend != PipBackendKind.DwmThumbnail)
                result += $"Capture border: {(CaptureBorderHidden ? "off" : "shown")}\n";
            if (backend == PipBackendKind.WgcSoftware)
                result += $"Software effective cap: {Math.Min(30, requested == 0 ? targetHz : requested)} FPS\n";
            if (includeHardware && Capabilities is { } c)
                result += $"Adapter: {c.Adapter}; LUID: {c.AdapterLuid}; feature level: {c.FeatureLevel}; compute area: {c.ComputeAreaSupported}; WARP proven: {c.WarpUsable}\n";
            return result + (Failure is { } failure ? $"Last failure: {failure}\n" : "");
        }
    }
}
