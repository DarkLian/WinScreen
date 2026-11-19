using WinScreen.Windows;

namespace WinScreen.Glass;

internal sealed class ContentEffects : IDisposable
{
    private readonly WindowController windows;
    private readonly Dictionary<nint, ContentOverlay> _overlays = [];

    public ContentEffects(WindowController windows)
    {
        this.windows = windows;
        windows.OpacityChanged += (window, alpha) =>
        {
            if (_overlays.TryGetValue(window, out ContentOverlay? overlay) && !overlay.IsDisposed) overlay.SetOpacity(alpha);
        };
    }
    private Func<string, ContentLevels> _levelsOf = _ => new(0.15f, 1);
    // Set from a window's own Glass panel, for that window only, until it closes or its app's levels are saved.
    private readonly Dictionary<nint, ContentLevels> _ownLevels = [];
    // Glass that makes only the page colour see-through, drawn here so the kept areas stay as they are.
    private readonly HashSet<nint> _plain = [];
    private Func<string, BackdropChoice?> _backgroundOf = _ => null;
    private Func<string, IReadOnlyList<Settings.KeptArea>> _keptOf = _ => [];
    private readonly Dictionary<nint, string> _programs = [];
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    // Sound comes from one window at a time, the one with sound that was in front last: two windows playing the same
    // video would otherwise be heard twice, out of step.
    private nint _audible;

    public bool LeaveAlreadyDark { get; set; } = true;

    public event Action<string>? Problem;

    public event Action<nint>? Created;

    public bool Has(nint window, ContentLook look) => _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.Look == look;

    public bool Has(nint window) => _overlays.ContainsKey(window);

    public int Count => _overlays.Count;
    internal IEnumerable<nint> Sources => _overlays.Keys;

    internal int RestacksOf(nint window) => _overlays.TryGetValue(window, out ContentOverlay? overlay) ? overlay.Restacks : 0;

    internal nint OverlayOf(nint window) =>
        _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.IsHandleCreated ? overlay.Handle : 0;

    public void SetLevels(ContentLevels levels) => SetLevels(_ => levels);

    // Called for each window's program, by name without ".exe".
    public void SetLevels(Func<string, ContentLevels> levelsOf)
    {
        _levelsOf = levelsOf;
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetLevels(LevelsOf(window));
    }

    // Null goes back to the levels of the window's app.
    public void SetWindowLevels(nint window, ContentLevels? levels)
    {
        if (levels is { } own) _ownLevels[window] = own;
        else _ownLevels.Remove(window);
        if (_overlays.TryGetValue(window, out ContentOverlay? overlay)) overlay.SetLevels(LevelsOf(window));
    }

    public bool HasOwnLevels(nint window) => _ownLevels.ContainsKey(window);

    public bool IsPlain(nint window) => _plain.Contains(window);

    internal ContentLevels LevelsOf(nint window) =>
        (_ownLevels.TryGetValue(window, out ContentLevels own) ? own : _levelsOf(ProgramOf(window))) with { Plain = _plain.Contains(window) };

    // Called for each window's program, by name without ".exe".
    public void SetBackgrounds(Func<string, BackdropChoice?> backgroundOf)
    {
        _backgroundOf = backgroundOf;
        _reported.Clear();
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetBackdrop(BackgroundOf(window));
        if (_audible != 0 && !HasSound(_audible)) _audible = 0;
        if (_audible == 0) _audible = _overlays.Keys.FirstOrDefault(HasSound);
        Hear();
    }

    public void SetKeptAreas(Func<string, IReadOnlyList<Settings.KeptArea>> keptOf)
    {
        _keptOf = keptOf;
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetKept(KeptOf(window));
    }

    private Settings.KeptArea[] KeptOf(nint window) => ProgramOf(window) is { Length: > 0 } program ? [.. _keptOf(program)] : [];

    internal BackdropChoice? BackdropOf(nint window) => _overlays.TryGetValue(window, out ContentOverlay? overlay) ? overlay.Background : null;

    internal nint Audible => _audible;

    private string ProgramOf(nint window)
    {
        if (!_programs.TryGetValue(window, out string? program)) _programs[window] = program = WindowInfo.ProgramOf(window);
        return program;
    }

    private BackdropChoice? BackgroundOf(nint window) => ProgramOf(window) is { Length: > 0 } program ? _backgroundOf(program) : null;

    private bool HasSound(nint window) =>
        _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.Look == ContentLook.Glass && overlay.Background is { Volume: > 0 };

    private void CameForward(nint window)
    {
        if (window == _audible || !HasSound(window)) return;
        _audible = window;
        Hear();
    }

    private void Hear()
    {
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetAudible(window == _audible);
    }

    public bool Toggle(nint window, ContentLook look, bool plain = false)
    {
        if (_overlays.TryGetValue(window, out ContentOverlay? existing))
        {
            TurnOff(window);
            if (_overlays.ContainsKey(window) || existing.Look == look)
                return false;
        }

        if (plain && look == ContentLook.Glass) _plain.Add(window);
        else _plain.Remove(window);
        var overlay = new ContentOverlay(window, look, LevelsOf(window), LeaveAlreadyDark, look == ContentLook.Glass ? BackgroundOf(window) : null, KeptOf(window));
        try
        {
            byte opacity = windows.OpacityOf(window);
            windows.HideOwned(window, overlay, solid: true);
            overlay.Start();
            overlay.SetOpacity(opacity);
        }
        catch
        {
            if (windows.IsConcealedBy(window, overlay))
            {
                try { windows.ShowOwned(window, overlay); }
                catch (WindowOperationException)
                {
                    _overlays[window] = overlay;
                    throw;
                }
            }
            overlay.Dispose();
            throw;
        }

        _overlays[window] = overlay;
        overlay.Failed += message =>
        {
            TurnOff(window);
            Problem?.Invoke(message);
        };
        overlay.BackdropFailed += message =>
        {
            // Once for each file, until the backgrounds change.
            if (overlay.Background is { } failed && !_reported.Add(failed.File)) return;
            Problem?.Invoke(message);
        };
        overlay.CameForward += () => CameForward(window);
        if (HasSound(window))
        {
            _audible = window; // Just turned on: what the person is looking at.
            Hear();
        }
        Created?.Invoke(overlay.Handle);
        return true;
    }

    public void TurnOff(nint window)
    {
        if (!_overlays.TryGetValue(window, out ContentOverlay? overlay))
            return;
        try
        {
            if (Native.User32.IsWindow(window))
                windows.ShowOwned(window, overlay);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
            return;
        }
        Forget(window);
        overlay.Dispose();
    }

    private void Forget(nint window)
    {
        _overlays.Remove(window);
        _programs.Remove(window);
        _ownLevels.Remove(window);
        _plain.Remove(window);
        if (window != _audible) return;
        _audible = _overlays.Keys.FirstOrDefault(HasSound);
        Hear();
    }

    public void TurnAllOff()
    {
        foreach (nint window in _overlays.Keys.ToArray())
            TurnOff(window);
    }

    public void DropClosed()
    {
        foreach (nint window in _overlays.Keys.Where(window => !Native.User32.IsWindow(window)).ToArray())
            if (_overlays.TryGetValue(window, out ContentOverlay? overlay))
            {
                Forget(window);
                overlay.Dispose();
            }
    }

    public void Dispose() => TurnAllOff();
}
