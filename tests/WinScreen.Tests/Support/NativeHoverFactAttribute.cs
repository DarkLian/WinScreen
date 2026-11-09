using System.Runtime.InteropServices;
using Xunit;

namespace WinScreen.Tests.Support;

internal sealed class NativeHoverFactAttribute : FactAttribute
{
    public NativeHoverFactAttribute(bool requiresWindows11 = false)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native hover helper has runtime validation only on native x64 Windows.";
        else if (requiresWindows11 && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Skip = "Enhanced hover rollout requires Windows 11; Windows 10 cold-source visibility is not consistently validated.";
    }
}

internal sealed class NativeHoverTheoryAttribute : TheoryAttribute
{
    public NativeHoverTheoryAttribute(bool requiresWindows11 = false)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native hover helper has runtime validation only on native x64 Windows.";
        else if (requiresWindows11 && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Skip = "Enhanced hover rollout requires Windows 11; Windows 10 cold-source visibility is not consistently validated.";
    }
}

internal sealed class Windows10HoverFallbackFactAttribute : FactAttribute
{
    public Windows10HoverFallbackFactAttribute()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Skip = "This regression checks the Windows 10 unavailable/fallback path.";
    }
}
