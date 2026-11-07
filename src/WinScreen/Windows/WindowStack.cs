using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>Reads the desktop's windows from the top down, for <see cref="CoverTracker"/>.</summary>
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

    private static bool IsCloaked(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Concealed, translucent and click-through windows leave what's behind them in view.</summary>
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
