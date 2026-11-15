using System.Reflection;
using System.Text.Json;

namespace WinScreen.Updates;

/// <summary>
/// The short list of what a release brought, in three languages, carried inside the program (no network). Offered once,
/// as a quiet notification, the first time a published version starts after an update; never on a first install or
/// for a local build. Always reachable from the tray menu and Settings.
/// </summary>
internal static class WhatsNew
{
    private static Dictionary<string, Dictionary<string, string[]>>? _notes;

    /// <summary>The version without anything after a "-" or "+": betas of a release share its list.</summary>
    internal static string Core(string version) => version.Split('+')[0].Split('-')[0];

    /// <summary>The list for a version's release in a language (English when it has none there), or null.</summary>
    internal static string[]? For(string version, string language)
    {
        Dictionary<string, Dictionary<string, string[]>> notes = _notes ??= Load();
        if (!notes.TryGetValue(Core(version), out Dictionary<string, string[]>? languages)) return null;
        return languages.TryGetValue(language, out string[]? lines) ? lines : languages.GetValueOrDefault("en");
    }

    /// <summary>
    /// Whether to offer the list now. Not on a first install (the welcome is for that), not for a local build, and only
    /// once per release: a later beta of the same release isn't offered it again. Settings from before this kept no
    /// version, and come from an update.
    /// </summary>
    internal static bool ShouldOffer(string current, string? lastSeen, bool firstRun, bool published) =>
        published && !firstRun && (lastSeen is null || Core(lastSeen) != Core(current)) && For(current, "en") is not null;

    /// <summary>
    /// The version to keep as last seen. Only a published copy records itself: a local build of 1.1.0's betas must not
    /// make the published 1.1.0 look already seen.
    /// </summary>
    internal static string? Remember(string current, string? lastSeen, bool published) => published ? current : lastSeen;

    /// <summary>Whether this copy was published, rather than built locally.</summary>
    internal static bool Published => typeof(WhatsNew).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(metadata => metadata.Key == "ReleaseType" && metadata.Value == "release");

    /// <summary>Opens the list for this version, in the language WinScreen speaks, in a small window in Settings' look.</summary>
    internal static void Show()
    {
        string version = AppInfo.Version;
        string[]? lines = For(version, Localization.UiText.Language);
        string text = lines is null ? Localization.UiText.T("whatsNew.none") : string.Join(Environment.NewLine, lines.Select(line => "• " + line));
        new Settings.InfoWindow(string.Format(Localization.UiText.T("whatsNew.title"), Core(version)), text).Show();
    }

    private static Dictionary<string, Dictionary<string, string[]>> Load()
    {
        using Stream? stream = typeof(WhatsNew).Assembly.GetManifestResourceStream("WinScreen.WhatsNew");
        if (stream is null) return [];
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(stream) ?? [];
    }
}
