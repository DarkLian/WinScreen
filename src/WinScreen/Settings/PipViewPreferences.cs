namespace WinScreen.Settings;

public enum PipHoverPreview { Off, WholeView, Lens }

public sealed record PipViewPreferences
{
    public PipHoverPreview HoverPreview { get; init; }
    public int HoverDelayMilliseconds { get; init; } = 600;
    public bool MinimizeToTaskbar { get; init; } = true;
    public bool KeepMinimizedOriginalsOnTaskbar { get; init; } = true;
    public bool AlwaysShowTitleBar { get; init; }
    public bool KeepHiddenOnNewViews { get; init; } = true;
    // Copy picture also saves a file, as Windows' Snipping Tool does; null folder is Windows' Screenshots folder.
    public bool SavePictures { get; init; } = true;
    public string? PictureFolder { get; init; }
    // Null is Windows' Screen Recordings folder under Videos, where the Snipping Tool saves.
    public string? RecordingFolder { get; init; }
    public bool RecordSound { get; init; }
    // Frames a second for recordings; 0 is the view's own rate.
    public int RecordingFrameRate { get; init; }

    internal PipViewPreferences Validated() => this with
    {
        HoverPreview = Enum.IsDefined(HoverPreview) ? HoverPreview : PipHoverPreview.Off,
        HoverDelayMilliseconds = Math.Clamp(HoverDelayMilliseconds, 250, 2000),
        PictureFolder = string.IsNullOrWhiteSpace(PictureFolder) ? null : PictureFolder.Trim(),
        RecordingFolder = string.IsNullOrWhiteSpace(RecordingFolder) ? null : RecordingFolder.Trim(),
        RecordingFrameRate = Pip.RecordingGeometry.ChoosableRates.Contains(RecordingFrameRate) ? RecordingFrameRate : 0,
    };
}
