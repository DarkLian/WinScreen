using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace WinScreen.Pip;

internal static partial class PipDisplay
{
    private static readonly ConcurrentDictionary<string, Task<int>> RefreshQueries = new();
    internal static int CachedRefresh(nint hwnd)
    {
        return CachedRefresh(Screen.FromHandle(hwnd).DeviceName);
    }

    internal static int CachedRefresh(string device)
    {
        // Display-driver queries can block for seconds. Never issue them from a WinForms timer.
        var query = RefreshQueries.GetOrAdd(device, name => Task.Run(() => RefreshDevice(name)));
        return query.IsCompletedSuccessfully ? query.Result : 60;
    }

    internal static void InvalidateRefresh() => RefreshQueries.Clear();

    internal static nint MonitorOf(Screen screen) =>
        Native.User32.MonitorFromPoint(new Native.NativePoint { X = screen.Bounds.Left, Y = screen.Bounds.Top }, 2);

    // DEVMODEW's display union is 16 bytes; the full layout must be retained for EnumDisplaySettings.

    private static int RefreshDevice(string device)
    {
        var mode = new Native.DisplayMode { Size = (ushort)Marshal.SizeOf<Native.DisplayMode>(), Device = "", Form = "" };
        return Native.User32.EnumDisplaySettings(device, -1, ref mode) && mode.Frequency is >= 15 and <= 480
            ? (int)mode.Frequency : 60;
    }
}
