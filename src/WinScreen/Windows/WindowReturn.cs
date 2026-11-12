using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

public sealed partial class WindowController
{
    private const uint ReturnDestroyEvent = 0x8001, ReturnLocationEvent = 0x800B;
    private readonly Dictionary<nint, (WindowPlacement Home, nint[] Hooks)> _returning = [];
    private User32.WinEventProc? _returnEvent;

    /// <summary>True while a window put back in full screen is watched, for tests.</summary>
    internal bool IsWatchingReturn(nint window)
    {
        lock (_gate) return _returning.ContainsKey(window);
    }

    /// <summary>
    /// An app that goes full screen remembers where it was, to go back there afterwards. One that went full screen
    /// while parked remembers the parked spot past the edge of the screen, and goes there when it leaves full screen,
    /// long after its view closed. So a window put back while it is full screen is watched, by itself, until it
    /// leaves full screen: if it lands out of reach it goes where it was before the view; anywhere else, it is left
    /// there. Nothing is polled; the watch ends with the window.
    /// </summary>
    private void WatchForReturn(nint window, WindowPlacement home)
    {
        StopWatchingReturn(window);
        User32.GetWindowThreadProcessId(window, out uint process);
        if (process == 0) return;
        _returnEvent ??= OnReturnEvent;
        nint callback = Marshal.GetFunctionPointerForDelegate(_returnEvent);
        nint[] hooks =
        [
            User32.SetWinEventHook(ReturnDestroyEvent, ReturnDestroyEvent, 0, callback, process, 0, 0),
            User32.SetWinEventHook(ReturnLocationEvent, ReturnLocationEvent, 0, callback, process, 0, 0),
        ];
        if (hooks.All(hook => hook != 0)) _returning[window] = (home, hooks);
        else Unhook(hooks);
    }

    private void StopWatchingReturn(nint window)
    {
        if (_returning.Remove(window, out var watch)) Unhook(watch.Hooks);
    }

    private static void Unhook(nint[] hooks)
    {
        foreach (nint hook in hooks)
            if (hook != 0) User32.UnhookWinEvent(hook);
    }

    private void OnReturnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (item != 0 || child != 0) return;
        lock (_gate)
        {
            if (!_returning.TryGetValue(window, out var watch)) return;
            if (kind == ReturnDestroyEvent || !User32.IsWindow(window)) { StopWatchingReturn(window); return; }
            if (User32.IsIconic(window) || !User32.GetWindowRect(window, out Rect bounds)) return;
            Rectangle now = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            Rectangle[] monitors = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
            if (FillsAMonitor(now, monitors)) return; // Still full screen.
            StopWatchingReturn(window);
            if (!OutOfReach(now, monitors)) return; // It left full screen to somewhere it can be seen.
            WindowPlacement home = watch.Home;
            if (home.ShowCommand == 1) home.ShowCommand = User32.SW_SHOWNOACTIVATE;
            User32.SetWindowPlacement(window, ref home);
        }
    }

    /// <summary>Covers a whole monitor, as an app in full screen does (allowing a pixel or two short).</summary>
    internal static bool FillsAMonitor(Rectangle window, IReadOnlyList<Rectangle> monitors) =>
        monitors.Any(monitor =>
        {
            Rectangle overlap = Rectangle.Intersect(window, monitor);
            return overlap.Width >= monitor.Width - 2 && overlap.Height >= monitor.Height - 2;
        });

    /// <summary>No part of it big enough to take hold of is on any monitor: at most a parked strip.</summary>
    internal static bool OutOfReach(Rectangle window, IReadOnlyList<Rectangle> monitors) =>
        monitors.All(monitor =>
        {
            Rectangle overlap = Rectangle.Intersect(window, monitor);
            return overlap.Width < 16 || overlap.Height < 16;
        });
}
