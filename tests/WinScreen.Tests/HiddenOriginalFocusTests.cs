using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A hidden original is disabled while nothing uses it, so Windows' focus handling passes over it (roadmap 12.3): it
/// was the first window Windows gave the focus to when the window in use closed or was minimized, which showed it and
/// turned Keep hidden off. It is enabled whenever something needs it, and by recovery.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class HiddenOriginalFocusTests
{
    private static readonly Rectangle Spot = new(160, 140, 420, 300);

    /// <summary>
    /// Measured 17 Nov on Windows 11: with the window in use closing (its program exiting), the hidden original got
    /// the focus every time; disabled, it is passed over. No view here: this is Windows' own choice.
    /// </summary>
    [Theory]
    [InlineData("close")]
    [InlineData("minimize")]
    public void Windows_passes_over_a_hidden_original_when_the_window_in_use_goes(string how)
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        using var behind = new TargetProcess(new Rectangle(520, 320, 420, 300), "1EB446", "Window behind");
        var front = new TargetProcess(new Rectangle(320, 240, 420, 300), "1E64C8", "Window in front");
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                Desktop.GainForegroundRights();
                var windows = new WindowController([]);
                try
                {
                    Desktop.Activate(behind.Handle);
                    Desktop.Activate(source.Handle);
                    windows.Hide(source.Handle);
                    Assert.True(User32.IsWindowEnabled(source.Handle), "Disabled while it still had the focus.");
                    Desktop.Activate(front.Handle);
                    windows.PassOverWhenUnused(source.Handle);
                    Assert.False(User32.IsWindowEnabled(source.Handle), "The hidden original wasn't disabled.");
                    TestDesktop.Pump(500);
                    const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060;
                    Desktop.PostMessage(front.Handle, WM_SYSCOMMAND, how == "close" ? SC_CLOSE : SC_MINIMIZE, 0);
                    TestDesktop.Pump(1500);
                    nint now = User32.GetForegroundWindow();
                    Assert.True(now != source.Handle, $"Windows gave the hidden original the focus when the window in use was {how}d.");

                    windows.Show(source.Handle);
                    Assert.True(User32.IsWindowEnabled(source.Handle), "Shown again, the original stayed disabled.");
                }
                finally { windows.RestoreAll(); }
            });
        }
        finally { front.Dispose(); }
    }

    /// <summary>
    /// Enabled while a visit, a drop or a showing needs it; disabled again a second after (a visit that ends and
    /// starts again finds it as it was); never left disabled.
    /// </summary>
    [Fact]
    public void A_hidden_original_is_enabled_exactly_while_something_uses_it()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        using var other = new TargetProcess(new Rectangle(620, 360, 300, 200), "1E64C8", "Other window");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Desktop.Activate(other.Handle);
                windows.Hide(source.Handle);
                windows.Park(source.Handle);
                Assert.False(User32.IsWindowEnabled(source.Handle));

                windows.SetParkedReachable(source.Handle, true); // A visit.
                Assert.True(User32.IsWindowEnabled(source.Handle), "A visit found the original disabled.");
                windows.SetParkedReachable(source.Handle, false);
                windows.FollowParkedPlace(source.Handle);
                Assert.True(User32.IsWindowEnabled(source.Handle), "Disabled the moment a visit ended.");
                TestDesktop.Pump(1100);
                windows.FollowParkedPlace(source.Handle);
                Assert.False(User32.IsWindowEnabled(source.Handle), "Still enabled a second after the visit.");

                windows.ShowParkedForDrop(source.Handle, new Point(200, 200)); // A drop.
                Assert.True(User32.IsWindowEnabled(source.Handle), "A drop found the original disabled.");
                windows.ReturnToParking(source.Handle);
                TestDesktop.Pump(1100);
                windows.FollowParkedPlace(source.Handle);
                Assert.False(User32.IsWindowEnabled(source.Handle));

                // The app enables it itself, as when a dialog of its own closes: disabled again on the next look.
                User32.EnableWindow(source.Handle, true);
                windows.FollowParkedPlace(source.Handle);
                Assert.False(User32.IsWindowEnabled(source.Handle), "Enabled by the app, it stayed enabled while hidden.");

                windows.Unpark(source.Handle);
                windows.Show(source.Handle);
                Assert.True(User32.IsWindowEnabled(source.Handle));
            }
            finally { windows.RestoreAll(); }
            Assert.True(User32.IsWindowEnabled(source.Handle), "Put back, it stayed disabled.");
        });
    }

    /// <summary>A window its app had disabled (a modal dialog up) is the app's: WinScreen neither records nor enables it.</summary>
    [Fact]
    public void A_window_its_app_disabled_is_left_to_the_app()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            try
            {
                User32.EnableWindow(source.Handle, false);
                windows.Hide(source.Handle);
                windows.Show(source.Handle);
                Assert.False(User32.IsWindowEnabled(source.Handle), "WinScreen enabled a window its app had disabled.");
            }
            finally
            {
                windows.RestoreAll();
                User32.EnableWindow(source.Handle, true);
            }
        });
    }

    /// <summary>Disabled is in the record a crash leaves, so the next start enables it.</summary>
    [Fact]
    public void A_hidden_original_left_disabled_by_a_crash_is_enabled_by_recovery()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        using var other = new TargetProcess(new Rectangle(620, 360, 300, 200), "1E64C8", "Other window");
        string path = Path.Combine(Path.GetTempPath(), $"winscreen-disabled-{Guid.NewGuid():N}.json");
        try
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(other.Handle);
            new WindowController([], new ChangeJournal(path)).Hide(source.Handle); // And never put back: a crash.
            Assert.False(User32.IsWindowEnabled(source.Handle));
            Assert.Contains("\"DisabledWhileHidden\":true", File.ReadAllText(path).Replace(" ", ""));
            new WindowController([], new ChangeJournal(path)).RecoverFromJournal();
            Assert.True(User32.IsWindowEnabled(source.Handle), "Recovery left the window disabled.");
            Assert.False(WindowInfo.IsConcealed(source.Handle));
        }
        finally
        {
            User32.EnableWindow(source.Handle, true);
            File.Delete(path);
        }
    }
}
