using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>A window's descendants, at any depth, as Windows lists them.</summary>
internal static class ChildWindows
{
    private delegate bool EnumProc(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint data);

    /// <summary>
    /// Whether any window inside <paramref name="parent"/> belongs to <paramref name="process"/>. Explorer's frames
    /// round Windows 11's own panels hold the panel's windows a level or two down, under a window
    /// <c>GetWindow</c> doesn't reach.
    /// </summary>
    internal static bool AnyOf(nint parent, uint process)
    {
        if (parent == 0 || process == 0) return false;
        bool found = false;
        int looked = 0;
        EnumChildWindows(parent, (child, _) =>
        {
            User32.GetWindowThreadProcessId(child, out uint owner);
            found = owner == process;
            return !found && ++looked < 64;
        }, 0);
        return found;
    }
}
