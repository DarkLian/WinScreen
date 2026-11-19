namespace WinScreen.Settings;

// On by default (plain Glass passes presses through its see-through page); a PC with no graphics card gets plain Glass.
public sealed record EffectPreferences
{
    public bool LayeredGlass { get; init; } = true;
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int FadeRangePercent { get; init; } = 100;

    internal EffectPreferences Validated() => this with
    {
        BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
        TextPercent = Math.Clamp(TextPercent, 20, 100),
        FadeRangePercent = Math.Clamp(FadeRangePercent, 10, 100),
    };

    internal Glass.ContentLevels Levels => new(BackgroundPercent / 100f, TextPercent / 100f, FadeRangePercent / 100f);
}
