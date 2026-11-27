using WinScreen.Native;

namespace WinScreen.Windows;

// Highest first (docs/UI_ELEMENTS.md, "What stays above what"): Windows' own passing surfaces (snip, Start, search,
// taskbar flyouts, task switching, notifications), which nothing is lifted over or the two flicker taking turns;
// WinScreen's hover tips; its open menus, each submenu over its menu; its dialogs; Settings, unless maximized; the
// taskbar; the toolbar, just above its window; views; pinned windows; then ordinary windows.
// Anything WinScreen lifts to the top goes through here.
internal static class TopLayer
{
    private const uint Flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
    private const uint GW_HWNDNEXT = 2;
    private const long ClickThroughOverlay = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE;

    internal static Func<IEnumerable<nint>>? Above { get; set; }

    internal static Func<nint, bool> IsTip { get; set; } = window => WindowInfo.ClassName(window) == "tooltips_class32";

    private sealed record LayerState(List<nint> Order, List<nint> Taskbars, List<nint> Tips,
        List<nint> Menus, List<nint> System, bool OverflowOpen)
    {
        internal bool Blocked => OverflowOpen || Menus.Count != 0;

        // Just under the lowest of Windows' own open surfaces, or at the top when none is: passing what is above would
        // mean passing the surface. The windows being lifted never count, even Windows' own (an Explorer view's menus).
        internal nint InsertAfter(ICollection<nint> lifting) =>
            Order.LastOrDefault(window => System.Contains(window) && !lifting.Contains(window)) is var lowest and not 0 ? lowest : User32.HWND_TOPMOST;

        internal IEnumerable<nint> OutOfReach(ICollection<nint> lifting)
        {
            nint lowest = InsertAfter(lifting);
            return lowest == User32.HWND_TOPMOST ? [] : Order.TakeWhile(window => window != lowest).Append(lowest);
        }
    }

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

    internal static bool OverflowOpen => Survey().OverflowOpen;

    internal static void Raise(nint window) => RaiseGroup([window]);

    // Nothing moves when no other ordinary window is ahead and their order is already right; members never count as
    // covering each other.
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

    // Without resetting an already correct z-order.
    internal static void KeepAbove(Func<nint, bool>? refusedForTest)
    {
        _refused = refusedForTest;
        try { KeepAbove(); }
        finally { _refused = null; }
    }

    private static Func<nint, bool>? _refused; // A move Windows or the app ignores, as a pinned app in front can.

    internal static void KeepAbove()
    {
        var state = Survey();
        nint[] above = ProtectedWindows(state);
        if (state.OverflowOpen && LiftCovered(state.Taskbars, above.Concat(state.Tips), state)) state = Survey();
        RepairProtected(above, state);
    }

    private static void RepairProtected(nint[] above, LayerState state)
    {
        if (above.Length == 0) PinAgain();
        if (LiftCovered(above, state.Tips, state, pushOthersUnder: true)) state = Survey();
        LiftCovered(state.Tips, [], state);
    }

    private static nint[] ProtectedWindows(LayerState state) => (Above?.Invoke() ?? []).Concat(state.Menus.AsEnumerable().Reverse())
        .Where(window => window != 0 && User32.IsWindowVisible(window)).Distinct().ToArray();

