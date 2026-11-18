using System.Reflection;
using System.Text.Json;

namespace WinScreen.Updates;

// Carried inside the program: no network.
internal static class WhatsNew
{
    private static Dictionary<string, Dictionary<string, string[]>>? _notes;

    internal static string Core(string version) => version.Split('+')[0].Split('-')[0];

    internal static string[]? For(string version, string language)
    {
        Dictionary<string, Dictionary<string, string[]>> notes = _notes ??= Load();
        if (!notes.TryGetValue(Core(version), out Dictionary<string, string[]>? languages)) return null;
        return languages.TryGetValue(language, out string[]? lines) ? lines : languages.GetValueOrDefault("en");
    }

    // Settings from before this kept no version, and come from an update.
    internal static bool ShouldOffer(string current, string? lastSeen, bool firstRun, bool published) =>
        published && !firstRun && (lastSeen is null || Core(lastSeen) != Core(current)) && For(current, "en") is not null;

    // Only a published copy records itself: a local build of a release's betas must not make the published release look
    // already seen.
    internal static string? Remember(string current, string? lastSeen, bool published) => published ? current : lastSeen;

    internal static bool Published => typeof(WhatsNew).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(metadata => metadata.Key == "ReleaseType" && metadata.Value == "release");

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
