using System.Diagnostics;

namespace WinScreen.Pip;

/// <summary>One automatic recreation, then a stable fallback. Only an explicit retry reopens the circuit.</summary>
internal sealed class PipBackendPolicy
{
    private bool _recreated;
    private long? _fallbackAt;
    internal bool Pinned => _fallbackAt.HasValue;
    internal bool Fail(long now)
    {
        if (!_recreated && !Pinned) { _recreated = true; return true; }
        _fallbackAt = now;
        return false;
    }
    internal bool Retry(long now, long frequency)
    {
        if (_fallbackAt is not { } at || unchecked(now - at) < frequency * 10) return false;
        _recreated = false; _fallbackAt = null;
        return true;
    }
    internal void Reset() { _recreated = false; _fallbackAt = null; }
    internal void Pin(long now) => _fallbackAt = now;
}
