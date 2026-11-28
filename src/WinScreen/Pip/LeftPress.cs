namespace WinScreen.Pip;

// The left button between the hook (or the view), which sees it go down and up, and the input worker, which may hand a
// press to the real window for a drag. One word holds the press's number and where it stands, and each side changes it
// whole: a release can't slip between the worker's look and its hand-over, and a move left from an earlier press can't
// hand over a newer one. A quick click with a twitch was picked up as a drag after its release (TD-77).
internal sealed class LeftPress
{
    private const long Held = 1, HandedOver = 2, Phase = 3;
    private long _state; // The press's number times four, plus its phase: 0 once let go.
    private long _presses;

    // Hook or view: the button went down. The number goes with the press to the worker.
    internal long Pressed()
    {
        long number = Interlocked.Increment(ref _presses);
        Interlocked.Exchange(ref _state, number << 2 | Held);
        return number;
    }

    // Hook or view: the button went up. True when the worker had handed the press over: the release is the real window's.
    internal bool Released()
    {
        long now;
        do now = Volatile.Read(ref _state);
        while (Interlocked.CompareExchange(ref _state, now & ~Phase, now) != now);
        return (now & Phase) == HandedOver;
    }

    // Worker: only while that same press is still held.
    internal bool HandOver(long number) =>
        Interlocked.CompareExchange(ref _state, number << 2 | HandedOver, number << 2 | Held) == (number << 2 | Held);
}
