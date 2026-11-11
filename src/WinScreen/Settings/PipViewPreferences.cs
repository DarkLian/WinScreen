namespace WinScreen.Settings;

public enum PipHoverPreview { Off, WholeView, Lens }

public sealed record PipViewPreferences
{
    public PipHoverPreview HoverPreview { get; init; }
    public int HoverDelayMilliseconds { get; init; } = 600;
    public bool MinimizeToTaskbar { get; init; }
    /// <summary>New views keep their title bar showing instead of only while the pointer is over them.</summary>
    public bool AlwaysShowTitleBar { get; init; }

    internal PipViewPreferences Validated() => this with
    {
        HoverPreview = Enum.IsDefined(HoverPreview) ? HoverPreview : PipHoverPreview.Off,
        HoverDelayMilliseconds = Math.Clamp(HoverDelayMilliseconds, 250, 2000),
    };
}
