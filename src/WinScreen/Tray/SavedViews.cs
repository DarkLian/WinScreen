using WinScreen.Pip;
using WinScreen.Settings;

namespace WinScreen.Tray;

/// <summary>
/// Which saved views have come back this session, and which sources wait to hide. One view per program:
/// ten browser windows shouldn't open ten views.
/// </summary>
internal sealed class SavedViews
{
    private readonly HashSet<string> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, IConcealable> _waiting = [];

    internal interface IConcealable
    {
        bool IsGone { get; }
        void Conceal();
    }

    private sealed class View(PipWindow pip) : IConcealable
    {
        public bool IsGone => pip.IsDisposed;
        public void Conceal() => pip.SetOriginalHidden(true);
    }

    public bool ShouldOpen(ProgramSetup setup, bool windowHasView) =>
        setup.View is not null && !windowHasView && !_opened.Contains(setup.Key);

    public void Opened(ProgramSetup setup) => _opened.Add(setup.Key);

    public void ConcealOnLeaving(nint window, PipWindow pip) => _waiting[window] = new View(pip);

    internal void ConcealOnLeaving(nint window, IConcealable view) => _waiting[window] = view;

    /// <summary>Hides each waiting source once another window has the foreground. Nothing to do costs one count.</summary>
    public void ConcealIfLeft(nint foreground)
    {
        if (_waiting.Count == 0 || foreground == 0)
            return;
        foreach ((nint window, IConcealable view) in _waiting.ToArray())
        {
            if (view.IsGone)
            {
                _waiting.Remove(window);
            }
            else if (window != foreground)
            {
                _waiting.Remove(window);
                view.Conceal();
            }
        }
    }
}
