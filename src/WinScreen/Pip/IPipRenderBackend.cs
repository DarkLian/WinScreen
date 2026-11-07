namespace WinScreen.Pip;

internal enum PipBackendKind { DwmThumbnail, WgcHardware, WgcSoftware }
/// <summary><see cref="Compositor"/> means Windows owns the picture, so WinScreen can't say whether it's current.</summary>
internal enum PipSessionState { Starting, Live, SourcePaused, Recovering, Fallback, Stopped, Failed, Compositor }

/// <summary>Owns rendering only. Source identity, journal, input and HWND ownership stay in PipWindow.</summary>
internal interface IPipRenderBackend : IDisposable
{
    PipBackendKind Kind { get; }
    PipSessionState State { get; }
    Size SourceSize { get; }
    void SetViewport(Rectangle destination, Rectangle source);
    void SetOpacity(byte opacity);
    void SetVisible(bool visible);
    void SetSelection(Rectangle? selection);
}

internal sealed record PipCaptureStart(PipSource Source, nint Destination, Size Client,
    nint Monitor, Settings.PipPreferences Preferences, int SourceHz, int TargetHz, bool Software);

/// <summary>Capture-only lifetime and scheduling. DWM never implements a fabricated frame stream.</summary>
internal interface IPipCaptureBackend : IPipRenderBackend
{
    Task Completion { get; }
    PipDiagnostics Diagnostics { get; }
    long LastProgress { get; }
    void SetClient(Size client);
    void SetRefresh(int source, int target);
}
