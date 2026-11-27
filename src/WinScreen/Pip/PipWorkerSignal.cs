using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Pip;

// Event-driven capture, with a private high-resolution deadline timer for a pending final frame.
internal sealed partial class PipWorkerSignal : IDisposable
{
    private readonly AutoResetEvent _changed = new(false);
    private readonly EventWaitHandle _deadline = new(false, EventResetMode.AutoReset);
    private readonly WaitHandle[] _handles;

    internal PipWorkerSignal()
    {
        nint timer = Native.Kernel32.CreateWaitableTimerEx(0, 0, 2, 0x1F0003);
        if (timer == 0) timer = Native.Kernel32.CreateWaitableTimerEx(0, 0, 0, 0x1F0003);
        if (timer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _deadline.SafeWaitHandle = new SafeWaitHandle(timer, true);
        _handles = [_changed, _deadline];
    }
    internal void Set() => _changed.Set();
    internal void Wait(int milliseconds) => Wait(TimeSpan.FromMilliseconds(milliseconds));

    internal void Wait(TimeSpan time)
    {
        long due = -Math.Max(1, time.Ticks);
        if (!Native.Kernel32.SetWaitableTimerEx(_deadline.SafeWaitHandle, in due, 0, 0, 0, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(_handles);
    }
    public void Dispose() { _changed.Dispose(); _deadline.Dispose(); }
}
