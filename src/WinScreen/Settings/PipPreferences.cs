using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinScreen.Settings;

[JsonConverter(typeof(PipEnumConverter<PipBackendPreference>))]
public enum PipBackendPreference { Auto, Dwm, Wgc }
[JsonConverter(typeof(PipEnumConverter<PipScaling>))]
public enum PipScaling { Auto, Crisp, Smooth }

/// <summary>Preferences, never a persisted hardware blacklist. Zero FPS follows the destination monitor.</summary>
public sealed class PipPreferences
{
    public PipBackendPreference Backend { get; set; } = PipBackendPreference.Auto;
    public int FrameRate { get; set; } = 60;
    public int OpacityPercent { get; set; } = 100;
    public PipScaling Scaling { get; set; }
    public bool ShowDiagnostics { get; set; }
    public int PixelBudget { get; set; } = 8_294_400;
    public static readonly int[] FrameRates = [0, 15, 24, 30, 48, 60, 90, 120, 144, 165, 180, 200, 240, 280, 300, 360, 480];

    internal PipPreferences Validated() => new()
    {
        Backend = Enum.IsDefined(Backend) ? Backend : PipBackendPreference.Dwm,
        FrameRate = FrameRate == 0 ? 0 : Math.Clamp(FrameRate, 15, 480),
        OpacityPercent = Math.Clamp(OpacityPercent, 10, 100),
        Scaling = Enum.IsDefined(Scaling) ? Scaling : PipScaling.Auto,
        ShowDiagnostics = ShowDiagnostics,
        PixelBudget = Math.Clamp(PixelBudget, 65_536, 33_177_600),
    };

    internal byte Alpha => (byte)Math.Round(OpacityPercent * 255.0 / 100);
}

/// <summary>An unknown choice must not discard unrelated settings or opt a previous install into capture.</summary>
internal sealed class PipEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        T fallback = typeof(T) == typeof(PipBackendPreference) ? (T)(object)PipBackendPreference.Dwm : default;
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), true, out T value) &&
            Enum.IsDefined(value)) return value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) &&
            Enum.IsDefined(typeof(T), number)) return (T)Enum.ToObject(typeof(T), number);
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) reader.Skip();
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class PipPreferencesConverter : JsonConverter<PipPreferences>
{
    public override PipPreferences Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new() { Backend = PipBackendPreference.Dwm };
        int Number(string name, int fallback) => root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;
        T Choice<T>(string name, T fallback) where T : struct, Enum
        {
            if (!root.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.String && Enum.TryParse(value.GetString(), true, out T choice) &&
                Enum.IsDefined(choice)) return choice;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(typeof(T), number))
                return (T)Enum.ToObject(typeof(T), number);
            return typeof(T) == typeof(PipBackendPreference) ? (T)(object)PipBackendPreference.Dwm : default;
        }
        return new PipPreferences
        {
            Backend = Choice(nameof(PipPreferences.Backend), PipBackendPreference.Auto),
            FrameRate = Number(nameof(PipPreferences.FrameRate), 60),
            OpacityPercent = Number(nameof(PipPreferences.OpacityPercent), 100),
            Scaling = Choice(nameof(PipPreferences.Scaling), PipScaling.Auto),
            ShowDiagnostics = root.TryGetProperty(nameof(PipPreferences.ShowDiagnostics), out var diagnostics) && diagnostics.ValueKind == JsonValueKind.True,
            PixelBudget = Number(nameof(PipPreferences.PixelBudget), 8_294_400),
        }.Validated();
    }
    public override void Write(Utf8JsonWriter writer, PipPreferences value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
