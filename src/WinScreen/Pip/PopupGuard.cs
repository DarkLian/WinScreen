using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

// Windows 11's newer menus (File Explorer's tab menus) put themselves back where their app worked out a few times while
// they open, and now and then in use; the sweep sees that a frame or two late, so each reported move is undone at once.
// A menu is held while the view's sweeps keep seeing it, and let go a short while after they stop.
internal static class PopupGuard
{
    private const uint Shown = 0x8002, LocationChange = 0x800B, OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    private const int QuitMessage = 0x0012, StartMessage = 0x0400;
    internal const int HoldMilliseconds = 800;

    private sealed record Held(Point Place, long Until, uint Process);
    private static readonly Dictionary<nint, Held> _held = [];
    private static readonly Dictionary<uint, nint> _hooks = []; // Process, hook.
    private static readonly Dictionary<uint, nint> _showHooks = [];
    private static readonly List<(uint Process, Action Opened)> _watchers = [];
    private static uint _thread;
    private static readonly unsafe nint Callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnEvent;

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
        if (added) User32.PostThreadMessage(_thread, StartMessage, 0, 0);
    }

    internal static void Release(nint popup)
    {
        lock (_held) _held.Remove(popup);
    }

    internal static IDisposable Watch(uint process, Action opened)
    {
        var entry = (process, opened);
        lock (_held) { _watchers.Add(entry); EnsureThread(); }
        User32.PostThreadMessage(_thread, StartMessage, 0, 0);
        return new Unwatch(() => { lock (_held) _watchers.Remove(entry); User32.PostThreadMessage(_thread, StartMessage, 0, 0); });
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
            _thread = Kernel32.GetCurrentThreadId();
            User32.PeekMessage(out _, 0, 0, 0, 0); // A message queue, before anyone posts to it.
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
            if (User32.MsgWaitForMultipleObjects(0, 0, false, 100, 0x04FF) != 0x102)
                while (User32.PeekMessage(out Msg message, 0, 0, 0, 1))
                {
                    if (message.Message == QuitMessage) return;
                    User32.TranslateMessage(message);
                    User32.DispatchMessage(message);
                }
            Tidy();
        }
    }

    // Runs on the guard's own thread.
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

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        // Never let an exception out of a callback Windows makes into native code.
        try { Handle(kind, window, item, child); }
        catch (Exception e) when (e is not OutOfMemoryException) { Diagnostics.Log.Swallowed(e); }
    }

    private static void Handle(uint kind, nint window, int item, int child)
    {
        if (item != 0 || child != 0) return; // The window itself, not a part of it.
        if (kind == Shown)
        {
            if (Windows.WindowInfo.ClassName(window) != SourcePopups.WinUiPopup) return;
            User32.GetWindowThreadProcessId(window, out uint process);
            Action[] opened;
            lock (_held) opened = _watchers.Where(watcher => watcher.Process == process).Select(watcher => watcher.Opened).ToArray();
            // One watcher that has gone mustn't keep the others from hearing.
            foreach (Action action in opened)
                try { action(); } catch (ObjectDisposedException caught) { Diagnostics.Log.Swallowed(caught); }
            return;
        }
        Held? held;
        lock (_held) if (!_held.TryGetValue(window, out held) || held.Until <= Environment.TickCount64) return;
        if (!User32.GetWindowRect(window, out Rect r) || (r.Left == held.Place.X && r.Top == held.Place.Y)) return;
        User32.SetWindowPos(window, 0, held.Place.X, held.Place.Y, 0, 0, Move);
    }
}
