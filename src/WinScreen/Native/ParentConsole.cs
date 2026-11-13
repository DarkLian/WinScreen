using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// WinScreen is a window program, so a command prompt that starts it shows nothing it writes. For the command line
/// (<c>--list-commands</c>, errors), it writes to the prompt it was started from, or to wherever its output was sent.
/// </summary>
internal static partial class ParentConsole
{
    private const uint ParentProcess = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint process);

    /// <summary>Makes Console.Out and Console.Error reach the parent's prompt, unless output was already sent elsewhere.</summary>
    public static void Attach()
    {
        if (!Console.IsOutputRedirected && AttachConsole(ParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }
}
