namespace WinScreen.Settings;

public enum PipHoverPreview { Off, WholeView, Lens }

public sealed record PipViewPreferences
{
    public PipHoverPreview HoverPreview { get; init; }
    public int HoverDelayMilliseconds { get; init; } = 600;
    public bool MinimizeToTaskbar { get; init; }
    public bool AlwaysShowTitleBar { get; init; }
    public bool KeepHiddenOnNewViews { get; init; } = true;

    internal PipViewPreferences Validated() => this with
    {
        HoverPreview = Enum.IsDefined(HoverPreview) ? HoverPreview : PipHoverPreview.Off,
        HoverDelayMilliseconds = Math.Clamp(HoverDelayMilliseconds, 250, 2000),
    };
}
