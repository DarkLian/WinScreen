using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Native;

/// <summary>Who is at the other end of a named pipe: Windows says, so it can't be claimed (10-2).</summary>
internal static partial class PipePeers
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);

    /// <summary>The process that connected to a pipe this one serves, or 0.</summary>
    internal static uint Client(SafePipeHandle pipe) => GetNamedPipeClientProcessId(pipe, out uint process) ? process : 0;

    /// <summary>The process serving a pipe this one connected to, or 0.</summary>
    internal static uint Server(SafePipeHandle pipe) => GetNamedPipeServerProcessId(pipe, out uint process) ? process : 0;
}
