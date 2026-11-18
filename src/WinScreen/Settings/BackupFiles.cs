namespace WinScreen.Settings;

internal static class BackupFiles
{
    public const int Kept = 5;

    public static void Prune(string path, string marker)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder))
                return;
            foreach (FileInfo old in new DirectoryInfo(folder)
                         .EnumerateFiles(Path.GetFileName(path) + marker + "*")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                         .Skip(Kept))
                old.Delete();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Tidying up is never worth a failure.
            Diagnostics.Log.Swallowed(failure);
        }
    }
}
