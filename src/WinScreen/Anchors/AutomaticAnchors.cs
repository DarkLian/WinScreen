using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Anchors;

// alsoCovered: windows that are covered though lifted over the rest, see-through, so their view keeps drawing.
internal sealed class AutomaticAnchors(AnchorSet anchors, Func<nint, bool> canAnchor, Func<bool> paused,
    Func<IEnumerable<nint>>? alsoCovered = null) : IDisposable
{
    private const int ContentCheckMilliseconds = 3000;

    private WindowEvents? _events;
    private bool _avoidContent;
    private long _nextContentCheck;

    internal WindowEvents? Events => _events;

    public void Configure(bool on, bool avoidContent = false)
    {
        _avoidContent = avoidContent;
        if (on == _events is not null)
        {
            if (on) Pass();
            return;
        }

        if (on)
        {
            _events = new WindowEvents();
            _events.Changed += Pass;
            Pass();
        }
        else
        {
            _events!.Dispose();
            _events = null;
            anchors.SyncAutomatic([]);
        }
    }

    internal void Pass()
    {
        bool stopped = paused();
        anchors.SyncAutomatic(stopped ? [] : CoverTracker.FullyCovered(WindowStack.Read()).Concat(alsoCovered?.Invoke() ?? [])
            .Distinct().Where(canAnchor).ToList());
        if (stopped || !_avoidContent || Environment.TickCount64 < _nextContentCheck || anchors.Automatic.Count == 0)
            return;
        _nextContentCheck = Environment.TickCount64 + ContentCheckMilliseconds;
        anchors.AvoidContent(Pip.ScreenContent.BusyCells);
    }

    public void Dispose()
    {
        _events?.Dispose();
        _events = null;
    }
}
