using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinScreen.Settings;

/// <summary>A portable settings document, separate from this machine's startup and first-run state.</summary>
internal static class SettingsTransfer
{
    private const int Version = 1;
    private const int MaximumBytes = 1024 * 1024;

    internal static void Export(AppSettings settings, string path)
    {
        var content = JsonSerializer.SerializeToNode(settings)!.AsObject();
        content.Remove(nameof(AppSettings.StartWithWindows));
        content.Remove(nameof(AppSettings.RunAsAdministrator)); // This computer's choice, like starting with Windows.
        content.Remove(nameof(AppSettings.SettingsWindowPlacement)); // Where it sat on this computer's screens.
        content.Remove(nameof(AppSettings.WelcomeShown));
        var document = new JsonObject { ["FormatVersion"] = Version, ["Settings"] = content };
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static AppSettings Import(string path, AppSettings current)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new JsonException("Settings file is too large.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("FormatVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != Version ||
            !root.TryGetProperty("Settings", out var content))
            throw new JsonException(Localization.UiText.T("settings.transfer.invalid"));
        AppSettings imported = AppSettings.Parse(content.GetRawText(),
            () => throw new JsonException(Localization.UiText.T("settings.transfer.invalid")));
        imported.StartWithWindows = current.StartWithWindows;
        imported.RunAsAdministrator = current.RunAsAdministrator;
        imported.SettingsWindowPlacement = current.SettingsWindowPlacement;
        imported.WelcomeShown = current.WelcomeShown;
        imported.Imported = true;
        return imported;
    }
}
