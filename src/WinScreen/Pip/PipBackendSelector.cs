using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Settings;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

/// <summary>UI-thread coordinator. DWM stays registered while a separate worker proves first presentation.</summary>
internal sealed class PipBackendSelector : IPipRenderBackend
{
    private readonly DwmThumbnailBackend? _dwm;
    private readonly PipSource _source;
    private readonly nint _hwnd;
    private PipPreferences _preferences;
    private readonly PipBackendPolicy _policy = new();
    private IPipCaptureBackend? _advanced;
    private readonly Func<PipCaptureStart, IPipCaptureBackend> _createCapture;
    private bool _active, _disposed, _restart, _software, _warned;
    private bool _retiring;
    private bool _trySourceAdapter;
    private Rectangle _viewport, _crop;
    private Rectangle _visibleSource;
    private Size _client;
    private Rectangle? _selection;
    private byte _opacity;
    private bool _visible = true;
    private nint _monitor;
    private int _sourceHz, _targetHz;
    private long _lastDisplayCheck;
    private PipDiagnostics _lastDiagnostics = new();
    private readonly Queue<string> _failures = new();
    internal event Action<string>? Problem;
    internal bool CaptureBorderVisible => _active && _advanced is { } capture &&
        Interlocked.Read(ref capture.Diagnostics.Received) > 0 && !capture.Diagnostics.CaptureBorderHidden;

    public PipBackendKind Kind => _active ? _advanced!.Kind : PipBackendKind.DwmThumbnail;
    public PipSessionState State => _disposed ? PipSessionState.Stopped : _active ? _advanced!.State :
        _policy.Pinned ? (_dwm is null ? PipSessionState.Failed : PipSessionState.Fallback) : _advanced is not null ? PipSessionState.Starting : PipSessionState.Compositor;
    // Existing UI uses DWM window-surface coordinates. WGC coordinates are translated at the boundary below.
    public Size SourceSize => _dwm?.SourceSize ?? _source.SurfaceSize;
    internal bool CanRetry => _policy.Pinned && _preferences.Backend != PipBackendPreference.Dwm &&
        (_advanced is null || _advanced.Completion.IsCompleted);

    internal Task Retirement => _advanced?.Completion ?? Task.CompletedTask;
    internal string StatusText => State switch
    {
        PipSessionState.Starting or PipSessionState.Recovering => T("pip.state.starting"),
        PipSessionState.SourcePaused => T("pip.state.noNewPicture"),
        PipSessionState.Fallback => T("pip.state.fallback"),
        PipSessionState.Failed or PipSessionState.Stopped => T("common.unavailable"),
        _ => T("pip.state.live"),
    };

    internal PipBackendSelector(WindowIdentity identity, nint destination, PipPreferences preferences,
        Func<PipCaptureStart, IPipCaptureBackend>? createCapture = null)
        : this(new WindowPipSource(identity), destination, preferences, createCapture)
    {
    }

    internal PipBackendSelector(PipSource source, nint destination, PipPreferences preferences,
        Func<PipCaptureStart, IPipCaptureBackend>? createCapture = null)
    {
        source.Require();
        _source = source; _hwnd = destination; _preferences = Effective(preferences);
        _createCapture = createCapture ?? (start => new WgcCaptureSession(start.Source, start.Destination,
            start.Client, start.Monitor, start.Preferences, start.SourceHz, start.TargetHz, start.Software));
        _opacity = _preferences.Alpha;
        if (source.IsWindow)
        {
            _dwm = new DwmThumbnailBackend(destination, source.Window);
            _dwm.SetOpacity(_opacity);
        }
        Diagnostics.Log.Event("capture.view", ("preference", _preferences.Backend), ("window", source.IsWindow),
            ("program", source.IsWindow ? Diagnostics.LogValue.ProgramOf(source.Window) : Diagnostics.LogValue.Program("")));
    }

    /// <summary>What a failed capture leads to, for the log.</summary>
    private enum AfterFailure { Recreate, Software, Compositor, Nothing }

    /// <summary>A part of the screen has no compositor view, so the only way to show it is capture.</summary>
    private PipPreferences Effective(PipPreferences preferences)
    {
        PipPreferences valid = preferences.Validated();
        if (!_source.IsWindow) valid.Backend = PipBackendPreference.Wgc;
        return valid;
    }

