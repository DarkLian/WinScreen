using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// The order of everything kept on top, highest first:
/// <list type="number">
/// <item>Windows' own passing surfaces: the screen snip, Start, search, the taskbar's flyouts, task switching and
/// notifications. WinScreen never lifts anything over these, or the two would take turns on top and flicker;</item>
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
        List<nint> Menus, List<nint> System, bool OverflowOpen)
    {
        internal bool Blocked => OverflowOpen || Menus.Count != 0;

        /// <summary>
        /// Where a lifted window goes: just under the lowest of Windows' own surfaces that is open, or at the top when
        /// none is. Whatever is above that point is left where it is, since passing it would mean passing the surface.
        /// The windows being lifted never count, even when they are Windows' own (an Explorer view's menus).
        /// </summary>
        internal nint InsertAfter(ICollection<nint> lifting) =>
            Order.LastOrDefault(window => System.Contains(window) && !lifting.Contains(window)) is var lowest and not 0 ? lowest : User32.HWND_TOPMOST;

        /// <summary>The windows above the point where a lifted window goes; none of them counts as covering it.</summary>
        internal IEnumerable<nint> OutOfReach(ICollection<nint> lifting)
        {
            nint lowest = InsertAfter(lifting);
            return lowest == User32.HWND_TOPMOST ? [] : Order.TakeWhile(window => window != lowest).Append(lowest);
        }
    }

    /// <summary>Whether a window is one of Windows' own passing surfaces; a seam for tests.</summary>
    internal static Func<nint, bool> IsSystemSurface { get; set; } = SystemSurface;

    private static readonly string[] SystemClasses =
    [
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging",
        "Shell_LightDismissOverlay", "ControlCenterWindow", "TaskListThumbnailWnd", "TaskSwitcherWnd", "SnipOverlayRootWindow",
    ];

    private static readonly string[] SystemPrograms =
    [
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "ScreenClippingHost", "SnippingTool",
        "ShellHost", "TextInputHost", "LockApp", "Widgets", "WidgetBoard", "ScreenSketch",
    ];

    private static readonly Dictionary<uint, bool> SystemProcesses = [];

    private static bool SystemSurface(nint window)
    {
        string name = WindowInfo.ClassName(window);
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || IsOverflowClass(name)) return false; // Its own layer.
        if (SystemClasses.Contains(name)) return true;
        if (User32.GetWindowThreadProcessId(window, out uint process) == 0 || process == (uint)Environment.ProcessId) return false;
        lock (SystemProcesses)
        {
            if (SystemProcesses.TryGetValue(process, out bool known)) return known;
            if (SystemProcesses.Count > 256) SystemProcesses.Clear();
            string program = WindowInfo.ProcessName(process);
            // Explorer's other always-on-top windows are the taskbar's flyouts and menus.
            bool system = program.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                SystemPrograms.Contains(program, StringComparer.OrdinalIgnoreCase);
            return SystemProcesses[process] = system;
        }
    }

    private static long _lifts;
    internal static long LiftCount => Interlocked.Read(ref _lifts);

    internal static bool IsOverflowClass(string name) =>
        name is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";

    internal static bool InputBlocked => Survey().Blocked;

    /// <summary>Whether the taskbar's overflow panel is open, when views let the pointer through to it.</summary>
    internal static bool OverflowOpen => Survey().OverflowOpen;

    /// <summary>Raises only when another ordinary window is ahead, then repairs the higher layers.</summary>
    internal static void Raise(nint window) => RaiseGroup([window]);

    /// <summary>
    /// Raises windows that belong together, such as a menu and its submenus, lowest first, so each stays above the
    /// ones before it. Nothing moves when no other ordinary window is ahead of them and their order is already
    /// right; members never count as covering each other. Then repairs the higher layers.
    /// </summary>
    internal static void RaiseGroup(IReadOnlyList<nint> lowestFirst)
    {
        nint[] group = lowestFirst.Where(window => window != 0).Distinct().ToArray();
        if (group.Length == 0) return;
        var members = group.ToHashSet();
        var state = Survey();
        nint[] above = ProtectedWindows(state);
        var higher = state.Taskbars.Concat(above).Concat(state.Tips).Concat(state.System).Concat(state.OutOfReach(members))
            .Where(handle => !members.Contains(handle)).ToHashSet();
        bool covered = group.Any(window => !state.Order.Contains(window));
        int next = group.Length - 1; // The member expected next, walking down from the top.
        bool outsider = false;
        foreach (nint other in state.Order)
        {
            if (covered || next < 0) break;
            if (members.Contains(other))
            {
                if (outsider || other != group[next]) covered = true;
                next--;
            }
            else if (!higher.Contains(other) && !group.Any(member => WindowInfo.IsOwnedBy(other, member))) outsider = true;
        }
        if (covered)
        {
            nint after = state.InsertAfter(members);
            // From the highest down, each just under the one before, so the group keeps its order.
            for (int index = group.Length - 1; index >= 0; index--) after = Lift(group[index], after);
            state = Survey();
        }
        if (LiftCovered(state.Taskbars.Where(handle => !members.Contains(handle)).ToArray(), above.Concat(state.Tips), state))
            state = Survey();
        RepairProtected(above, state);
    }

    /// <summary>Open menus and tips stay ahead without resetting an already correct z-order.</summary>
    internal static void KeepAbove()
    {
        var state = Survey();
        nint[] above = ProtectedWindows(state);
        if (state.OverflowOpen && LiftCovered(state.Taskbars, above.Concat(state.Tips), state)) state = Survey();
        RepairProtected(above, state);
    }

    private static void RepairProtected(nint[] above, LayerState state)
    {
        if (LiftCovered(above, state.Tips, state)) state = Survey();
        LiftCovered(state.Tips, [], state);
    }

    private static nint[] ProtectedWindows(LayerState state) => (Above?.Invoke() ?? []).Concat(state.Menus.AsEnumerable().Reverse())
        .Where(window => window != 0 && User32.IsWindowVisible(window)).Distinct().ToArray();

    private static bool LiftCovered(IEnumerable<nint> group, IEnumerable<nint> higher, LayerState state)
    {
        List<nint> order = state.Order;
        nint[] windows = group.Where(window => window != 0 && User32.IsWindowVisible(window)).Distinct().ToArray();
        if (windows.Length == 0) return false;
        var own = windows.ToHashSet();
        var allowed = higher.Concat(windows).Concat(state.System.Where(window => !own.Contains(window)))
            .Concat(state.OutOfReach(own)).ToHashSet();
        bool outsider = false, covered = windows.Any(window => !order.Contains(window));
        foreach (nint window in order)
        {
            if (own.Contains(window) && outsider) { covered = true; break; }
            if (!allowed.Contains(window)) outsider = true;
        }
        if (!covered) return false;
        // The group is listed lowest first; from its highest down, each goes just under the one placed before it.
        nint after = state.InsertAfter(own);
        for (int index = windows.Length - 1; index >= 0; index--) after = Lift(windows[index], after);
        return true;
    }

    /// <summary>Puts a window just under <paramref name="after"/> in the always-on-top band and returns it.</summary>
    private static nint Lift(nint window, nint after)
    {
        if (after == window) return window;
        Interlocked.Increment(ref _lifts);
        // Under a window means above everything else; HWND_TOPMOST alone would put it over Windows' own surfaces.
        bool topmost = (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        if (!topmost || after == User32.HWND_TOPMOST) User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        if (after != User32.HWND_TOPMOST) User32.SetWindowPos(window, after, 0, 0, 0, 0, Flags);
        return window;
    }

    private static LayerState Survey()
    {
        var order = new List<nint>();
        var taskbars = new List<nint>();
        var tips = new List<nint>();
        var menus = new List<nint>();
        var system = new List<nint>();
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
            string name = WindowInfo.ClassName(window);
            // A menu's drop shadow follows the menu on its own. Counting it as a window over the taskbar lifted the
            // taskbar over the menu and the menu back again, over and over.
            if (name == "SysShadow") continue;
            // Nor does a window too small to cover anything, such as the tiny helpers Explorer keeps always-on-top.
            if (!User32.GetWindowRect(window, out Rect bounds) || bounds.Right - bounds.Left < 8 || bounds.Bottom - bounds.Top < 8) continue;
            order.Add(window);
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || IsOverflowClass(name))
                taskbars.Add(window);
            if (IsOverflowClass(name)) overflowOpen = true;
            if (IsSystemSurface(window)) { system.Add(window); continue; }
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
        return new LayerState(order, taskbars, tips, menus, system, overflowOpen);
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
