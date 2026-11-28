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
    // As _ownLevels, for what shows behind it. Empty for none.
    private readonly Dictionary<nint, BackdropSet> _ownBackgrounds = [];
    // Glass that makes only the page colour see-through, drawn here so the kept areas stay as they are.
    private readonly HashSet<nint> _plain = [];
    private Func<string, BackdropSet?> _backgroundOf = _ => null;
    private Func<string, IReadOnlyList<Settings.KeptArea>> _keptOf = _ => [];
    private readonly Dictionary<nint, string> _programs = [];
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    // Sound comes from one window at a time, the one with sound that was in front last: two windows playing the same
    // video would otherwise be heard twice, out of step. With SoundOnlyInFront, only while its app is the one in front.
    private nint _audible;
    private bool _front = true;
    private bool _soundOnlyInFront = true;
    private bool _held;

    public bool SoundOnlyInFront
    {
        get => _soundOnlyInFront;
        set
        {
            if (_soundOnlyInFront == value) return;
            _soundOnlyInFront = value;
            Hear();
        }
    }

    // Every background stops where it is, and is quiet, until let go.
    public void Hold(bool held)
    {
        if (_held == held) return;
        _held = held;
        foreach (ContentOverlay overlay in _overlays.Values) overlay.SetHeld(held);
    }

    public bool Held => _held;

    internal bool Hearing => _audible != 0 && (!_soundOnlyInFront || _front);

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
        LevelsChanged?.Invoke();
    }

    // Null goes back to the levels of the window's app.
    public void SetWindowLevels(nint window, ContentLevels? levels)
    {
        if (levels is { } own) _ownLevels[window] = own;
        else _ownLevels.Remove(window);
        if (_overlays.TryGetValue(window, out ContentOverlay? overlay)) overlay.SetLevels(LevelsOf(window));
        LevelsChanged?.Invoke();
    }

    // Levels of a window or app changed, as Dark without a graphics card needs to know.
    public event Action? LevelsChanged;

    public bool HasOwnLevels(nint window) => _ownLevels.ContainsKey(window);

    // Null goes back to the background of the window's app; a set of nothing shows none.
    public void SetWindowBackground(nint window, BackdropSet? own)
    {
        if (own is null) _ownBackgrounds.Remove(window);
        else _ownBackgrounds[window] = own;
        if (!_overlays.TryGetValue(window, out ContentOverlay? overlay) || overlay.Look != ContentLook.Glass) return;
        overlay.SetBackdrop(BackgroundOf(window));
        if (HasSound(window)) CameForward(window);
        else if (_audible == window)
        {
            TakeOver();
            Hear();
        }
    }

    public bool HasOwnBackground(nint window) => _ownBackgrounds.ContainsKey(window);

    internal int ShowingAt(nint window) => _overlays.TryGetValue(window, out ContentOverlay? overlay) ? overlay.ShowingAt : -1;

    // One of the window's set now, or its next for null.
    public void ShowBackground(nint window, int? index)
    {
        if (_overlays.TryGetValue(window, out ContentOverlay? overlay)) overlay.Jump(index);
    }

    public bool IsPlain(nint window) => _plain.Contains(window);

    internal ContentLevels LevelsOf(nint window) =>
        (_ownLevels.TryGetValue(window, out ContentLevels own) ? own : _levelsOf(ProgramOf(window))) with { Plain = _plain.Contains(window) };

    // Called for each window's program, by name without ".exe".
    public void SetBackgrounds(Func<string, BackdropSet?> backgroundOf)
    {
        _backgroundOf = backgroundOf;
        _reported.Clear();
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetBackdrop(BackgroundOf(window));
        if (_audible != 0 && !HasSound(_audible)) _audible = 0;
        if (_audible == 0) TakeOver();
        Hear();
    }

    public void SetKeptAreas(Func<string, IReadOnlyList<Settings.KeptArea>> keptOf)
    {
        _keptOf = keptOf;
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetKept(KeptOf(window));
    }

    private Settings.KeptArea[] KeptOf(nint window) => ProgramOf(window) is { Length: > 0 } program ? [.. _keptOf(program)] : [];

    internal BackdropSet? BackdropOf(nint window) => _overlays.TryGetValue(window, out ContentOverlay? overlay) ? overlay.Background : null;

    // The one of its set the window shows now.
    internal BackdropChoice? ShowingOf(nint window) => _overlays.TryGetValue(window, out ContentOverlay? overlay) ? overlay.Showing : null;

    internal nint Audible => _audible;

    private string ProgramOf(nint window)
    {
        if (!_programs.TryGetValue(window, out string? program)) _programs[window] = program = WindowInfo.ProgramOf(window);
        return program;
    }

    private BackdropSet? BackgroundOf(nint window) => _ownBackgrounds.TryGetValue(window, out BackdropSet? own) ? own.Items.Count > 0 ? own : null :
        ProgramOf(window) is { Length: > 0 } program ? _backgroundOf(program) : null;

    private bool HasSound(nint window) =>
        _overlays.TryGetValue(window, out ContentOverlay? overlay) && overlay.Look == ContentLook.Glass && overlay.Background is { HasSound: true };

    private void CameForward(nint window)
    {
        if (!HasSound(window)) return;
        _audible = window;
        _front = true;
        Hear();
    }

    private void TakeOver()
    {
        _audible = _overlays.Keys.FirstOrDefault(HasSound);
        _front = _audible != 0 && ContentOverlay.AppInFront(_audible) == true;
    }

    private void FrontMoved(nint window, bool front)
    {
        if (window != _audible || _front == front) return;
        _front = front;
        Hear();
    }

    // The window's background is the one heard, as when its sound was just turned up from its menu.
    public void HearFrom(nint window) => CameForward(window);

    private void Hear()
    {
        foreach ((nint window, ContentOverlay overlay) in _overlays)
            overlay.SetAudible(window == _audible && (!_soundOnlyInFront || _front));
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
        // Turned on again: a file or link that fails again is named again.
        if (overlay.Background is { } set) foreach (BackdropChoice item in set.Items) _reported.Remove(item.File);
        overlay.BackdropFailed += (file, missing) =>
        {
            // Once for each file, until the backgrounds change.
            if (!_reported.Add(file)) return;
            string name = BackdropChoice.IsLinkText(file) ? file : Path.GetFileName(file);
            Problem?.Invoke(string.Format(missing
                ? Localization.UiText.T("errors.glass.backdropMissing") : Localization.UiText.T("errors.glass.backdropNamed"), name));
        };
        overlay.CameForward += () => CameForward(window);
        overlay.FrontChanged += front => FrontMoved(window, front);
        overlay.SetHeld(_held);
        if (HasSound(window))
        {
            _audible = window; // Just turned on: what the person is looking at, unless another app is in front.
            _front = ContentOverlay.AppInFront(window) ?? true;
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
            // Not a window that closed or whose handle now belongs to another: there is nothing of it to show.
            if (Native.User32.IsWindow(window) && windows.IsConcealedBy(window, overlay))
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
        _ownBackgrounds.Remove(window);
        _plain.Remove(window);
        if (window != _audible) return;
        TakeOver();
        Hear();
    }

    public void TurnAllOff()
    {
        foreach (nint window in _overlays.Keys.ToArray())
            TurnOff(window);
    }

    public void DropClosed()
    {
        // Dark without a graphics card has no overlay here but can have levels of its own.
        foreach (nint window in _ownLevels.Keys.Where(window => !_overlays.ContainsKey(window) && !Native.User32.IsWindow(window)).ToArray())
            _ownLevels.Remove(window);
        foreach (nint window in _overlays.Keys.Where(window => !Native.User32.IsWindow(window)).ToArray())
            if (_overlays.TryGetValue(window, out ContentOverlay? overlay))
            {
                Forget(window);
                overlay.Dispose();
            }
    }

    public void Dispose() => TurnAllOff();
}
