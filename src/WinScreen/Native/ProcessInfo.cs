using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>What Windows will say about another process.</summary>
internal static partial class ProcessInfo
{
    private const uint ProcessQueryLimited = 0x1000;
    private const uint TokenQuery = 0x8;
    private const int TokenElevation = 20;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int kind, out int value, int size, out int returned);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* path, ref uint size);

    /// <summary>The full path of a process's program, or "" when Windows won't say. Works for elevated processes too.</summary>
    public static unsafe string PathOf(uint processId)
    {
        if (processId == 0) return "";
        nint process = OpenProcess(ProcessQueryLimited, false, processId);
        if (process == 0) return "";
        try
        {
            char* path = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageName(process, 0, path, ref size) ? new string(path, 0, (int)size) : "";
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Whether WinScreen itself was started as administrator, which lets it work with such apps too.</summary>
    public static bool SelfElevated { get; } = IsElevated((uint)Environment.ProcessId);

    /// <summary>
    /// Whether this person can run WinScreen as administrator: they already do, or Windows holds back rights their
    /// account has. A standard account would be asked for someone else's password, and run as that person.
    /// </summary>
    public static bool CanElevate { get; } = SelfElevated || ElevationType() == TokenElevationTypeLimited;

    private const int TokenElevationTypeKind = 18;
    private const int TokenElevationTypeLimited = 3;

    private static int ElevationType()
    {
        nint process = OpenProcess(ProcessQueryLimited, false, (uint)Environment.ProcessId);
        if (process == 0) return 0;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token)) return 0;
            try { return GetTokenInformation(token, TokenElevationTypeKind, out int type, sizeof(int), out _) ? type : 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// True when a process runs as administrator. A normal process isn't allowed to look at an elevated
    /// one's token, so being refused counts as yes. That's also the answer that keeps WinScreen away from it.
    /// </summary>
    public static bool IsElevated(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimited, false, processId);
        if (process == 0)
            return true;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token))
                return true;
            try
            {
                return !GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) || elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
