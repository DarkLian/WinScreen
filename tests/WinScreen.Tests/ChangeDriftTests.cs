using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The other rows of F10's table (11.2): Pin, opacity and click-through, and Crop, when another program, the app or the
/// person undoes them. WinScreen's record follows the window, and the person is told what was let go of.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class ChangeDriftTests
{
    private static readonly Rectangle Spot = new(260, 180, 520, 380);
    private static readonly Point Away = new(5, 5);

    private static void Run(bool frame, Action<TargetProcess, WindowController, List<string>> test)
    {
        using var target = new TargetProcess(Spot, "F0F0F0");
        if (frame) target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            var told = new List<string>();
            windows.ChangeUndone += (_, text) => told.Add(text);
            try { test(target, windows, told); }
            finally { windows.RestoreAll(); }
        });
    }

    private static void Tick(WindowController windows, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            windows.Refresh(Away, false, false);
            Application.DoEvents();
        }
    }

    private static bool Topmost(nint window) => ((long)User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;

    private static void TakeTopmost(nint window) =>
        User32.SetWindowPos(window, User32.HWND_NOTOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

    [Fact]
    public void A_pin_another_program_takes_away_is_put_back()
    {
        Run(true, (target, windows, told) =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(windows.TogglePin(target.Handle));
            TakeTopmost(target.Handle);
            Assert.False(Topmost(target.Handle));
            Tick(windows);
            Assert.True(Topmost(target.Handle), "The pin wasn't put back.");
            Assert.True(windows.IsPinned(target.Handle));
            Assert.Empty(told);
        });
    }

    [Fact]
    public void A_pin_taken_away_by_the_program_in_front_is_put_back_without_taking_the_focus()
    {
        using var front = new TargetProcess(new Rectangle(900, 300, 360, 260), "1E64C8", title: "In front");
        Run(true, (target, windows, told) =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(windows.TogglePin(target.Handle));
            Desktop.Activate(front.Handle);
            TakeTopmost(target.Handle);
            Tick(windows);
            Assert.True(Topmost(target.Handle), "The pin wasn't put back while another window was in front.");
            Assert.Equal(front.Handle, User32.GetForegroundWindow());
            Assert.True(windows.IsPinned(target.Handle));
            Assert.Empty(told);
        });
    }

    [Fact]
    public void A_program_that_keeps_taking_the_pin_wins_and_the_person_is_told()
    {
        Run(true, (target, windows, told) =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            Assert.True(windows.TogglePin(target.Handle));
            for (int taking = 0; taking < 6 && windows.IsPinned(target.Handle); taking++)
            {
                TakeTopmost(target.Handle);
                Tick(windows);
            }
            Assert.False(windows.IsPinned(target.Handle), "WinScreen kept fighting for the pin.");
            Assert.Single(told);
            Assert.False(Topmost(target.Handle));
        });
    }

    [Fact]
    public void Opacity_the_app_rewrites_is_let_go_of_and_put_back_leaves_the_apps_own()
    {
        Run(true, (target, windows, told) =>
        {
            windows.SetOpacity(target.Handle, 128);
            Tick(windows, 3);
            Assert.Empty(told); // WinScreen's own level, checked again and again, is no drift.
            Assert.Equal(128, windows.OpacityOf(target.Handle));

            Assert.True(User32.SetLayeredWindowAttributes(target.Handle, 0, 200, User32.LWA_ALPHA));
            Tick(windows);
            Assert.Empty(told); // Seen once could be a moment in passing.
            Tick(windows);
            Assert.Single(told);
            Assert.Equal(255, windows.OpacityOf(target.Handle));

            windows.RestoreAll();
            Assert.True(User32.GetLayeredWindowAttributes(target.Handle, out _, out byte alpha, out _));
            Assert.Equal(200, alpha);
        });
    }

    [Fact]
    public void Click_through_the_app_takes_off_is_no_longer_shown_as_on()
    {
        Run(true, (target, windows, told) =>
        {
            Assert.True(windows.ToggleClickThrough(target.Handle));
            long style = (long)User32.GetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE);
            User32.SetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE, (nint)(style & ~User32.WS_EX_TRANSPARENT));
            Tick(windows, 2);
            Assert.False(windows.IsClickThrough(target.Handle));
            Assert.Single(told);
        });
    }

    [Fact]
    public void A_crop_keeps_its_rectangle_through_a_move_and_a_resize()
    {
        Run(false, (target, windows, told) =>
        {
            windows.Crop(target.Handle, new Rectangle(20, 20, 200, 150));
            User32.SetWindowPos(target.Handle, 0, Spot.X + 40, Spot.Y + 30, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Tick(windows);
            Assert.True(windows.IsCropped(target.Handle));
            User32.SetWindowPos(target.Handle, 0, 0, 0, Spot.Width + 120, Spot.Height + 80, User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Tick(windows);
            Assert.True(windows.IsCropped(target.Handle));
            Assert.Equal(new Rectangle(20, 20, 200, 150), WindowInfo.ShapeOf(target.Handle));
            Assert.Empty(told);
        });
    }

    [Fact]
    public void A_crop_the_app_takes_off_itself_is_no_longer_shown_as_on()
    {
        Run(false, (target, windows, told) =>
        {
            windows.Crop(target.Handle, new Rectangle(20, 20, 200, 150));
            User32.SetWindowRgn(target.Handle, 0, true);
            Tick(windows);
            Assert.False(windows.IsCropped(target.Handle));
            Assert.Empty(told);
        });
    }
}
