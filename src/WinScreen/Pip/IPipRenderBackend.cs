namespace WinScreen.Pip;

internal enum PipBackendKind { DwmThumbnail, WgcHardware, WgcSoftware }
// Compositor: Windows owns the picture, so WinScreen can't say whether it's current.
internal enum PipSessionState { Starting, Live, SourcePaused, Recovering, Fallback, Stopped, Failed, Compositor }

// Rendering only: source identity, journal, input and window ownership stay in PipWindow.
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

// DWM never implements a fabricated frame stream.
internal interface IPipCaptureBackend : IPipRenderBackend
{
    Task Completion { get; }
    PipDiagnostics Diagnostics { get; }
    long LastProgress { get; }
    void SetClient(Size client);
    void SetRefresh(int source, int target);

    void SetPacing(int frameRate, Settings.PipScaling scaling) { }
}
