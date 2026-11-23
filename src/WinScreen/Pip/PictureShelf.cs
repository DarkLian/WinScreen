using System.Drawing.Imaging;

namespace WinScreen.Pip;

// Where a view's picture or text goes: the clipboard, and a folder when Settings says so. The tray sets it from
// Settings; tests set their own, so the PC's clipboard and folders are left alone.
internal sealed class PictureShelf
{
    internal Func<bool> Saves { get; init; } = static () => false;
    internal Func<string?> Folder { get; init; } = static () => null;
    internal Action<Bitmap> PutPicture { get; init; } = CopyPicture;
    internal Action<string> PutText { get; init; } = CopyText;
    internal Action<string> ShowFile { get; init; } = OpenFileLocation;
    internal Func<DateTime> Now { get; init; } = static () => DateTime.Now;

    internal string FolderInUse => Folder() ?? DefaultFolder();

    // The file it was saved to, if any; a copy that can't be saved is still copied, and the reason comes back.
    internal (string? File, string? Problem) Keep(Bitmap picture)
    {
        PutPicture(picture);
        if (!Saves()) return (null, null);
        try { return (Save(picture), null); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException or NotSupportedException)
        {
            return (null, failure.Message);
        }
    }

    // Named as Windows names screenshots, with a number added when that name is taken.
    internal string Save(Bitmap picture)
    {
        string folder = FolderInUse;
        Directory.CreateDirectory(folder);
        string name = $"Screenshot {Now():yyyy-MM-dd HHmmss}";
        string path = Path.Combine(folder, name + ".png");
        for (int number = 2; File.Exists(path); number++)
            path = Path.Combine(folder, $"{name} ({number}).png");
        picture.Save(path, ImageFormat.Png);
        return path;
    }

    // As PNG for apps that keep it exactly, and as a bitmap for the rest.
    private static void CopyPicture(Bitmap picture)
    {
        var data = new DataObject();
        var png = new MemoryStream();
        picture.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        data.SetData(DataFormats.Bitmap, true, picture);
        Clipboard.SetDataObject(data, true, 5, 100);
    }

    private static void CopyText(string text) => Clipboard.SetDataObject(text, true, 5, 100);

    private static void OpenFileLocation(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); }
        catch (System.ComponentModel.Win32Exception failure) { Diagnostics.Log.Swallowed(failure); }
    }

    internal static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Swallowed(failure);
        }
    }

    // Windows' own Screenshots folder, wherever the person keeps their Pictures (OneDrive too).
    internal static string DefaultFolder() => Native.KnownFolders.Screenshots() ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
}
