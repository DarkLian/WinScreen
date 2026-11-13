namespace WinScreen.Settings;

/// <summary>Keeps only the newest few backup copies of a file, so they don't pile up over the years.</summary>
internal static class BackupFiles
{
    public const int Kept = 5;

    /// <summary>Deletes all but the newest <see cref="Kept"/> files named <c>path + marker + anything</c>.</summary>
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
