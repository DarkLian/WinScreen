namespace WinScreen.Pip;

internal enum PipBackendKind { DwmThumbnail, WgcHardware, WgcSoftware }
internal enum PipSessionState { Starting, Live, SourcePaused, Recovering, Fallback, Stopped, Failed }

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

internal sealed record PipCaptureStart(Windows.WindowIdentity Identity, nint Destination, Size Client,
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
