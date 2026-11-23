using System.Runtime.InteropServices;

namespace WinScreen.Native;

internal static class ChildWindows
{
    private delegate bool EnumProc(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint data);

    // Explorer's frames round Windows 11's own panels hold the panel's windows a level or two down, under a window
    // GetWindow doesn't reach.
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

    internal static List<nint> Of(nint parent, int most = 64)
    {
        var found = new List<nint>();
        if (parent == 0) return found;
        EnumChildWindows(parent, (child, _) =>
        {
            found.Add(child);
            return found.Count < most;
        }, 0);
        return found;
    }
}
