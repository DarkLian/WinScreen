using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Native;

// Asked of Windows, so the other end can't claim to be someone else.
internal static partial class PipePeers
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);

    internal static uint Client(SafePipeHandle pipe) => GetNamedPipeClientProcessId(pipe, out uint process) ? process : 0;

    internal static uint Server(SafePipeHandle pipe) => GetNamedPipeServerProcessId(pipe, out uint process) ? process : 0;
}
