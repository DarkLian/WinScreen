using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Anchors;

/// <summary>
/// Keeps an Anchor on windows that are out of sight. While the option is off, none of this exists: no
/// event hook, timer or pass. Anchors someone added themselves are never touched.
/// </summary>
internal sealed class AutomaticAnchors(AnchorSet anchors, Func<nint, bool> canAnchor, Func<bool> paused) : IDisposable
{
    private const int ContentCheckMilliseconds = 3000;

    private WindowEvents? _events;
    private bool _avoidContent;
    private long _nextContentCheck;

    /// <summary>The event source, while there is one, for measuring its rate.</summary>
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

    /// <summary>Compares what is covered with the Anchors on screen. Nothing shows behind a game or full-screen app.</summary>
    internal void Pass()
    {
        bool stopped = paused();
        anchors.SyncAutomatic(stopped ? [] : CoverTracker.FullyCovered(WindowStack.Read()).Where(canAnchor).ToList());
        // Looking at the screen is the costly part, so it follows window changes and never runs often.
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
