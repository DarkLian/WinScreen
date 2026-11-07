using System.Runtime.InteropServices;

namespace WinScreen.Updates;

/// <summary>Each architecture updates from its own feed, so an installed copy can never take the other's build.</summary>
internal static class UpdateChannel
{
    public static string Current => For(RuntimeInformation.ProcessArchitecture);

    public static string For(Architecture architecture) => architecture == Architecture.Arm64 ? "win-arm64" : "win";
}
