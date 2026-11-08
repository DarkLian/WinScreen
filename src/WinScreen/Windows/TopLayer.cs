using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// The order of everything kept on top, highest first:
/// <list type="number">
/// <item>WinScreen's own hover tips;</item>
/// <item>WinScreen's open menus and Settings;</item>
/// <item>the Windows taskbar, so its icons, WinScreen's included, can always be reached;</item>
/// <item>always-on-top windows: views and their parts, toolbars, pin frames, pinned windows.</item>
/// </list>
/// Anything WinScreen lifts to the top goes through here.
/// </summary>
internal static class TopLayer
{
    private const uint Flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
    private const uint GW_HWNDNEXT = 2;

    /// <summary>The menus and windows that belong above the taskbar, lowest first; empty when none is open.</summary>
    internal static Func<IEnumerable<nint>>? Above { get; set; }

    /// <summary>Whether one of WinScreen's own windows is a hover tip; a seam for tests.</summary>
    internal static Func<nint, bool> IsTip { get; set; } = window => WindowInfo.ClassName(window) == "tooltips_class32";

    /// <summary>Puts a window at the top of the always-on-top windows, under the taskbar and WinScreen's open menus.</summary>
    internal static void Raise(nint window)
    {
        User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        (List<nint> taskbars, List<nint> tips) = Survey();
        // The taskbar goes back over it: tucking the window under the taskbar instead doesn't hold.
        foreach (nint taskbar in taskbars)
            if (taskbar != window)
                User32.SetWindowPos(taskbar, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        LiftAbove(tips);
    }

    /// <summary>Lifts the open menus and Settings, then their hover tips, back over whatever just went to the top.</summary>
    internal static void KeepAbove() => LiftAbove(Survey().Tips);

    private static void LiftAbove(List<nint> tips)
    {
        foreach (nint window in Above?.Invoke() ?? [])
            User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        foreach (nint tip in tips)
            User32.SetWindowPos(tip, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
    }

    /// <summary>
    /// One walk down the always-on-top windows: the taskbars (main and other displays'), and WinScreen's own
    /// hover tips that are showing.
    /// </summary>
    private static (List<nint> Taskbars, List<nint> Tips) Survey()
    {
        var taskbars = new List<nint>();
        var tips = new List<nint>();
        uint self = (uint)Environment.ProcessId;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5 /* GW_CHILD: the topmost top-level window */);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
        {
            if (!User32.IsWindowVisible(window))
                continue; // Hidden helper windows can sit among the always-on-top ones without being one.
            if ((User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) == 0)
                break; // The always-on-top windows come first; the rest don't matter here.
            string name = WindowInfo.ClassName(window);
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                taskbars.Add(window);
            else if (User32.GetWindowThreadProcessId(window, out uint process) != 0 && process == self && IsTip(window))
                tips.Add(window);
        }
        taskbars.Reverse(); // Lowest first, so they keep their order among themselves.
        return (taskbars, tips);
    }

    /// <summary>A menu's own window and every submenu open under it, outermost first.</summary>
    internal static IEnumerable<nint> OpenDropDowns(ToolStripDropDown? menu)
    {
        if (menu is not { Visible: true, IsHandleCreated: true, IsDisposed: false })
            yield break;
        yield return menu.Handle;
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripDropDownItem { HasDropDownItems: true, DropDown: var child })
                foreach (nint window in OpenDropDowns(child))
                    yield return window;
    }
}
