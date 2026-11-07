using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace WinScreen.Localization;

/// <summary>Embedded translations addressed by stable keys. Program names and hotkeys stay outside the catalogs.</summary>
public static class UiText
{
    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Catalogs = LoadCatalogs();
    private static volatile string _language = "en";

    public static string Language => _language;

    public static void Initialize(string? language, string? systemLanguage = null)
    {
        _ = Catalogs; // Parse all three embedded catalogs once, at startup, before any UI is constructed.
        string culture = string.IsNullOrEmpty(language) || language == "system"
            ? systemLanguage ?? CultureInfo.CurrentUICulture.Name
            : language;
        _language = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("HK", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "zh-Hans")
            : "en";
    }

    public static string T(string key) => Translate(key, Catalogs[Language], Catalogs["en"]);

    internal static string Translate(string key, IReadOnlyDictionary<string, string> selected,
        IReadOnlyDictionary<string, string> english) =>
        selected.TryGetValue(key, out string? text) ? text : english.GetValueOrDefault(key, key);

    internal static IReadOnlyDictionary<string, string> GetCatalog(string language) => Catalogs[language];

    private static FrozenDictionary<string, FrozenDictionary<string, string>> LoadCatalogs() =>
        new[] { "en", "zh-Hans", "zh-Hant" }.ToFrozenDictionary(language => language, language =>
        {
            using Stream stream = typeof(UiText).Assembly.GetManifestResourceStream($"WinScreen.Localization.{language}.json")
                ?? throw new InvalidOperationException($"Missing embedded localization catalog: {language}.");
            return ParseCatalog(stream);
        }, StringComparer.Ordinal);

    internal static FrozenDictionary<string, string> ParseCatalog(Stream stream)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A localization catalog must be a JSON object.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(entry.Value.GetString()))
                throw new JsonException($"Invalid or empty localization entry: {entry.Name}.");
            if (!entries.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new JsonException($"Duplicate localization key: {entry.Name}.");
        }
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
