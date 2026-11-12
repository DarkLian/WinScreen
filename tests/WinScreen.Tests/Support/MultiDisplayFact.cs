using Xunit;

namespace WinScreen.Tests.Support;

/// <summary>
/// For actions between displays. Skipped with one display, as on most PCs and the test VM's "clean" checkpoint; the
/// VM's "two-displays" checkpoint has a second, virtual display (Invoke-TestVmRun.ps1 -CheckpointName two-displays).
/// </summary>
public sealed class MultiDisplayFactAttribute : FactAttribute
{
    public MultiDisplayFactAttribute()
    {
        if (Screen.AllScreens.Length < 2) Skip = "Needs a second display.";
    }
}

/// <summary>A theory for actions between displays; see <see cref="MultiDisplayFactAttribute"/>.</summary>
public sealed class MultiDisplayTheoryAttribute : TheoryAttribute
{
    public MultiDisplayTheoryAttribute()
    {
        if (Screen.AllScreens.Length < 2) Skip = "Needs a second display.";
    }
}
