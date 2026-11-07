using System.Globalization;
using System.Text.Json.Serialization;

namespace WinScreen.Settings;

public sealed record PinBorderPreferences
{
    public string ColorHex { get; init; } = "#E82020";
    public int OpacityPercent { get; init; } = 100;
    public int Thickness { get; init; } = 3;

    public PinBorderPreferences Validated() => this with
    {
        ColorHex = ColorHex is { Length: 7 } && ColorHex[0] == '#' &&
            int.TryParse(ColorHex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? ColorHex.ToUpperInvariant() : "#E82020",
        OpacityPercent = Math.Clamp(OpacityPercent, 10, 100),
        Thickness = Math.Clamp(Thickness, 1, 12),
    };

    [JsonIgnore]
    internal Color Color => ColorTranslator.FromHtml(Validated().ColorHex);
}
