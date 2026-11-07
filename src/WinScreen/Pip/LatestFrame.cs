namespace WinScreen.Pip;

/// <summary>One owned pending item, one consumer. Replacements and late publication are disposed immediately.</summary>
internal sealed class LatestFrame<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _pending;
    private bool _closed;
    public long Dropped { get; private set; }
    internal bool HasPending { get { lock (_gate) return _pending is not null; } }
    public bool Publish(T frame)
    {
        lock (_gate)
        {
            if (_closed) { frame.Dispose(); return false; }
            if (_pending is not null) { _pending.Dispose(); Dropped++; }
            _pending = frame;
            return true;
        }
    }
    public T? Take() { lock (_gate) { T? frame = _pending; _pending = null; return frame; } }
    public void Dispose() { lock (_gate) { _closed = true; _pending?.Dispose(); _pending = null; } }
}
