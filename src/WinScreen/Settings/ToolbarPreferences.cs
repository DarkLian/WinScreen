using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinScreen.Settings;

public enum ToolbarPlacement { Left, Centre, Right }
public enum ToolbarTrigger { TitleBar, MinimizeButton }

public sealed record ToolbarPreferences
{
    public ToolbarPlacement Placement { get; init; } = ToolbarPlacement.Centre;
    public ToolbarTrigger Trigger { get; init; }
    public int HoverDelayMilliseconds { get; init; } = 500;
    [JsonConverter(typeof(ToolbarButtonListConverter))]
    public List<AppCommand> Buttons { get; init; } = DefaultButtons();

    internal static List<AppCommand> DefaultButtons() => [.. CommandCatalog.Ordered.Where(command => command != AppCommand.RestoreAll)];

    internal ToolbarPreferences Validated() => this with
    {
        Placement = Enum.IsDefined(Placement) ? Placement : ToolbarPlacement.Centre,
        Trigger = Enum.IsDefined(Trigger) ? Trigger : ToolbarTrigger.TitleBar,
        HoverDelayMilliseconds = Math.Clamp(HoverDelayMilliseconds, 100, 2000),
        Buttons = [.. DefaultButtons().Where(command => (Buttons ?? DefaultButtons()).Contains(command))],
    };
}

/// <summary>
/// Saves button names, so reordering commands can't change someone's choice. A name this version
/// doesn't know is skipped: it must never cost the rest of the settings.
/// </summary>
internal sealed class ToolbarButtonListConverter : JsonConverter<List<AppCommand>>
{
    public override List<AppCommand> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return ToolbarPreferences.DefaultButtons();
        var buttons = new List<AppCommand>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && Enum.TryParse(element.GetString(), true, out AppCommand named) &&
                Enum.IsDefined(named))
                buttons.Add(named);
            else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) &&
                     Enum.IsDefined(typeof(AppCommand), number))
                buttons.Add((AppCommand)number);
        }

        return buttons;
    }

    public override void Write(Utf8JsonWriter writer, List<AppCommand> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (AppCommand command in value)
            writer.WriteStringValue(command.ToString());
        writer.WriteEndArray();
    }
}
