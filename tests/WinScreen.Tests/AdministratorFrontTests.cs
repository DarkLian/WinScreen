using WinScreen.Native;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

/// <summary>The run with an administrator window in front does what it says, or its results mean nothing.</summary>
[Trait("Category", "Interactive")]
public sealed class AdministratorFrontTests
{
    [Fact]
    public void In_a_run_with_an_administrator_window_in_front_each_test_starts_with_it_in_front_and_input_still_arrives()
    {
        if (!AdministratorFront.On)
        {
            Skipped.Because("Only in a run with an administrator window in front (Invoke-HostRun.ps1 -AdministratorInFront).");
            return;
        }
        Assert.NotEqual(0, AdministratorFront.Window);
        Assert.Equal(AdministratorFront.Window, User32.GetForegroundWindow());
        Assert.True(ProcessInfo.IsElevated(ProcessOf(User32.GetForegroundWindow())));
        // Small and out of the way: maximized, it would hide the tests' windows behind it.
        Assert.False(User32.IsZoomed(AdministratorFront.Window));
        Assert.Equal(AdministratorFront.Spot, Desktop.BoundsOf(AdministratorFront.Window));
        // Sent from this test, Windows would keep both out: the hotkey would never arrive.
        Desktop.GainForegroundRights();
        var to = new Point(AdministratorFront.Spot.Left - 40, AdministratorFront.Spot.Top - 40);
        Desktop.MoveMouse(to);
        Assert.Equal(to, Desktop.CursorPosition());
    }

    private static uint ProcessOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return process;
    }
}
