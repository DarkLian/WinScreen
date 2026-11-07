namespace WinScreen.Pip;

/// <summary>Fractional monotonic deadlines. Late sources never build a catch-up queue.</summary>
internal sealed class PipFramePacer(long frequency, double framesPerSecond)
{
    private double _interval = frequency / Math.Clamp(framesPerSecond, 1, 480);
    private long _origin;
    private double _next;
    private bool _started;

    public void SetRate(double fps)
    {
        _interval = frequency / Math.Clamp(fps, 1, 480);
        _started = false;
    }

    public bool Accept(long timestamp)
    {
        if (!_started)
        {
            _origin = timestamp;
            _next = _interval;
            _started = true;
            return true;
        }
        // Subtraction before floating point conversion also works across signed QPC wraparound.
        long elapsed = unchecked(timestamp - _origin);
        if (elapsed < 0 || elapsed > _next + frequency)
        {
            _started = false;
            return Accept(timestamp);
        }
        if (elapsed + Math.Min(_interval * 0.25, frequency * 0.0011) < _next) return false;
        _next = Math.Max(_next + _interval, elapsed + _interval * 0.5);
        return true;
    }
}
