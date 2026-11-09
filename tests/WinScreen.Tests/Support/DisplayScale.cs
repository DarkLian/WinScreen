using System.Runtime.InteropServices;

namespace WinScreen.Tests.Support;

/// <summary>
/// Changes the primary display's scale while signed in, as Settings does. Only ever used inside the test VM: it
/// refuses anywhere else, so a test run can never rescale someone's own screen.
/// </summary>
internal static class DisplayScale
{
    private static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>A Hyper-V guest, which is what the test VM is.</summary>
    public static bool Allowed =>
        Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters") is not null;

    public static int Current => Steps[Read().Current];

    /// <summary>Sets the scale and waits for apps to hear about it.</summary>
    public static void Set(int percent)
    {
        if (!Allowed) throw new InvalidOperationException("The display scale is only changed inside the test VM.");
        var (header, recommended, current) = Read();
        int wanted = Array.IndexOf(Steps, percent);
        if (wanted < 0) throw new ArgumentOutOfRangeException(nameof(percent));
        if (wanted == current) return;
        var set = new ScaleSet { Header = header, Relative = wanted - recommended };
        set.Header.Type = -4; // DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE
        set.Header.Size = Marshal.SizeOf<ScaleSet>();
        if (DisplayConfigSetDeviceInfo(ref set) != 0) throw new InvalidOperationException("Windows refused the scale change.");
        Thread.Sleep(2500);
    }

    private static (Header Header, int Recommended, int Current) Read()
    {
        if (GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0) throw new InvalidOperationException("No display configuration.");
        var paths = new PathInfo[pathCount];
        var modes = new byte[modeCount * 64];
        if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0) != 0) throw new InvalidOperationException("No display configuration.");
        var get = new ScaleGet
        {
            Header = new Header { Type = -3, Size = Marshal.SizeOf<ScaleGet>(), Adapter = paths[0].Source.Adapter, Id = paths[0].Source.Id },
        };
        if (DisplayConfigGetDeviceInfo(ref get) != 0) throw new InvalidOperationException("The scale can't be read.");
        int recommended = Math.Abs(get.Minimum);
        return (get.Header, recommended, recommended + get.Current);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public int Type; public int Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct ScaleGet { public Header Header; public int Minimum, Current, Maximum; }
    [StructLayout(LayoutKind.Sequential)] private struct ScaleSet { public Header Header; public int Relative; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSource { public Luid Adapter; public uint Id; public uint ModeIndex; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 72)] private struct PathInfo { public PathSource Source; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] byte[] modeArray, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref ScaleGet info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref ScaleSet info);
}