    private static bool LiftCovered(IEnumerable<nint> group, IEnumerable<nint> higher, LayerState state, bool pushOthersUnder = false)
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
        if (pushOthersUnder) PushUnder(own, allowed, Survey());
        return true;
    }

    // Only for WinScreen's menus, dialogs and Settings: Windows can keep another app's window over them (the app in
    // front, pinned). Going lower is usually allowed, so that window goes just under them instead, still pinned. A
    // window WinScreen pinned that won't go under, or keeps climbing back, is unpinned until they close (ExitLag keeps
    // itself on top that way); any other is left alone.
    private static void PushUnder(ICollection<nint> own, ICollection<nint> allowed, LayerState state)
    {
        nint lowest = state.Order.LastOrDefault(own.Contains);
        if (lowest == 0) return;
        lock (Pushed) PushUnder(own, allowed, state, lowest);
    }

    private static void PushUnder(ICollection<nint> own, ICollection<nint> allowed, LayerState state, nint lowest)
    {
        long now = Environment.TickCount64;
        foreach (nint gone in Pushed.Keys.Where(window => now - Pushed[window].Since > PushWindowMilliseconds).ToArray())
            Pushed.Remove(gone);
        foreach (nint window in state.Order)
        {
            if (window == lowest) break;
            if (own.Contains(window) || allowed.Contains(window) || own.Any(member => WindowInfo.IsOwnedBy(window, member)) ||
                User32.GetWindowThreadProcessId(window, out uint process) == 0 || process == (uint)Environment.ProcessId)
                continue;
            var tries = Pushed.TryGetValue(window, out var known) ? known : (Count: 0, Since: now);
            if (tries.Count >= MostPushes) continue;
            Pushed[window] = (tries.Count + 1, tries.Since);
            // Under a window that isn't always on top, it would lose its pin.
            if (!IsTopmost(lowest)) continue;
            bool stays = tries.Count + 1 == MostPushes;
            if (!stays && _refused?.Invoke(window) != true && User32.SetWindowPos(window, lowest, 0, 0, 0, 0, Flags))
            {
                if (!IsTopmost(window)) User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
                Interlocked.Increment(ref _pushes);
                stays = IsAbove(window, lowest);
                Diagnostics.Log.Event("layer.pushedUnder", ("program", Diagnostics.LogValue.ProgramOf(window)), ("pinKept", IsTopmost(window)),
                    ("stayedOver", stays));
            }
            else stays = true;
            if (stays) Unpin(window);
        }
    }

    private static bool IsAbove(nint upper, nint lower)
    {
        nint window = lower;
        for (int seen = 0; seen < 4096 && (window = User32.GetWindow(window, GW_HWNDPREV)) != 0; seen++)
            if (window == upper) return true;
        return false;
    }

    private const uint GW_HWNDPREV = 3;
    private static readonly HashSet<nint> PinnedByWinScreen = [], Unpinned = [];

    // Told by every pin and unpin WinScreen makes, which also ends any unpinning here.
    internal static void PinChanged(nint window, bool pinned)
    {
        lock (Unpinned)
        {
            // Closed windows go, so a handle Windows gives a new window is never taken for one WinScreen pinned.
            PinnedByWinScreen.RemoveWhere(earlier => !User32.IsWindow(earlier));
            if (pinned) PinnedByWinScreen.Add(window);
            else PinnedByWinScreen.Remove(window);
            Unpinned.Remove(window);
        }
    }

    internal static bool IsUnpinnedForNow(nint window) { lock (Unpinned) return Unpinned.Contains(window); }

    internal static bool AnyUnpinnedForNow { get { lock (Unpinned) return Unpinned.Count != 0; } }

    private static void Unpin(nint window)
    {
        lock (Unpinned)
        {
            if (!PinnedByWinScreen.Contains(window) || Unpinned.Contains(window)) return;
            User32.SetWindowPos(window, User32.HWND_NOTOPMOST, 0, 0, 0, 0, Flags);
            if (IsTopmost(window)) return;
            Unpinned.Add(window);
        }
        Diagnostics.Log.Event("layer.unpinnedForNow", ("program", Diagnostics.LogValue.ProgramOf(window)));
    }

    // Once no menu, dialog or Settings is open.
    private static void PinAgain()
    {
        nint[] windows;
        lock (Unpinned)
        {
            if (Unpinned.Count == 0) return;
            windows = Unpinned.ToArray();
            Unpinned.Clear();
        }
        foreach (nint window in windows.Where(User32.IsWindow))
        {
            User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
            Diagnostics.Log.Event("layer.pinnedAgain", ("program", Diagnostics.LogValue.ProgramOf(window)), ("pinned", IsTopmost(window)));
        }
    }

    private const int MostPushes = 3, PushWindowMilliseconds = 1000;
    private static readonly Dictionary<nint, (int Count, long Since)> Pushed = [];

    private static bool IsTopmost(nint window) => (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;

    private static long _pushes;
    internal static long PushCount => Interlocked.Read(ref _pushes);

    private static nint Lift(nint window, nint after)
    {
        if (after == window || _refused?.Invoke(window) == true) return window;
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
            // Nor another program's overlay that takes no clicks and no focus, such as pen input's handwriting canvas
            // over every screen or AMD's recording overlay: they sit in a layer nothing can be put over, so a menu
            // "under" one was lifted again at every check.
            if ((style & ClickThroughOverlay) == ClickThroughOverlay && User32.GetWindowThreadProcessId(window, out uint owner) != 0 &&
                owner != self) continue;
            // Nor does a window too small to cover anything, such as the tiny helpers Explorer keeps always-on-top.
            if (!User32.GetWindowRect(window, out Rect bounds) || bounds.Right - bounds.Left < 8 || bounds.Bottom - bounds.Top < 8) continue;
            order.Add(window);
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || IsOverflowClass(name))
                taskbars.Add(window);
            if (IsOverflowClass(name)) overflowOpen = true;
            if (IsSystemSurface(window)) { system.Add(window); continue; }
            if (User32.GetWindowThreadProcessId(window, out uint process) == 0 || process != self) continue;
            if (IsTip(window)) tips.Add(window);
            // A combo box's open list too: counted as a window over its dialog, the dialog went over it.
            if (Control.FromHandle(window) is ToolStripDropDown || name is "#32768" or "ComboLBox") menus.Add(window);
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
