using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Pip;

/// <summary>Event-driven capture, with a private high-resolution deadline timer for a pending final frame.</summary>
internal sealed partial class PipWorkerSignal : IDisposable
{
    private readonly AutoResetEvent _changed = new(false);
    private readonly EventWaitHandle _deadline = new(false, EventResetMode.AutoReset);
    private readonly WaitHandle[] _handles;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(SafeWaitHandle timer, in long due, int period,
        nint callback, nint argument, nint wakeContext, uint tolerance);

    internal PipWorkerSignal()
    {
        nint timer = CreateWaitableTimerEx(0, 0, 2, 0x1F0003);
        if (timer == 0) timer = CreateWaitableTimerEx(0, 0, 0, 0x1F0003);
        if (timer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _deadline.SafeWaitHandle = new SafeWaitHandle(timer, true);
        _handles = [_changed, _deadline];
    }
    internal void Set() => _changed.Set();
    internal void Wait(int milliseconds)
    {
        long due = -Math.Max(1, milliseconds) * 10_000L;
        if (!SetWaitableTimerEx(_deadline.SafeWaitHandle, in due, 0, 0, 0, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(_handles);
    }
    public void Dispose() { _changed.Dispose(); _deadline.Dispose(); }
}
