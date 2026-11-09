using System.Diagnostics;

namespace WinScreen.Pip;

/// <summary>
/// How long the mouse hook's callback takes, kept lock-free and bounded. Windows drops a low-level hook that
/// answers slowly, so this is what shows whether the callback stays cheap. Holds durations only.
/// </summary>
internal sealed class PipHookTiming
{
    // Bucket upper bounds in microseconds; the last bucket takes everything slower.
    private static readonly long[] Bounds = [50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000, 200_000];
    private readonly long[] _counts = new long[Bounds.Length + 1];
    private long _count, _max, _total;

    internal static long Start() => Stopwatch.GetTimestamp();

    internal void Stop(long started)
    {
        long micros = Stopwatch.GetElapsedTime(started).Ticks / 10;
        int bucket = 0;
        while (bucket < Bounds.Length && micros > Bounds[bucket]) bucket++;
        Interlocked.Increment(ref _counts[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _total, micros);
        long max;
        while (micros > (max = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, micros, max) != max) { }
    }

    internal long Count => Interlocked.Read(ref _count);
    internal TimeSpan Max => TimeSpan.FromTicks(Interlocked.Read(ref _max) * 10);

    /// <summary>The bucket bound below which at least this share of callbacks finished.</summary>
    internal TimeSpan Percentile(double share)
    {
        long count = Count, seen = 0;
        if (count == 0) return TimeSpan.Zero;
        for (int bucket = 0; bucket < _counts.Length; bucket++)
        {
            seen += Interlocked.Read(ref _counts[bucket]);
            if (seen >= count * share)
                return bucket < Bounds.Length ? TimeSpan.FromTicks(Bounds[bucket] * 10) : Max;
        }
        return Max;
    }

    /// <summary>How many callbacks took longer than the given time.</summary>
    internal long Over(TimeSpan time)
    {
        long micros = time.Ticks / 10, over = 0;
        for (int bucket = 0; bucket < _counts.Length; bucket++)
            if (bucket == 0 ? false : Bounds[bucket - 1] >= micros) over += Interlocked.Read(ref _counts[bucket]);
        return over;
    }

    public override string ToString() =>
        $"hook callbacks={Count} mean={(Count == 0 ? 0 : Interlocked.Read(ref _total) / Count)}us p50<={Percentile(.5).TotalMilliseconds}ms " +
        $"p99<={Percentile(.99).TotalMilliseconds}ms max={Max.TotalMilliseconds:0.###}ms over10ms={Over(TimeSpan.FromMilliseconds(10))}";
}
