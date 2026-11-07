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
                return;
            }

            if (_watcher is { HasExited: false })
                return;
            Stop();
            try
            {
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(Environment.ProcessId));
                _watcher = _start(Environment.ProcessId);
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or
                                                UnauthorizedAccessException or InvalidOperationException)
            {
                Stop();
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
    public static void Watch(int parent, Action recover)
    {
        using EventWaitHandle stop = new(false, EventResetMode.ManualReset, StopName(parent));
        Process? process = null;
        try { process = Process.GetProcessById(parent); }
        catch (ArgumentException) { } // Already gone.
        using (process)
        {
            if (process is not null)
            {
                using var exited = new ManualResetEvent(false)
                    { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, false) };
                if (WaitHandle.WaitAny([exited, stop]) == 1)
                    return;
            }
            else if (stop.WaitOne(0))
            {
                return;
            }
        }

        using Mutex guard = ForRecovery(out bool held);
        try
        {
            if (!Mutex.TryOpenExisting(RunningInstance.MutexName, out Mutex? running))
                recover();
            running?.Dispose();
        }
        finally
        {
            if (held) guard.ReleaseMutex();
        }
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
