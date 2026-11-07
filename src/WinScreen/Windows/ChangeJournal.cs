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
    bool Cropped = false)
{
    public long IdentityToken { get; init; }
    public long ProcessStartTicks { get; init; }
    public uint ThreadId { get; init; }
    public long OwnedStyles { get; init; }
}

/// <summary>
/// Keeps the list of changed windows on disk. A forced kill skips every exit handler, so the next
/// start reads this file and puts back any window that was left moved or see-through.
/// </summary>
public sealed class ChangeJournal(string path)
{
    public bool ReadFailed { get; private set; }
    private bool _backupFailed;
    public void Save(IEnumerable<WindowRecord> records)
    {
        WindowRecord[] all = records.ToArray();
            if (all.Length == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, all);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
    }

    public WindowRecord[] Load()
    {
        ReadFailed = false;
        _backupFailed = false;
        if (!File.Exists(path))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Recovery data must be an array.");
            var parsed = new List<WindowRecord?>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try { parsed.Add(element.Deserialize<WindowRecord>()); }
                catch (JsonException) { parsed.Add(null); }
            }
            var loaded = parsed.ToArray();
            var valid = loaded.Where(record => record is { Window: not 0, ProcessId: > 0 } &&
                (record.OwnedStyles & ~(Native.User32.WS_EX_TOPMOST | Native.User32.WS_EX_LAYERED | Native.User32.WS_EX_TRANSPARENT)) == 0 &&
                (record.Placement is null || (record.Placement.Length == 10 && record.Placement[1] is >= 0 and <= 11 &&
                    record.Placement[8] > record.Placement[6] && record.Placement[9] > record.Placement[7])))
                .Cast<WindowRecord>().ToArray();
            if (valid.Length != loaded.Length)
                PreserveDamaged();
            if (valid.Any(record => record.IdentityToken == 0))
                PreserveDamaged();
            if (_backupFailed) { ReadFailed = true; return []; }
            return valid;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            ReadFailed = true;
            PreserveDamaged();
            return [];
        }
    }

    private void PreserveDamaged()
    {
        try { File.Copy(path, path + ".invalid-" + Guid.NewGuid().ToString("N"), overwrite: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _backupFailed = true; }
    }
}
