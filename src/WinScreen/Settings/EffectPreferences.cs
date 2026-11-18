namespace WinScreen.Settings;

// On by default (plain Glass passes presses through its see-through page); a PC with no graphics card gets plain Glass.
public sealed record EffectPreferences
{
    public bool LayeredGlass { get; init; } = true;
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public string BackdropFile { get; init; } = "";
    public Glass.BackdropFit BackdropFit { get; init; } = Glass.BackdropFit.Fill;

    internal EffectPreferences Validated() => this with
    {
        BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
        TextPercent = Math.Clamp(TextPercent, 20, 100),
        BackdropFile = BackdropFile?.Trim() ?? "",
        BackdropFit = Enum.IsDefined(BackdropFit) ? BackdropFit : Glass.BackdropFit.Fill,
    };

    internal Glass.BackdropChoice? Backdrop => string.IsNullOrEmpty(BackdropFile) ? null : new(BackdropFile, BackdropFit);

    internal Glass.ContentLevels Levels => new(BackgroundPercent / 100f, TextPercent / 100f);
}
