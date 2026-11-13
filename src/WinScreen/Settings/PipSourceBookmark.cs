using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinScreen.Settings;

internal sealed record BookmarkDisplay(string Device, Rectangle Bounds, bool Primary = false);
internal sealed record ResolvedBookmark(BookmarkDisplay Display, Rectangle? Region, bool NeedsPreview);

/// <summary>A reusable capture source. Region shares are measured from the display's own corner.</summary>
public sealed class PipSourceBookmark
{
    public string Name { get; set; } = "";
    public string Device { get; set; } = "";
    public int DisplayX { get; set; }
    public int DisplayY { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public PipCropShares? Region { get; set; }
    [JsonConverter(typeof(SavedViewConverter))]
    public PipPreset? View { get; set; }

    internal PipSourceBookmark? Validated()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Device) ||
            DisplayWidth <= 0 || DisplayHeight <= 0 || Region is not null && Region.Validated() is null) return null;
        return new PipSourceBookmark
        {
            Name = Name.Trim()[..Math.Min(Name.Trim().Length, PipPreset.NameLimit)], Device = Device,
            DisplayX = DisplayX, DisplayY = DisplayY, DisplayWidth = DisplayWidth, DisplayHeight = DisplayHeight,
            Region = Region, View = View?.Validated(),
        };
    }

    internal ResolvedBookmark? Resolve(IReadOnlyList<BookmarkDisplay> displays)
    {
        BookmarkDisplay? display = displays.FirstOrDefault(item => item.Device == Device)
            ?? displays.FirstOrDefault(item => item.Primary) ?? displays.FirstOrDefault();
        if (display is null) return null;
        Rectangle saved = new(DisplayX, DisplayY, DisplayWidth, DisplayHeight);
        Rectangle? region = Region is { } shares
            ? Rectangle.Intersect(shares.Within(display.Bounds.Size), new Rectangle(Point.Empty, display.Bounds.Size)) : null;
        return new ResolvedBookmark(display, region, display.Device != Device || display.Bounds != saved);
    }
}

internal sealed class PipBookmarkListConverter : JsonConverter<List<PipSourceBookmark>>
{
    public override List<PipSourceBookmark> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var bookmarks = new List<PipSourceBookmark>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return bookmarks;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<PipSourceBookmark>()?.Validated() is { } bookmark) bookmarks.Add(bookmark);
            }
            catch (JsonException caught) { Diagnostics.Log.Swallowed(caught); }
        }
        return bookmarks;
    }

    public override void Write(Utf8JsonWriter writer, List<PipSourceBookmark> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (PipSourceBookmark bookmark in value) JsonSerializer.Serialize(writer, bookmark, options);
        writer.WriteEndArray();
    }
}
