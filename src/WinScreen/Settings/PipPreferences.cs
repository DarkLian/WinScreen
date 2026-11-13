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
    internal bool RequireHiddenBorder { get; set; }
    public int PixelBudget { get; set; } = 8_294_400;
    public static readonly int[] FrameRates = [0, 15, 24, 30, 48, 60, 90, 120, 144, 165, 180, 200, 240, 280, 300, 360, 480];

    internal PipPreferences Validated() => new()
    {
        Backend = Enum.IsDefined(Backend) ? Backend : PipBackendPreference.Dwm,
        FrameRate = FrameRate == 0 ? 0 : Math.Clamp(FrameRate, 15, 480),
        OpacityPercent = Math.Clamp(OpacityPercent, 10, 100),
        Scaling = Enum.IsDefined(Scaling) ? Scaling : PipScaling.Auto,
        ShowDiagnostics = ShowDiagnostics,
        RequireHiddenBorder = RequireHiddenBorder,
        PixelBudget = Math.Clamp(PixelBudget, 65_536, 33_177_600),
    };

    internal byte Alpha => (byte)Math.Round(OpacityPercent * 255.0 / 100);

    /// <summary>These preferences as one view uses them. The originals are left alone.</summary>
    internal PipPreferences With(PipOverrides overrides)
    {
        PipPreferences result = Validated();
        result.FrameRate = overrides.FrameRate ?? result.FrameRate;
        result.OpacityPercent = overrides.OpacityPercent ?? result.OpacityPercent;
        result.Scaling = overrides.Scaling ?? result.Scaling;
        return result.Validated();
    }
}

/// <summary>What one view does differently from Settings. It ends with the view unless saved as a preset.</summary>
internal sealed record PipOverrides(int? FrameRate = null, int? OpacityPercent = null, PipScaling? Scaling = null,
    bool PositionLocked = false, bool ClickThrough = false, bool KeepAspectRatio = true, bool? TitleBarAlways = null);

/// <summary>A crop as shares of the whole source, so it still fits after the window is resized.</summary>
public sealed record PipCropShares(double X, double Y, double Width, double Height)
{
    internal PipCropShares? Validated() =>
        new[] { X, Y, Width, Height }.All(double.IsFinite) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        X + Width <= 1.0001 && Y + Height <= 1.0001 ? this : null;

    internal static PipCropShares? Of(Rectangle crop, Size whole) => crop.IsEmpty || whole.Width <= 0 || whole.Height <= 0
        ? null
        : new PipCropShares((double)crop.X / whole.Width, (double)crop.Y / whole.Height,
            (double)crop.Width / whole.Width, (double)crop.Height / whole.Height).Validated();

    internal Rectangle Within(Size whole)
    {
        int left = (int)Math.Round(X * whole.Width), top = (int)Math.Round(Y * whole.Height);
        return new Rectangle(left, top, Math.Max(1, (int)Math.Round(Width * whole.Width)),
            Math.Max(1, (int)Math.Round(Height * whole.Height)));
    }
}

/// <summary>A named view setup. Null values follow Settings. A crop is in pixels of the source,
/// with shares alongside it in newer files; older files have pixels only and keep working.</summary>
public sealed class PipPreset
{
    public const int NameLimit = 60;

    public string Name { get; set; } = "";
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public int LongSide { get; set; }
    public string? Device { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public int? FrameRate { get; set; }
    public int? OpacityPercent { get; set; }
    public PipScaling? Scaling { get; set; }
    public PipCropShares? CropShares { get; set; }
    public bool? PositionLocked { get; set; }
    public bool? TitleBarAlways { get; set; }

    [JsonIgnore]
    public Rectangle Crop => CropWidth > 0 && CropHeight > 0
        ? new Rectangle(CropX, CropY, CropWidth, CropHeight)
        : Rectangle.Empty;

    /// <summary>The crop for a source of this size.</summary>
    internal Rectangle CropFor(Size whole) =>
        CropShares is { } shares && whole.Width > 0 && whole.Height > 0 ? shares.Within(whole) : Crop;

    /// <summary>This preset with its numbers in range, or null when it has no usable name.</summary>
    internal PipPreset? Validated()
    {
        string name = (Name ?? "").Trim();
        if (name.Length == 0) return null;
        return new PipPreset
        {
            Name = name.Length > NameLimit ? name[..NameLimit] : name,
            CropX = CropX, CropY = CropY, CropWidth = Math.Max(0, CropWidth), CropHeight = Math.Max(0, CropHeight),
            LongSide = Math.Clamp(LongSide, 0, 16384),
            Device = Device,
            Left = double.IsFinite(Left) ? Math.Clamp(Left, 0, 1) : 0,
            Top = double.IsFinite(Top) ? Math.Clamp(Top, 0, 1) : 0,
            FrameRate = FrameRate is { } rate ? (rate == 0 ? 0 : Math.Clamp(rate, 15, 480)) : null,
            OpacityPercent = OpacityPercent is { } opacity ? Math.Clamp(opacity, 10, 100) : null,
            Scaling = Scaling is { } scaling && Enum.IsDefined(scaling) ? scaling : null,
            CropShares = CropShares?.Validated(),
            PositionLocked = PositionLocked,
            TitleBarAlways = TitleBarAlways,
        };
    }
}

/// <summary>A damaged saved view is dropped. It must never cost the rest of the settings.</summary>
internal sealed class SavedViewConverter : JsonConverter<PipPreset>
{
    public override PipPreset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try { return document.RootElement.Deserialize<PipPreset>()?.Validated(); }
        catch (JsonException) { return null; }
    }

    public override void Write(Utf8JsonWriter writer, PipPreset value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value);
}

/// <summary>One damaged preset is skipped. It must never cost the rest of the settings.</summary>
internal sealed class PipPresetListConverter : JsonConverter<List<PipPreset>>
{
    public override List<PipPreset> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var presets = new List<PipPreset>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return presets;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<PipPreset>()?.Validated() is { } preset)
                    presets.Add(preset);
            }
            catch (JsonException caught)
            {
                Diagnostics.Log.Swallowed(caught);
            }
        }

        return presets;
    }

    public override void Write(Utf8JsonWriter writer, List<PipPreset> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (PipPreset preset in value)
            JsonSerializer.Serialize(writer, preset);
        writer.WriteEndArray();
    }
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
