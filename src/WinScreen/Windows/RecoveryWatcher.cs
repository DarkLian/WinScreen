using System.Diagnostics;
using WinScreen.Hotkeys;

namespace WinScreen.Windows;

/// <summary>
/// While a window is concealed, a second small copy of WinScreen waits for this one to die, so a killed
/// app doesn't leave that window invisible until the next start. It holds nothing, polls nothing and
/// goes away as soon as nothing is concealed.
/// </summary>
internal sealed class RecoveryWatcher : IDisposable
{
    private const string Flag = "--watch";
    private const string RecoveryMutex = @"Local\WinScreen.Recovery";
    private const int RecoveryWaitMilliseconds = 5000;

    private readonly Func<int, Process?> _start;
    private readonly object _gate = new();
    private Process? _watcher;
    private EventWaitHandle? _stop;
    private long _retryAt;

    public RecoveryWatcher() : this(StartCopy)
    {
    }

    internal RecoveryWatcher(Func<int, Process?> start) => _start = start;

    /// <summary>True when a watcher is running, for tests.</summary>
    internal bool IsRunning
    {
        get { lock (_gate) return _watcher is { HasExited: false }; }
    }

    private static string StopName(int parent) => @"Local\WinScreen.Watch." + parent;

    /// <summary>Makes a watcher exist exactly while one is needed. Failing to start is never a reason to stop the app.</summary>
    public void Follow(bool needed)
    {
        lock (_gate)
        {
            if (!needed)
            {
                Stop();
                _retryAt = 0;
                return;
            }

            if (_watcher is { HasExited: false })
                return;
            if (Environment.TickCount64 < _retryAt) return;
            Stop();
            try
            {
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(Environment.ProcessId));
                _watcher = _start(Environment.ProcessId);
                if (_watcher is null) _retryAt = Environment.TickCount64 + 1000;
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or
                                                UnauthorizedAccessException or InvalidOperationException)
            {
                Stop();
                _retryAt = Environment.TickCount64 + 1000;
            }
        }
    }

    private void Stop()
    {
        _stop?.Set();
        _stop?.Dispose();
        _stop = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate) Stop();
    }

    /// <summary>Starts another copy of this program, but only from the real app: never from a test host.</summary>
    private static Process? StartCopy(int parent)
    {
        string? path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileNameWithoutExtension(path), "WinScreen", StringComparison.OrdinalIgnoreCase))
            return null;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Flag);
        start.ArgumentList.Add(parent.ToString());
        return Process.Start(start);
    }

    /// <summary>The parent's id when this copy was started to watch, or null for a normal start.</summary>
    public static int? ParentFrom(string[] arguments) =>
        arguments is [Flag, var text] && int.TryParse(text, out int parent) && parent > 0 ? parent : null;

    /// <summary>
    /// Waits for the parent to die or to say it no longer needs this. Runs in the watcher, and asks
    /// for recovery only after a real death with no new copy of WinScreen already in charge.
    /// </summary>
    /// <param name="runningInstance">The lock a running WinScreen holds. Tests pass their own, so a copy of
    /// WinScreen running on the same PC doesn't change the result.</param>
    public static void Watch(int parent, Action recover, string runningInstance = RunningInstance.MutexName)
        => WatchUntilRecovered(parent, () => { recover(); return true; }, runningInstance);

    public static void WatchUntilRecovered(int parent, Func<bool> recover, string runningInstance = RunningInstance.MutexName)
    {
        using EventWaitHandle stop = new(false, EventResetMode.ManualReset, StopName(parent));
        if (!WaitForParent(parent, stop))
            return;

        while (!stop.WaitOne(0))
        {
            using Mutex guard = ForRecovery(out bool held);
            if (held)
            {
                try
                {
                    if (Mutex.TryOpenExisting(runningInstance, out Mutex? running))
                    {
                        running.Dispose();
                        return;
                    }
                    if (recover()) return;
                }
                finally { guard.ReleaseMutex(); }
            }
            if (stop.WaitOne(1000)) return;
        }
    }

    /// <summary>
    /// Waits for the parent to end. False when it said it no longer needs a watcher. A parent that is already gone, or
    /// is dying at this very moment, counts as ended: it can die between being found and being opened, right after it
    /// started this watcher, and that must lead to recovery, never to the watcher failing.
    /// </summary>
    internal static bool WaitForParent(int parent, WaitHandle stop)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(parent);
            using var exited = new ManualResetEvent(false)
                { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, false) };
            return WaitHandle.WaitAny([exited, stop]) == 0;
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return !stop.WaitOne(0);
        }
        finally { process?.Dispose(); }
    }

    /// <summary>Only one copy recovers at a time, so a restart and a watcher can't both act on the journal.</summary>
    public static Mutex ForRecovery(out bool held)
    {
        var mutex = new Mutex(false, RecoveryMutex);
        try { held = mutex.WaitOne(RecoveryWaitMilliseconds); }
        catch (AbandonedMutexException) { held = true; } // The other copy died; recovery is still safe to run.
        return mutex;
    }
}
