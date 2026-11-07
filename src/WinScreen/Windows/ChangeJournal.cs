using System.Text.Json;

namespace WinScreen.Windows;

/// <summary>How one window looked before WinScreen changed it, as saved on disk.</summary>
public sealed record WindowRecord(
    long Window,
    uint ProcessId,
    long ExStyle,
    bool HadLayeredAttributes,
    uint ColorKey,
    byte Alpha,
    uint LayeredFlags,
    int[]? Placement,
    bool Cropped = false);

/// <summary>
/// Keeps the list of changed windows on disk. A forced kill skips every exit handler, so the next
/// start reads this file and puts back any window that was left moved or see-through.
/// </summary>
public sealed class ChangeJournal(string path)
{
    public void Save(IEnumerable<WindowRecord> records)
    {
        WindowRecord[] all = records.ToArray();
        try
        {
            if (all.Length == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only crash recovery depends on this file, so a locked disk mustn't stop the app.
        }
    }

    public WindowRecord[] Load()
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<WindowRecord[]>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
