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

    /// <summary>
    /// The key of the message a person was shown, so the log can name it without its text: the first line is
    /// matched against each message in the current language, with "{0}" and the like standing for anything.
    /// Null when nothing matches. Only for failures, since it reads the whole catalog.
    /// </summary>
    internal static string? KeyOf(string message, string prefix = "errors.")
    {
        string first = message.Split('\n')[0].TrimEnd('\r');
        foreach (string language in new[] { Language, "en" })
            foreach ((string key, string text) in Catalogs[language])
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (text == first) return key;
                if (text.Contains('{') && System.Text.RegularExpressions.Regex.IsMatch(first,
                        "^" + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Escape(text), @"\\\{\d+}", ".*") + "$",
                        System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromMilliseconds(50)))
                    return key;
            }
        return null;
    }

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
