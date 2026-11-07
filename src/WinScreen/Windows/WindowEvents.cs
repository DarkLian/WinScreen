using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// Says when whole windows may have changed, without polling. It exists only while something needs it,
/// and Windows calls back on the thread that created it. Bursts collapse into one <see cref="Changed"/>,
/// which waits while a mouse button is down, so dragging a window doesn't make everything flicker.
/// </summary>
internal sealed class WindowEvents : IDisposable
{
    private const int SettleMilliseconds = 100;
    private const int SafetyMilliseconds = 5000;
    private const uint OutOfContext = 0;
    private const uint Foreground = 0x0003;
    private const uint MoveSizeEnd = 0x000B;
    private const uint MinimizeStart = 0x0016;
    private const uint MinimizeEnd = 0x0017;
    private const uint Destroy = 0x8001;
    private const uint Reorder = 0x8004;
    private const uint LocationChange = 0x800B;
    private const int WholeWindow = 0;

    private readonly User32.WinEventProc _received;
    private readonly nint[] _hooks;
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = SettleMilliseconds };
    private readonly System.Windows.Forms.Timer _safety = new() { Interval = SafetyMilliseconds };

    /// <summary>How many of these exist now, so tests can prove there are none while a feature is off.</summary>
    internal static int Active { get; private set; }

    /// <summary>Window events seen, for measuring the rate.</summary>
    internal long Received { get; private set; }

    public event Action? Changed;

    public WindowEvents()
    {
        _received = OnEvent;
        nint callback = Marshal.GetFunctionPointerForDelegate(_received);
        _hooks =
        [
            User32.SetWinEventHook(Foreground, Foreground, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(MoveSizeEnd, MoveSizeEnd, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(MinimizeStart, MinimizeEnd, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(Destroy, Reorder, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(LocationChange, LocationChange, 0, callback, 0, 0, OutOfContext),
        ];
        _settle.Tick += (_, _) => Settle();
        _safety.Tick += (_, _) => Settle(); // In case an event was missed.
        _safety.Start();
        Active++;
    }

    private void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (kind != Reorder && (item != WholeWindow || child != 0 || window == 0))
            return; // A reorder is reported against the container, not the window that moved.
        Received++;
        _settle.Enabled = true;
    }

    private void Settle()
    {
        if ((Control.MouseButtons & MouseButtons.Left) != 0)
            return;
        _settle.Enabled = false;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Active--;
        foreach (nint hook in _hooks)
            if (hook != 0)
                User32.UnhookWinEvent(hook);
        _settle.Dispose();
        _safety.Dispose();
        GC.KeepAlive(_received);
    }
}
