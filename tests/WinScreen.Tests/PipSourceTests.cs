using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipSourceLogicTests
{
    [Fact]
    public void A_region_is_clamped_to_its_display()
    {
        var source = new ScreenPipSource("x", new Rectangle(1800, 900, 400, 400));
        Assert.Equal(new Rectangle(1800, 900, 120, 180), source.Clamped(new Size(1920, 1080)));
        Assert.True(source.Clamped(new Size(1280, 720)).IsEmpty);
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), new ScreenPipSource("x", null).Clamped(new Size(1920, 1080)));
    }

    [Fact]
    public void A_display_that_has_gone_is_unavailable()
    {
        var source = new ScreenPipSource(@"\\.\DISPLAY_GONE", null);
        Assert.False(source.IsAvailable);
        Assert.Throws<WindowOperationException>(source.Require);
    }

    [Fact]
    public void Every_display_is_offered_and_available()
    {
        Assert.Equal(Screen.AllScreens.Length, PipSourceList.Screens().Count);
        Assert.All(PipSourceList.Screens(), choice => Assert.True(new ScreenPipSource(choice.Device!, null).IsAvailable));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("report", true)]
    [InlineData("WORD", true)]
    [InlineData("word report", true)]
    [InlineData("excel", false)]
    public void Search_matches_every_word_in_title_or_program(string search, bool expected)
    {
        var choice = new PipChoice("Quarterly Report", "WINWORD", 1, null, false);
        Assert.Equal(expected, PipSourceList.Matches(choice, search));
    }
}

[Trait("Category", "RealWindows")]
public sealed class PipScreenViewTests
{
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

    private static string MainDisplay => Screen.PrimaryScreen!.DeviceName;

    private static Rectangle Middle(Form view) =>
        new(view.Left + view.Width / 2 - 10, view.Top + view.Height / 2 - 10, 20, 20);

    [Fact]
    public void The_picker_offers_real_windows_but_not_refused_ones()
    {
        using var target = new TargetProcess(new(200, 160, 400, 300), title: "Picker target");
        var windows = new WindowController([]);
        Assert.Contains(PipSourceList.Windows(windows.CanControl), choice => choice.Window == target.Handle);
        windows.SetExcluded(["WinScreen.TestTarget"]);
        Assert.DoesNotContain(PipSourceList.Windows(windows.CanControl), choice => choice.Window == target.Handle);
    }

    [WgcFact]
    public void A_region_view_shows_that_part_of_the_screen()
    {
        using var target = new TargetProcess(new(200, 160, 400, 300), "1EB446");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var pip = new PipWindow(new ScreenPipSource(MainDisplay, new Rectangle(250, 200, 200, 150)), windows);
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            pip.ShowOnTop();
            pip.Location = new Point(800, 120);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind == PipBackendKind.WgcHardware, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            // Screen views are hidden from screen reads, so the picture can only be checked with that lifted.
            User32.SetWindowDisplayAffinity(pip.Handle, User32.WDA_NONE);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle(pip)), Color.FromArgb(0x1E, 0xB4, 0x46)), 4000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.InRange((pip.Width - 4) / (double)(pip.Height - 4), 1.3, 1.37);
            pip.Close();
            pump(150);
        });
    }

    [WgcFact]
    public void A_screen_view_stays_out_of_screen_capture()
    {
        using var backdrop = new TargetProcess(new(100, 100, 900, 600), "1E64C8");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(backdrop.Handle);
            var windows = new WindowController([]);
            // The region is the area the view itself sits in, the case that would otherwise be a mirror tunnel.
            // What a screen read finds at the view's spot is the window underneath, not the view.
            using var pip = new PipWindow(new ScreenPipSource(MainDisplay, new Rectangle(300, 200, 400, 300)), windows);
            pip.ShowOnTop();
            pip.Location = new Point(320, 220);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind == PipBackendKind.WgcHardware, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle(pip)), Color.FromArgb(0x1E, 0x64, 0xC8)), 4000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.True(User32.GetWindowDisplayAffinity(pip.Handle, out uint affinity));
            Assert.Equal(User32.WDA_EXCLUDEFROMCAPTURE, affinity);
            pip.AllowAutoMove = true;
            pip.SetAutoMove(true);
            pip.SetAutoMove(false);
            Assert.True(User32.GetWindowDisplayAffinity(pip.Handle, out affinity));
            Assert.Equal(User32.WDA_EXCLUDEFROMCAPTURE, affinity);
            pip.Close();
            pump(150);
        });
    }

    [Fact]
    public void A_screen_view_has_nothing_to_fall_back_on()
    {
        OnUiThread(_ =>
        {
            using var destination = new Form { ShowInTaskbar = false, Size = new Size(300, 200) };
            destination.Show();
            using var selector = new PipBackendSelector(new ScreenPipSource(MainDisplay, null), destination.Handle,
                new PipPreferences { Backend = PipBackendPreference.Dwm }, _ => throw new NotSupportedException("no capture"));
            Assert.Throws<WindowOperationException>(() =>
            {
                selector.Poll(destination.ClientSize);
                selector.Poll(destination.ClientSize);
            });
        });
    }

    [Fact]
    public void A_view_of_a_missing_display_refuses_to_open()
    {
        OnUiThread(_ =>
        {
            using var pip = new PipWindow(new ScreenPipSource(@"\\.\DISPLAY_GONE", null), new WindowController([]));
            Assert.Throws<WindowOperationException>(pip.ShowOnTop);
        });
    }
}
