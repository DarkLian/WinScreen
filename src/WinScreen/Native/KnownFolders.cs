using System.Runtime.InteropServices;

namespace WinScreen.Native;

internal static partial class KnownFolders
{
    private static readonly Guid ScreenshotsId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    // Windows' Screenshots folder, where the person has it (OneDrive too); null when Windows doesn't say.
    internal static string? Screenshots()
    {
        if (SHGetKnownFolderPath(ScreenshotsId, 0, 0, out nint path) != 0) return null;
        try { return Marshal.PtrToStringUni(path) is { Length: > 0 } found ? found : null; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
}
