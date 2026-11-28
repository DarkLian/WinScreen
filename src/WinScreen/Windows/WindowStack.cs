using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

internal static class WindowStack
{
    private delegate bool EnumCallback(nint window, nint state);

    private static readonly EnumCallback Visit = static (window, state) =>
    {
        var found = (List<StackedWindow>)GCHandle.FromIntPtr(state).Target!;
        if (Describe(window) is { } described)
            found.Add(described);
        return true;
    };

    private static readonly nint VisitPointer = Marshal.GetFunctionPointerForDelegate(Visit);

    public static List<StackedWindow> Read()
    {
        var found = new List<StackedWindow>();
        GCHandle handle = GCHandle.Alloc(found);
        try { User32.EnumWindows(VisitPointer, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return found;
    }

    private static StackedWindow? Describe(nint window)
    {
        if (!User32.IsWindowVisible(window) || User32.IsIconic(window) || IsCloaked(window) ||
            WindowController.IsShell(window) ||
            !WindowInfo.TryGetFrameBounds(window, out Rectangle bounds, out _) || bounds.IsEmpty)
            return null;
        User32.GetWindowThreadProcessId(window, out uint process);
        if (process == Environment.ProcessId)
            return null; // Views and Anchors are small and float; they don't hide anything worth an Anchor.
        return new StackedWindow(window, bounds, IsSolid(window), true);
    }

    // The windows above this one that hide something of what's under them, highest first; empty when it isn't found.
    public static List<nint> Over(nint target)
    {
        const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
        var over = new List<nint>();
        nint window = User32.GetWindow(User32.GetDesktopWindow(), GW_CHILD);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
        {
            if (window == target) return over;
            if (CoverBounds(window) is not null) over.Add(window);
        }
        return [];
    }

    // The taskbar counts, as browsers count it; the desktop and WinScreen's own windows don't.
    public static Rectangle? CoverBounds(nint window)
    {
        if (!User32.IsWindow(window) || !User32.IsWindowVisible(window) || User32.IsIconic(window) || IsCloaked(window) || !IsSolid(window) ||
            window == User32.GetShellWindow() || WindowInfo.ClassName(window) is "Progman" or "WorkerW")
            return null;
        User32.GetWindowThreadProcessId(window, out uint process);
        if (process == Environment.ProcessId) return null;
        return WindowInfo.TryGetFrameBounds(window, out Rectangle bounds, out _) && !bounds.IsEmpty ? bounds : null;
    }

    // Whether a window above this one is at the point: another view, a menu. Overlays the pointer goes through, and
    // windows ignore() names (the target's own parts), don't count.
    public static bool CoveredAt(nint target, Point point, Func<nint, bool> ignore)
    {
        const long PassedThrough = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE;
        nint window = target;
        for (int seen = 0; seen < 4096 && (window = User32.GetWindow(window, User32.GW_HWNDPREV)) != 0; seen++)
        {
            if (!User32.IsWindowVisible(window) || User32.IsIconic(window) || IsCloaked(window) || ignore(window) ||
                (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & PassedThrough) == PassedThrough ||
                !User32.GetWindowRect(window, out Rect r) || !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(point))
                continue;
            return true;
        }
        return false;
    }

    private static bool IsCloaked(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsSolid(nint window)
    {
        long style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        if ((style & User32.WS_EX_TRANSPARENT) != 0)
            return false;
        if ((style & User32.WS_EX_LAYERED) == 0)
            return true;
        return User32.GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags) &&
               (flags & User32.LWA_ALPHA) != 0 && alpha == 255;
    }
}
