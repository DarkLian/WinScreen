using System.Runtime.InteropServices;

namespace WinScreen.Updates;

// Each architecture has its own feed, so an installed copy can never take the other's build. Only "win" (x64) is
// published; an ARM64 copy asks for "win-arm64" and finds no update.
internal static class UpdateChannel
{
    public static string Current => For(RuntimeInformation.ProcessArchitecture);

    public static string For(Architecture architecture) => architecture == Architecture.Arm64 ? "win-arm64" : "win";
}
