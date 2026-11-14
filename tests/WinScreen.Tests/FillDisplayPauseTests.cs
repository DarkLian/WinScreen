using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Windows reports a window that covers its display as a full-screen app, and Fill display makes exactly that. A
/// window WinScreen filled itself must not make WinScreen pause as it does for a game, or the toolbar and its menu
/// close the moment they open (the owner's report on 1.1.0-beta, F5).
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class FillDisplayPauseTests
{
    [Fact]
    public void A_window_WinScreen_filled_is_not_taken_for_a_full_screen_app()
    {
        using var target = new TargetProcess(new Rectangle(200, 160, 500, 360), "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            try
            {
                Assert.True(windows.ToggleBorderless(target.Handle, fill: true));
                Assert.True(windows.IsBorderlessFilled(target.Handle));
                Desktop.Activate(target.Handle);
                // What Windows says. If it ever stops saying so, this test has nothing left to guard.
                Desktop.WaitFor(() => FullScreen.Current() != FullScreenState.None, 2000, Application.DoEvents);
                FullScreenState reported = FullScreen.Current();
                Assert.NotEqual(FullScreenState.None, reported);

                Assert.Equal(FullScreenState.None, FullScreenPolicy.Seen(reported, target.Handle, windows.IsBorderlessFilled));
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Theory]
    [InlineData(FullScreenState.Busy, true, FullScreenState.None)]
    [InlineData(FullScreenState.Busy, false, FullScreenState.Busy)]
    [InlineData(FullScreenState.Exclusive, true, FullScreenState.Exclusive)]
    [InlineData(FullScreenState.Presentation, true, FullScreenState.Presentation)]
    [InlineData(FullScreenState.None, false, FullScreenState.None)]
    public void Only_a_window_WinScreen_filled_is_excused(FullScreenState reported, bool filledByWinScreen, FullScreenState seen) =>
        Assert.Equal(seen, FullScreenPolicy.Seen(reported, 1, _ => filledByWinScreen));
}
