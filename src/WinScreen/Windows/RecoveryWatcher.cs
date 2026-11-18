using System.Diagnostics;
using WinScreen.Hotkeys;

namespace WinScreen.Windows;

// Holds nothing, polls nothing, and goes away as soon as nothing is concealed.
internal sealed class RecoveryWatcher : IDisposable
{
    private const string Flag = "--watch";
    internal const string RecoveryMutex = @"Local\WinScreen.Recovery";
    private const int RecoveryWaitMilliseconds = 5000;

    private readonly Func<int, Process?> _start;
    private readonly object _gate = new();
    private Process? _watcher;
    private EventWaitHandle? _stop;
    private long _retryAt;

    public RecoveryWatcher() : this(StartCopy)
    {
    }

    public RecoveryWatcher(IReadOnlyList<string> arguments) : this(parent => StartCopy(parent, arguments))
    {
    }

    internal RecoveryWatcher(Func<int, Process?> start) => _start = start;

    internal bool IsRunning
    {
        get { lock (_gate) return _watcher is { HasExited: false }; }
    }

    private static string StopName(int parent) => @"Local\WinScreen.Watch." + parent;

    // Failing to start is never a reason to stop the app.
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

    // A watcher still starting opens the stop signal only after this copy closed it, and would then take a normal exit
    // for a death and run a recovery. It recovers only after this copy died, so ending it while this copy lives loses
    // nothing.
    private void Stop()
    {
        _stop?.Set();
        if (_watcher is { } watcher)
        {
            try { watcher.Kill(); }
            catch (Exception gone) when (gone is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Diagnostics.Log.Swallowed(gone);
            }
        }
        _stop?.Dispose();
        _stop = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate) Stop();
    }

    // Only from the real app: never from a test host.
    private static Process? StartCopy(int parent) => StartCopy(parent, []);

    private static Process? StartCopy(int parent, IReadOnlyList<string> arguments)
    {
        string? path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileNameWithoutExtension(path), "WinScreen", StringComparison.OrdinalIgnoreCase))
            return null;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Flag);
        start.ArgumentList.Add(parent.ToString());
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    public static int? ParentFrom(string[] arguments) =>
        (arguments.Length == 2 || arguments.Length == 3 && arguments[2] == HelperRole) && arguments[0] == Flag &&
        int.TryParse(arguments[1], out int parent) && parent > 0 ? parent : null;

    internal const string HelperRole = "--administrator-helper";

    // Recovers only after a real death with no new copy of WinScreen already in charge.
    public static void Watch(int parent, Action recover, string runningInstance = RunningInstance.MutexName)
        => WatchUntilRecovered(parent, () => { recover(); return true; }, runningInstance);

    public static void WatchUntilRecovered(int parent, Func<bool> recover, string runningInstance = RunningInstance.MutexName,
        string recoveryMutex = RecoveryMutex)
    {
        using EventWaitHandle stop = new(false, EventResetMode.ManualReset, StopName(parent));
        if (!WaitForParent(parent, stop))
            return;

        while (!stop.WaitOne(0))
        {
            using Mutex guard = ForRecovery(out bool held, recoveryMutex);
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

    // A parent already gone, or dying right now, counts as ended: it can die between being found and opened, right
    // after starting this watcher, and that must lead to recovery.
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

    // Only one copy recovers at a time, so a restart and a watcher can't both act on the journal.
    public static Mutex ForRecovery(out bool held, string name = RecoveryMutex)
    {
        var mutex = new Mutex(false, name);
        try { held = mutex.WaitOne(RecoveryWaitMilliseconds); }
        catch (AbandonedMutexException) { held = true; } // The other copy died; recovery is still safe to run.
        return mutex;
    }
}