    public void SetViewport(Rectangle destination, Rectangle source)
    {
        _viewport = destination; _crop = source;
        _dwm?.SetViewport(destination, source);
        UpdateAdvanced();
    }
    public void SetOpacity(byte opacity) { _opacity = opacity; _dwm?.SetOpacity(opacity); _advanced?.SetOpacity(opacity); }
    public void SetVisible(bool visible) { _visible = visible; _dwm?.SetVisible(visible && !_active); _advanced?.SetVisible(visible); }
    public void SetSelection(Rectangle? selection)
    {
        _selection = selection;
        _dwm?.SetSelection(_active ? null : selection);
        _advanced?.SetSelection(selection);
    }

    internal void Configure(PipPreferences preferences)
    {
        PipPreferences next = Effective(preferences);
        bool restart = next.Backend != _preferences.Backend || next.PixelBudget != _preferences.PixelBudget ||
            next.RequireHiddenBorder != _preferences.RequireHiddenBorder;
        _preferences = next;
        if (!restart)
        {
            if (_opacity != next.Alpha) SetOpacity(next.Alpha);
            _advanced?.SetPacing(next.FrameRate, next.Scaling);
            return;
        }
        UseDwm();
        _advanced?.Dispose();
        _retiring = _advanced is not null;
        _restart = _preferences.Backend != PipBackendPreference.Dwm;
        _software = false; _trySourceAdapter = false; _warned = false; _policy.Reset();
        SetOpacity(_preferences.Alpha);
    }

    private void UseDwm()
    {
        _dwm?.SetVisible(_visible);
        _active = false;
        _dwm?.SetSelection(_selection);
    }

    /// <exception cref="WindowOperationException">The source is gone, or a source with nothing to fall back on can't be shown.</exception>
    internal void Poll(Size client)
    {
        PollCapture(client);

    }

    private void PollCapture(Size client)
    {
        if (_disposed) return;
        _source.Require();
        _client = client;
        if (_preferences.Backend == PipBackendPreference.Dwm && _advanced is null) return;
        if (Stopwatch.GetElapsedTime(_lastDisplayCheck).TotalSeconds >= 1)
        {
            _lastDisplayCheck = Stopwatch.GetTimestamp();
            nint monitor = PipDisplay.MonitorFromWindow(_hwnd);
            _sourceHz = _source.RefreshRate; _targetHz = PipDisplay.CachedRefresh(_hwnd);
            if (_monitor != 0 && _monitor != monitor && _advanced is not null && !_policy.Pinned)
            {
                UseDwm(); _advanced.Dispose(); _retiring = true; _restart = true;
            }
            _monitor = monitor;
        }
        if (_advanced is null && _preferences.Backend != PipBackendPreference.Dwm && !_policy.Pinned)
            Start();
        if (_advanced is not { } advanced) return;
        if (_retiring)
        {
            if (!advanced.Completion.IsCompleted) return;
            _advanced = null; _retiring = false;
            if (_restart && !_policy.Pinned) { _restart = false; Start(); }
            return;
        }
        if (!advanced.Completion.IsCompleted && Stopwatch.GetElapsedTime(advanced.LastProgress).TotalSeconds > 7)
        {
            UseDwm(); advanced.Dispose(); _retiring = true; _restart = true;
            _lastDiagnostics = advanced.Diagnostics;
            _lastDiagnostics.Failure = "Capture/render worker exceeded its progress deadline.";
            Diagnostics.Log.Event("capture.hung", ("software", _software));
            // A hung driver cannot be forcibly disposed. Keep one retiring worker and never allocate another beside it.
            _policy.Pin(Stopwatch.GetTimestamp());
            if (!_warned && _dwm is not null) { _warned = true; Problem?.Invoke(T("errors.pip.advancedUnavailable")); }
            return;
        }
        if (advanced.State == PipSessionState.Failed && advanced.Completion.IsCompleted)
        {
            _lastDiagnostics = advanced.Diagnostics;
            UseDwm();
            if (_failures.Count == 4) _failures.Dequeue();
            _failures.Enqueue($"{_lastDiagnostics.Failure}\n{_lastDiagnostics.Exception?.StackTrace}");
            bool recreate = _policy.Fail(Stopwatch.GetTimestamp());
            _advanced = null;
            AfterFailure next = recreate ? AfterFailure.Recreate
                : !_software && _preferences.Backend == PipBackendPreference.Wgc ? AfterFailure.Software
                : _dwm is not null ? AfterFailure.Compositor : AfterFailure.Nothing;
            Diagnostics.Log.Event("capture.failed", ("software", _software), ("next", next),
                ("code", Diagnostics.LogValue.Code(_lastDiagnostics.Exception?.HResult ?? 0)));
            if (_lastDiagnostics.Exception is { } cause) Diagnostics.Log.Error("capture.failedBy", cause);
            if (recreate) { _trySourceAdapter = true; _restart = true; }
            else if (!_software && _preferences.Backend == PipBackendPreference.Wgc)
            {
                // WARP is only labelled usable after the same capture-to-present probe succeeds.
                _software = true; _policy.Reset(); _policy.Fail(Stopwatch.GetTimestamp()); _restart = true;
            }
            else
            {
                _restart = false;
                if (!_warned && _dwm is not null)
                {
                    _warned = true;
                    Problem?.Invoke(T("errors.pip.advancedUnavailable") + " " + _lastDiagnostics.Failure);
                }
            }
        }
        else if (advanced.Completion.IsCompleted)
        {
            UseDwm();
            _advanced = null;
        }
        else if (advanced.State is PipSessionState.Live or PipSessionState.SourcePaused)
        {
            if (!_active)
            {
                _dwm?.SetSelection(null);
                _dwm?.SetVisible(false);
                _active = true;
            }
        }
        if (_advanced is null && _restart && !_policy.Pinned) { _restart = false; Start(); }
        UpdateAdvanced();
    }

