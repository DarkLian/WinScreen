using System.Runtime.InteropServices;
using Xunit;

namespace WinScreen.Tests.Support;

internal sealed class NativeHoverFactAttribute : FactAttribute
{
    public NativeHoverFactAttribute()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native hover helper has runtime validation only on native x64 Windows.";
    }
}

internal sealed class NativeHoverTheoryAttribute : TheoryAttribute
{
    public NativeHoverTheoryAttribute()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native hover helper has runtime validation only on native x64 Windows.";
    }
}
