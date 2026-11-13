using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>The graphics adapters Windows has displays on, by name, without WMI.</summary>
internal static class DisplayAdapters
{
    private const uint AttachedToDesktop = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    /// <summary>Each adapter's name once, those driving a display first.</summary>
    public static IReadOnlyList<(string Name, bool Active)> All()
    {
        var adapters = new List<(string Name, bool Active)>();
        var info = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(null, index, ref info, 0) && index < 32; index++)
        {
            bool active = (info.Flags & AttachedToDesktop) != 0;
            int known = adapters.FindIndex(adapter => adapter.Name == info.Description);
            if (known < 0) adapters.Add((info.Description, active));
            else if (active) adapters[known] = (info.Description, true);
            info = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }
        return [.. adapters.OrderByDescending(adapter => adapter.Active)];
    }
}
