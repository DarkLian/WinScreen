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

    private sealed record LayerState(List<nint> Order, List<nint> Taskbars, List<nint> Tips,
        List<nint> Menus, bool OverflowOpen)
    {
        internal bool Blocked => OverflowOpen || Menus.Count != 0;
    }

    private static long _lifts;
    internal static long LiftCount => Interlocked.Read(ref _lifts);

    internal static bool IsOverflowClass(string name) =>
        name is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";

    internal static bool InputBlocked => Survey().Blocked;

    /// <summary>Raises only when another ordinary window is ahead, then repairs the higher layers.</summary>
    internal static void Raise(nint window)
    {
        var state = Survey();
        nint[] above = ProtectedWindows(state);
        var higher = state.Taskbars.Concat(above).Concat(state.Tips).Where(handle => handle != window).ToHashSet();
        bool covered = !state.Order.Contains(window);
        foreach (nint other in state.Order)
        {
            if (other == window) break;
            if (!higher.Contains(other) && !WindowInfo.IsOwnedBy(other, window)) covered = true;
        }
        if (covered) { Lift(window); state = Survey(); }
        if (LiftCovered(state.Taskbars.Where(handle => handle != window).ToArray(), above.Concat(state.Tips), state.Order))
            state = Survey();
        RepairProtected(above, state);
    }

    /// <summary>Open menus and tips stay ahead without resetting an already correct z-order.</summary>
    internal static void KeepAbove()
    {
        var state = Survey();
        nint[] above = ProtectedWindows(state);
        if (state.OverflowOpen && LiftCovered(state.Taskbars, above.Concat(state.Tips), state.Order)) state = Survey();
        RepairProtected(above, state);
    }

    private static void RepairProtected(nint[] above, LayerState state)
    {
        if (LiftCovered(above, state.Tips, state.Order)) state = Survey();
        LiftCovered(state.Tips, [], state.Order);
    }

    private static nint[] ProtectedWindows(LayerState state) => (Above?.Invoke() ?? []).Concat(state.Menus.AsEnumerable().Reverse())
        .Where(window => window != 0 && User32.IsWindowVisible(window)).Distinct().ToArray();

    private static bool LiftCovered(IEnumerable<nint> group, IEnumerable<nint> higher, List<nint> order)
    {
        nint[] windows = group.Where(window => window != 0 && User32.IsWindowVisible(window)).Distinct().ToArray();
        if (windows.Length == 0) return false;
        var own = windows.ToHashSet();
        var allowed = higher.Concat(windows).ToHashSet();
        bool outsider = false, covered = windows.Any(window => !order.Contains(window));
        foreach (nint window in order)
        {
            if (own.Contains(window) && outsider) { covered = true; break; }
            if (!allowed.Contains(window)) outsider = true;
        }
        if (!covered) return false;
        foreach (nint window in windows) Lift(window);
        return true;
    }

    private static void Lift(nint window)
    {
        Interlocked.Increment(ref _lifts);
        User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
    }

    private static LayerState Survey()
    {
        var order = new List<nint>();
        var taskbars = new List<nint>();
        var tips = new List<nint>();
        var menus = new List<nint>();
        bool overflowOpen = false;
        uint self = (uint)Environment.ProcessId;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
        {
            if (!User32.IsWindowVisible(window)) continue;
            long style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
            if ((style & User32.WS_EX_TOPMOST) == 0) break;
            if ((style & User32.WS_EX_LAYERED) != 0 &&
                User32.GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags) &&
                (flags & User32.LWA_ALPHA) != 0 && alpha <= 1) continue;
            order.Add(window);
            string name = WindowInfo.ClassName(window);
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || IsOverflowClass(name))
                taskbars.Add(window);
            if (IsOverflowClass(name)) overflowOpen = true;
            if (User32.GetWindowThreadProcessId(window, out uint process) == 0 || process != self) continue;
            if (IsTip(window)) tips.Add(window);
            if (Control.FromHandle(window) is ToolStripDropDown || name == "#32768") menus.Add(window);
        }
        // Shell overflow panels need not be topmost before WinScreen raises something.
        foreach (string name in new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
        {
            nint panel = User32.FindWindow(name, null);
            if (panel == 0 || !User32.IsWindowVisible(panel)) continue;
            overflowOpen = true;
            if (!taskbars.Contains(panel)) taskbars.Add(panel);
        }
        taskbars.Reverse();
        tips.Reverse();
        return new LayerState(order, taskbars, tips, menus, overflowOpen);
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
