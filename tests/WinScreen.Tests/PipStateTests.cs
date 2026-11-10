using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipStateChipTests
{
    [Theory]
    [InlineData(true, (int)PipSessionState.Compositor, false, false, (int)PipChipState.SourceMinimized)]
    [InlineData(true, (int)PipSessionState.Live, false, false, (int)PipChipState.SourceMinimized)]
    [InlineData(true, (int)PipSessionState.SourcePaused, false, true, (int)PipChipState.None)]
    [InlineData(false, (int)PipSessionState.SourcePaused, false, false, (int)PipChipState.None)]
    [InlineData(false, (int)PipSessionState.SourcePaused, true, false, (int)PipChipState.NoNewPicture)]
    [InlineData(false, (int)PipSessionState.Live, true, false, (int)PipChipState.None)]
    [InlineData(false, (int)PipSessionState.Compositor, true, false, (int)PipChipState.None)]
    public void The_chip_only_says_what_is_known(bool minimized, int state, bool pointer, bool busy,
        int expected) => Assert.Equal((PipChipState)expected, PipStateChip.Decide(minimized, (PipSessionState)state, pointer, busy));
}

[Trait("Category", "RealWindows")]
public sealed class PipOpenTests
{
    [Fact]
    public void Interactive_options_are_visible_on_first_and_repeated_menu_openings()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            try
            {
                pip.ShowOnTop();
                pip.SetInteractive(true);
                var menu = pip.ContextMenuStrip!;
                ToolStripMenuItem Item(string key) => menu.Items.OfType<ToolStripMenuItem>()
                    .Single(item => item.Name != "inputStatus" && item.Text == WinScreen.Localization.UiText.T(key));
                for (int visit = 0; visit < 3; visit++)
                {
                    menu.Show(pip, new Point(20, 20));
                    pump(30);
                    Assert.True(menu.Visible);
                    Assert.True(Item("menu.view.interactive").Checked);
                    Assert.True(Item("menu.view.middleScroll").Visible, "Middle-click scrolling must appear in the open menu.");
                    Assert.True(Item("menu.view.hover").Visible, "Enhanced hover must appear in the open menu.");
                    menu.Close();
                }
                pip.SetInteractive(false);
                menu.Show(pip, new Point(20, 20));
                pump(30);
                Assert.True(Item("menu.view.interactive").Visible);
                Assert.False(Item("menu.view.middleScroll").Visible);
                Assert.False(Item("menu.view.hover").Visible);
                menu.Close();
            }
            finally { pip.Close(); }
        });
    }

    private static void OnUiThread(Action<Action<int>> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test(milliseconds =>
                {
                    DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
                    while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
                });
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void A_compatibility_view_never_claims_to_be_live()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            pip.ShowOnTop();
            pump(200);
            Assert.Equal(PipSessionState.Compositor, pip.BackendState);
            User32.ShowWindow(source.Handle, 6);
            pump(300);
            Assert.Equal(PipSessionState.Compositor, pip.BackendState);
            pip.Close();
        });
    }

    [Fact]
    public void Opening_on_a_minimized_source_restores_it_and_says_so()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            User32.ShowWindow(source.Handle, 6);
            Assert.True(Desktop.WaitFor(() => User32.IsIconic(source.Handle), 2000));
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            var notices = new List<string>();
            pip.Notice += notices.Add;
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            pip.ShowOnTop();
            pump(200);
            Assert.False(User32.IsIconic(source.Handle));
            Assert.Single(notices);
            pip.Close();
        });
    }

    [Fact]
    public void Keep_updating_brings_a_minimized_source_back_hidden_without_taking_the_focus()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows);
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            pip.ShowOnTop();
            pump(150);
            User32.ShowWindow(source.Handle, 6);
            pump(400);
            nint foreground = User32.GetForegroundWindow();
            pip.KeepUpdating();
            pump(300);
            Assert.False(User32.IsIconic(source.Handle));
            Assert.True(windows.IsHidden(source.Handle));
            Assert.True(WindowInfo.IsConcealed(source.Handle));
            Assert.NotEqual(source.Handle, User32.GetForegroundWindow());
            Assert.Equal(foreground, User32.GetForegroundWindow());
            pip.Close();
            pump(150);
            Assert.False(windows.IsHidden(source.Handle));
        });
    }

    [Fact]
    public void A_source_minimized_during_a_view_stays_minimized()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            var notices = new List<string>();
            pip.Notice += notices.Add;
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            pip.ShowOnTop();
            pump(150);
            User32.ShowWindow(source.Handle, 6);
            pump(600);
            Assert.True(User32.IsIconic(source.Handle));
            Assert.Empty(notices);
            pip.Close();
        });
    }
}
