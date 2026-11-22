namespace WinScreen.Diagnostics;

// Something that may happen now and then, happening again and again. One "suspect" line per run of it, for whoever
// reads the log later; the person is told nothing.
public sealed class Suspicion
{
    public static Suspicion HookLostRepeatedly { get; } = new("hookLostRepeatedly", times: 3, within: TimeSpan.FromSeconds(30), quietFor: TimeSpan.FromMinutes(2));

    // Counted only when the pointer is still on the view: one passing over a view ends its visits quickly too.
    public static Suspicion VisitsEndAtOnce { get; } = new("visitsEndAtOnce", times: 3, within: TimeSpan.FromSeconds(10), quietFor: TimeSpan.FromMinutes(1));

    private readonly string _what;
    private readonly int _times;
    private readonly long _within, _quietFor;
    private readonly Queue<long> _recent = new();
    private readonly object _gate = new();
    private long _last;
    private bool _told;

    internal Suspicion(string what, int times, TimeSpan within, TimeSpan quietFor)
    {
        _what = what;
        _times = times;
        _within = (long)within.TotalMilliseconds;
        _quietFor = (long)quietFor.TotalMilliseconds;
    }

    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    // From any thread. True when this one wrote the line.
    public bool Note(params (string Key, LogValue Value)[] fields)
    {
        lock (_gate)
        {
            long now = Clock();
            if (_told && now - _last >= _quietFor)
            {
                _told = false;
                _recent.Clear();
            }
            _last = now;
            if (_told) return false;
            _recent.Enqueue(now);
            while (now - _recent.Peek() > _within) _recent.Dequeue();
            if (_recent.Count < _times) return false;
            _told = true;
            _recent.Clear();
        }
        Log.Event("suspect", [("what", LogValue.Known(_what)), ("times", _times), ("withinSeconds", (int)(_within / 1000)), .. fields]);
        return true;
    }
}
