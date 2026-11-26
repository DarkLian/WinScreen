using System.Runtime.InteropServices;

namespace WinScreen.Native;

internal static partial class StartedBy
{
    private const int TitleIsLinkName = 0x800;
    private const uint Allow = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountChars, CountCharsY, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetStartupInfoW")]
    private static partial void GetStartupInfo(ref StartupInfo info);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint window, int message, uint action, nint filter);

    // The shortcut Windows started this process from, if any.
    internal static string? Shortcut()
    {
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        GetStartupInfo(ref info);
        return (info.Flags & TitleIsLinkName) != 0 && info.Title != 0 ? Marshal.PtrToStringUni(info.Title) : null;
    }

    // Lets this one message in from programs running with fewer rights.
    internal static void LetIn(nint window, int message) => ChangeWindowMessageFilterEx(window, message, Allow, 0);
}
