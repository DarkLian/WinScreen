using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A hidden original stays enabled. 1.1.2-beta.3 disabled it while nothing used it, so Windows' focus handling would
/// pass over it (roadmap 12.3); the owner found that a disabled Chrome stops drawing its video, so the view of it
/// froze. The focus Windows hands a hidden original is passed on by the view instead (ViewState.OriginalArrived).
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class HiddenOriginalFocusTests
{
    private static readonly Rectangle Spot = new(160, 140, 420, 300);

    [Fact]
    public void A_hidden_original_is_never_disabled()
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
                TestDesktop.Pump(1500);
                windows.FollowParkedPlace(source.Handle);
                Assert.True(User32.IsWindowEnabled(source.Handle), "Hidden and unused, the original was disabled.");

                windows.SetParkedReachable(source.Handle, true);
                windows.SetParkedReachable(source.Handle, false);
                TestDesktop.Pump(1100);
                windows.FollowParkedPlace(source.Handle);
                Assert.True(User32.IsWindowEnabled(source.Handle), "After a visit, the original was disabled.");
            }
            finally { windows.RestoreAll(); }
        });
    }

    /// <summary>A record 1.1.2-beta.3 left after a crash, with the window disabled: the next start enables it.</summary>
    [Fact]
    public void A_window_beta_3_left_disabled_is_enabled_by_recovery()
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        using var other = new TargetProcess(new Rectangle(620, 360, 300, 200), "1E64C8", "Other window");
        string path = Path.Combine(Path.GetTempPath(), $"winscreen-disabled-{Guid.NewGuid():N}.json");
        try
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(other.Handle);
            new WindowController([], new ChangeJournal(path)).Hide(source.Handle); // And never put back: a crash.
            // As beta 3 left it: the window disabled and the record saying so.
            User32.EnableWindow(source.Handle, false);
            string record = File.ReadAllText(path);
            var flag = new System.Text.RegularExpressions.Regex("\"DisabledWhileHidden\":\\s*false");
            Assert.Matches(flag, record);
            File.WriteAllText(path, flag.Replace(record, "\"DisabledWhileHidden\":true"));

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

    /// <summary>A window its app disabled (a dialog of its own up) is the app's: WinScreen leaves it disabled.</summary>
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
}
