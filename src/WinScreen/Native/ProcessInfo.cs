using System.Runtime.InteropServices;

namespace WinScreen.Native;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    // The value Process.StartTime gives, converted the same way, so records written by older copies still match; without
    // the Process object, as views and overlays ask many times a second.
    public static long? StartTicks(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimited, false, processId);
        if (process == 0) return null;
        try { return GetProcessTimes(process, out long creation, out _, out _, out _) ? DateTime.FromFileTime(creation).ToUniversalTime().Ticks : null; }
        finally { CloseHandle(process); }
    }

    // Works for elevated processes too.
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

    public static bool SelfElevated { get; } = IsElevated((uint)Environment.ProcessId);

    // A standard account would be asked for someone else's password, and run as that person.
    public static bool CanElevate { get; } = SelfElevated || ElevationType() == TokenElevationTypeLimited;

    // Only with a split token (UAC on, an administrator account) can an ordinary copy run beside an elevated one.
    public static bool SplitToken { get; } = ElevationType() is TokenElevationTypeFull or TokenElevationTypeLimited;

    private const int TokenElevationTypeKind = 18;
    private const int TokenElevationTypeFull = 2;
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

    // A normal process may not open an elevated one's token, so a refusal counts as yes, which also keeps WinScreen
    // away from it.
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
