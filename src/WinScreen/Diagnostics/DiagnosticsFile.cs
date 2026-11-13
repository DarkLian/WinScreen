using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinScreen.Settings;

namespace WinScreen.Diagnostics;

/// <summary>
/// Save diagnostics: one zip a person can look inside and attach to a report themselves. It holds the log and the
/// settings with every list cut to a count, so no program name, path, title or other text of theirs is in it.
/// </summary>
internal static class DiagnosticsFile
{
    internal const string FolderName = "diagnostics";
    internal const string FileName = "WinScreen-diagnostics.zip";

    /// <summary>Text kept as it is: choices from a fixed set, not anything a person typed or picked from their PC.</summary>
    private static readonly HashSet<string> KeptText = [nameof(AppSettings.Language), nameof(AppSettings.Theme)];

    /// <summary>
    /// Writes the zip in a folder of its own inside the data folder, so nothing else is beside it to attach by
    /// mistake, and returns its path. Replaces the one saved before.
    /// </summary>
    public static string Save(string dataFolder, AppSettings settings)
    {
        Log.Event("diagnostics.saved");
        StartupFacts.LogNow(); // The facts again, in case the start of this session has rolled out of the log.
        Log.Flush(TimeSpan.FromSeconds(2));

        string folder = Path.Combine(dataFolder, FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName), temporary = path + ".tmp";
        File.Delete(temporary);
        using (ZipArchive zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (string file in Log.Files)
            {
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using Stream entry = zip.CreateEntry(Path.GetFileName(file)).Open();
                source.CopyTo(entry);
            }
            using var writer = new StreamWriter(zip.CreateEntry("settings.json").Open());
            writer.Write(Shrunk(settings));
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>The settings with lists as counts and only fixed-set text kept: safe for anyone to read.</summary>
    internal static string Shrunk(AppSettings settings) =>
        Shrink(JsonSerializer.SerializeToNode(settings), null, inHotkeys: false)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonNode? Shrink(JsonNode? node, string? key, bool inHotkeys)
    {
        switch (node)
        {
            case JsonObject item:
                var kept = new JsonObject();
                foreach ((string name, JsonNode? value) in item)
                    kept[name] = Shrink(value, name, inHotkeys || name == nameof(AppSettings.Hotkeys) && key is null);
                return kept;
            case JsonArray list when key == nameof(ToolbarPreferences.Buttons):
                // Command names: fixed by WinScreen.
                return new JsonArray([.. list.Select(value => value is JsonValue name && name.TryGetValue(out string? text) ? JsonValue.Create(text) : null)]);
            case JsonArray list:
                return new JsonObject { ["count"] = list.Count };
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return inHotkeys || key is not null && KeptText.Contains(key) ? value.DeepClone() : JsonValue.Create("(text)");
            default:
                return node?.DeepClone();
        }
    }
}
