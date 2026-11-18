using System.Runtime.InteropServices;

namespace WinScreen.Native;

// A window program's output doesn't reach the prompt that started it; this attaches to that prompt for the command
// line.
internal static partial class ParentConsole
{
    private const uint ParentProcess = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint process);

    public static void Attach()
    {
        if (!Console.IsOutputRedirected && AttachConsole(ParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }
}
