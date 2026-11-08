namespace WinScreen.Settings;

/// <summary>Layered Glass and picture-safe Dark. Both are off by default and need experimental effects.</summary>
public sealed record EffectPreferences
{
    public bool LayeredGlass { get; init; }
    public bool PictureSafeDark { get; init; }
    public int BackgroundPercent { get; init; } = 15;
    public int TextPercent { get; init; } = 100;
    public int PicturePercent { get; init; } = 90;

    internal EffectPreferences Validated() => this with
    {
        BackgroundPercent = Math.Clamp(BackgroundPercent, 0, 100),
        TextPercent = Math.Clamp(TextPercent, 20, 100),
        PicturePercent = Math.Clamp(PicturePercent, 20, 100),
    };

    internal Glass.ContentLevels Levels => new(BackgroundPercent / 100f, TextPercent / 100f, PicturePercent / 100f);
}
