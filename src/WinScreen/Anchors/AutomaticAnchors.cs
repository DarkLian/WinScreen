using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Anchors;

/// <summary>
/// Keeps an Anchor on windows that are out of sight. While the option is off, none of this exists: no
/// event hook, timer or pass. Anchors someone added themselves are never touched.
/// </summary>
internal sealed class AutomaticAnchors(AnchorSet anchors, Func<nint, bool> canAnchor, Func<bool> paused) : IDisposable
{
    private WindowEvents? _events;

    /// <summary>The event source, while there is one, for measuring its rate.</summary>
    internal WindowEvents? Events => _events;

    public void Configure(bool on)
    {
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
    internal void Pass() => anchors.SyncAutomatic(paused() ? [] :
        CoverTracker.FullyCovered(WindowStack.Read()).Where(canAnchor).ToList());

    public void Dispose()
    {
        _events?.Dispose();
        _events = null;
    }
}
