using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Native;

internal static partial class Kernel32
{
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static extern nint GetModuleHandle(nint name);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process2(nint process, out ushort machine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    public static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimerEx(SafeWaitHandle timer, in long due, int period,
        nint callback, nint argument, nint wakeContext, uint tolerance);
}

internal static partial class Shell32
{
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);
}

internal static class Ole32
{
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid implementation, nint outer, uint context, in Guid wanted, out nint instance);
}

// WinRT strings and factories, and the Direct3D and composition devices made from them.
internal static partial class WinRt
{
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WindowsCreateString(string value, int length, out nint result);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint name, in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);

    [DllImport("dcomp.dll")]
    public static extern int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);
}
