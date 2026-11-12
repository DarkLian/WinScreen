using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// Puts a moved menu back the moment its app moves it again, while a view is using it. Windows 11's
/// newer menus (File Explorer's tab menus) put themselves back at the place their app worked out a few times while
/// they open, and now and then while they are in use; the regular sweep only notices that up to a frame or two later.
/// Windows reports each move as it happens, so the menu goes straight back instead. A menu is held while the view's
/// sweeps keep seeing it, and let go a short while after they stop.
/// </summary>
internal static class PopupGuard
{
    private const uint Shown = 0x8002, LocationChange = 0x800B, OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    private const int QuitMessage = 0x0012, StartMessage = 0x0400;
    /// <summary>How long a menu stays held after the last sweep that saw it.</summary>
    internal const int HoldMilliseconds = 800;

    private sealed record Held(Point Place, long Until, uint Process);
    private static readonly Dictionary<nint, Held> _held = [];
    private static readonly Dictionary<uint, nint> _hooks = []; // Process, hook.
    private static readonly Dictionary<uint, nint> _showHooks = [];
    private static readonly List<(uint Process, Action Opened)> _watchers = [];
    private static uint _thread;
    private static readonly unsafe nint Callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnEvent;

    /// <summary>
    /// Keeps <paramref name="popup"/> at <paramref name="place"/> for a short while; called again while the popup is
    /// in use, it keeps it there for longer.
    /// </summary>
    internal static void Hold(nint popup, Point place)
    {
        bool added;
        lock (_held)
        {
            added = !_held.TryGetValue(popup, out var held) || held.Place != place;
            uint process = held?.Process ?? 0;
            if (process == 0) User32.GetWindowThreadProcessId(popup, out process);
            if (process == 0) return;
            _held[popup] = new Held(place, Environment.TickCount64 + HoldMilliseconds, process);
            if (added) EnsureThread();
        }
        if (added) PostThreadMessageW(_thread, StartMessage, 0, 0);
    }

    /// <summary>
    /// Calls <paramref name="opened"/> as soon as one of these menus appears in <paramref name="process"/>, so it is
    /// moved by the view at once instead of at the next sweep. Ends when the result is disposed.
    /// </summary>
    internal static IDisposable Watch(uint process, Action opened)
    {
        var entry = (process, opened);
        lock (_held) { _watchers.Add(entry); EnsureThread(); }
        PostThreadMessageW(_thread, StartMessage, 0, 0);
        return new Unwatch(() => { lock (_held) _watchers.Remove(entry); PostThreadMessageW(_thread, StartMessage, 0, 0); });
    }

    private sealed class Unwatch(Action stop) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) stop(); }
    }

    private static void EnsureThread()
    {
        if (_thread != 0) return;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _thread = GetCurrentThreadId();
            PeekMessageW(out _, 0, 0, 0, 0); // A message queue, before anyone posts to it.
            ready.Set();
            Loop();
        }) { IsBackground = true, Name = "WinScreen popup guard" };
        thread.Start();
        ready.Wait();
    }

    private static void Loop()
    {
        while (true)
        {
            // Wakes at least every 100 ms to let go of menus held long enough.
            if (MsgWaitForMultipleObjects(0, 0, false, 100, 0x04FF) != 0x102)
                while (PeekMessageW(out Msg message, 0, 0, 0, 1))
                {
                    if (message.Message == QuitMessage) return;
                    TranslateMessage(message);
                    DispatchMessageW(message);
                }
            Tidy();
        }
    }

    /// <summary>Hooks each process with a held menu and unhooks the rest. Runs on the guard's own thread.</summary>
    private static void Tidy()
    {
        long now = Environment.TickCount64;
        HashSet<uint> wanted, watched;
        lock (_held)
        {
            watched = _watchers.Select(watcher => watcher.Process).ToHashSet();
            foreach (nint gone in _held.Where(entry => entry.Value.Until <= now || !User32.IsWindowVisible(entry.Key)).Select(entry => entry.Key).ToArray())
                _held.Remove(gone);
            wanted = _held.Values.Select(held => held.Process).ToHashSet();
        }
        foreach (uint process in _hooks.Keys.Where(process => !wanted.Contains(process)).ToArray())
        {
            User32.UnhookWinEvent(_hooks[process]);
            _hooks.Remove(process);
        }
        foreach (uint process in wanted.Where(process => !_hooks.ContainsKey(process)))
            if (User32.SetWinEventHook(LocationChange, LocationChange, 0, Callback, process, 0, OutOfContext | SkipOwnProcess) is var hook and not 0)
                _hooks[process] = hook;
        foreach (uint process in _showHooks.Keys.Where(process => !watched.Contains(process)).ToArray())
        {
            User32.UnhookWinEvent(_showHooks[process]);
            _showHooks.Remove(process);
        }
        foreach (uint process in watched.Where(process => !_showHooks.ContainsKey(process)))
            if (User32.SetWinEventHook(Shown, Shown, 0, Callback, process, 0, OutOfContext | SkipOwnProcess) is var hook and not 0)
                _showHooks[process] = hook;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Window; public uint Message; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, nint handles, bool all, uint milliseconds, uint wake);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, int message, nint w, nint l);

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (item != 0 || child != 0) return; // The window itself, not a part of it.
        if (kind == Shown)
        {
            if (Windows.WindowInfo.ClassName(window) != SourcePopups.WinUiPopup) return;
            User32.GetWindowThreadProcessId(window, out uint process);
            Action[] opened;
            lock (_held) opened = _watchers.Where(watcher => watcher.Process == process).Select(watcher => watcher.Opened).ToArray();
            // Never let an exception out of a callback Windows makes into native code.
            foreach (Action action in opened)
                try { action(); } catch (ObjectDisposedException) { }
            return;
        }
        Held? held;
        lock (_held) if (!_held.TryGetValue(window, out held) || held.Until <= Environment.TickCount64) return;
        if (!User32.GetWindowRect(window, out Rect r) || (r.Left == held.Place.X && r.Top == held.Place.Y)) return;
        User32.SetWindowPos(window, 0, held.Place.X, held.Place.Y, 0, 0, Move);
    }
}