    private void Start()
    {
        if (_disposed || _advanced is not null || _client.Width <= 0) return;
        _source.Require();
        try
        {
            _advanced = _createCapture(new(_source, _hwnd, _client,
                _trySourceAdapter ? _source.Monitor : _monitor,
                _preferences, _sourceHz, _targetHz, _software));
            Diagnostics.Log.Event("capture.started", ("software", _software), ("sourceAdapter", _trySourceAdapter),
                ("sourceHz", _sourceHz), ("targetHz", _targetHz));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ExternalException or NotSupportedException or InvalidOperationException)
        {
            Diagnostics.Log.Error("capture.startFailed", e);
            _policy.Pin(Stopwatch.GetTimestamp());
            _lastDiagnostics.Failure = $"{e.GetType().Name} 0x{e.HResult:X8}";
            if (!_warned && _dwm is not null) { _warned = true; Problem?.Invoke(T("errors.pip.advancedUnavailable")); }
        }
        UpdateAdvanced();
    }

    private void UpdateAdvanced()
    {
        if (_advanced is not { } advanced) return;
        advanced.SetClient(_client);
        advanced.SetRefresh(_sourceHz, _targetHz);
        advanced.SetOpacity(_opacity);
        advanced.SetSelection(_selection);
        advanced.SetVisible(_visible);
        // WGC starts at the extended frame bounds; DWM's surface can include invisible resize borders.
        // Translate the shared DWM crop once, so click and crop geometry remain in their existing coordinate space.
        if (_visibleSource.IsEmpty || !_source.IsMinimized)
            _visibleSource = _source.Whole(SourceSize);
        Point origin = _source.CaptureOrigin(_visibleSource);
        Rectangle captureCrop = _crop;
        captureCrop.Offset(-origin.X, -origin.Y);
        advanced.SetViewport(_viewport, captureCrop);
    }

    /// <summary>The newest captured frame's tile brightness; null when smooth capture isn't what shows the picture.</summary>
    internal void RequestSample(Action<FrameSample?> done)
    {
        if (_active && _advanced is { } capture) capture.RequestSample(done);
        else done(null);
    }

    internal bool Retry()
    {
        if (!_policy.Retry(Stopwatch.GetTimestamp(), Stopwatch.Frequency)) return false;
        _software = false; _trySourceAdapter = false; _warned = false; _restart = true;
        return true;
    }
    internal void DisplayChanged()
    {
        _lastDisplayCheck = 0;
        _monitor = -1;
        PipDisplay.InvalidateRefresh();
    }
    internal string Snapshot()
    {
        var diagnostic = _advanced?.Diagnostics ?? _lastDiagnostics;
        diagnostic.Source = SourceSize;
        if (Kind == PipBackendKind.DwmThumbnail) diagnostic.Target = _viewport.Size;
        string snapshot = diagnostic.Snapshot(Kind, State, _preferences.FrameRate, _sourceHz, _targetHz, _preferences.ShowDiagnostics);
        if (_lastDiagnostics.Failure is { } previous && _advanced is not null)
            snapshot += $"Previous failure: {previous}\n";
        if (_preferences.ShowDiagnostics && (_advanced?.Diagnostics.Exception ?? _lastDiagnostics.Exception) is { } failure)
            snapshot += $"Failure stack: {failure.StackTrace}\n";
        if (_preferences.ShowDiagnostics && _failures.Count > 0)
            snapshot += "Recovery history:\n" + string.Join("\n", _failures) + "\n";
        return snapshot;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _advanced?.Dispose();
        _dwm?.Dispose();
    }
}
