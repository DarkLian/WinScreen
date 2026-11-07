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
    /// <summary>The file could not be read, or kept safe, so writing over it could lose recovery data.</summary>
    public bool ReadFailed { get; private set; }

    /// <summary>Where the last load kept a damaged file, or null when it found no damage.</summary>
    public string? DamagedBackup { get; private set; }

    public string FilePath => path;

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
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, all);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public WindowRecord[] Load()
    {
        ReadFailed = false;
        DamagedBackup = null;
        if (!File.Exists(path))
            return [];

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Possibly only locked for a moment, so the file is left where it is for a retry.
            ReadFailed = true;
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Recovery data must be an array.");
            var parsed = new List<WindowRecord?>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    parsed.Add(element.Deserialize<WindowRecord>());
                }
                catch (JsonException)
                {
                    parsed.Add(null);
                }
            }

            var loaded = parsed.ToArray();
            var valid = loaded.Where(record => record is { Window: not 0, ProcessId: > 0 } &&
                                               (record.OwnedStyles & ~(Native.User32.WS_EX_TOPMOST |
                                                                       Native.User32.WS_EX_LAYERED |
                                                                       Native.User32.WS_EX_TRANSPARENT)) == 0 &&
                                               (record.Placement is null ||
                                                (record.Placement.Length == 10 &&
                                                 record.Placement[1] is >= 0 and <= 11 &&
                                                 record.Placement[8] > record.Placement[6] &&
                                                 record.Placement[9] > record.Placement[7])))
                .Cast<WindowRecord>().ToArray();
            if ((valid.Length != loaded.Length || valid.Any(record => record.IdentityToken == 0)) && !CopyDamaged())
            {
                ReadFailed = true;
                return [];
            }

            return valid;
        }
        catch (JsonException)
        {
            MoveDamaged();
            return [];
        }
    }

    private string NewBackupPath() => path + ".invalid-" + Guid.NewGuid().ToString("N");

    /// <summary>Nothing in the file can be used, so it moves aside and work can carry on.</summary>
    private void MoveDamaged()
    {
        string backup = NewBackupPath();
        try
        {
            File.Move(path, backup);
            DamagedBackup = backup;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ReadFailed = true;
        }
    }

    /// <summary>Part of the file is usable, so it stays for recovery and a copy is kept for diagnosis.</summary>
    private bool CopyDamaged()
    {
        string backup = NewBackupPath();
        try
        {
            File.Copy(path, backup, overwrite: false);
            DamagedBackup = backup;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
