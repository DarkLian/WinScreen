using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Which starts open Settings. Starting for real is in AppEndToEndTests.</summary>
public sealed class LaunchTests
{
    private const string Startup = @"C:\Users\A\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string CommonStartup = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp";

    [Theory]
    [InlineData(@"C:\Users\A\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\WinScreen.lnk")]
    [InlineData(@"C:\Users\A\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\WinScreen.lnk")]
    [InlineData(@"C:\Users\A\Desktop\WinScreen.lnk")]
    public void A_shortcut_opened_by_hand_opens_Settings(string link) =>
        Assert.True(Launch.ByHand(link, Startup, CommonStartup));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Startup + @"\WinScreen.lnk")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\WinScreen.lnk")]
    public void Sign_in_and_starts_without_a_shortcut_stay_in_the_tray(string? link) =>
        Assert.False(Launch.ByHand(link, Startup, CommonStartup));
}
