using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Checks the pixels DWM really puts on screen for a PiP window.</summary>
[Trait("Category", "RealWindows")]
public class PipWindowTests
{
    private static readonly Rectangle SourceSpot = new(240, 160, 400, 300);
    private static readonly Color Red = Color.FromArgb(0xC8, 0x1E, 0x1E);
    private static readonly Color Blue = Color.FromArgb(0x1E, 0x64, 0xC8);
    private static readonly Color Green = Color.FromArgb(0x1E, 0xB4, 0x46);

    /// <summary>Runs a test on a window-owning thread, with a helper that keeps the window responsive.</summary>
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
                    while (DateTime.UtcNow < end)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                });
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Rectangle Middle(Form window)
    {
        Rectangle b = window.Bounds;
        return new Rectangle(b.Left + b.Width / 2 - 20, b.Top + b.Height / 2 - 20, 40, 40);
    }

    private static PipWindow OpenAwayFromToasts(nint source)
    {
        Desktop.GainForegroundRights();
        var pip = new PipWindow(source, new WindowController([]));
        pip.ShowOnTop();
        pip.Location = new Point(700, 120);
        return pip;
    }

    private static bool Shows(Form window, Color color, Action<int> pump)
    {
        return Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle(window)), color), 3000, () => pump(20));
    }

    [Fact]
    public void Shows_the_source_window_on_screen()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            Assert.True(Shows(pip, Red, pump), $"PiP showed {Desktop.ColorOnScreen(Middle(pip))}.");
        });
    }

    [Fact]
    public void Auto_move_leaves_text_and_images_for_a_plain_spot()
    {
        Rectangle area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        using var calm = new TargetProcess(area, "808080");
        var busyPart = new Rectangle(area.Left + 40, area.Top + 40, 720, 460);
        using var busy = new TargetProcess(busyPart, string.Join(",", Enumerable.Repeat("000000,FFFFFF", 45)));
        using var source = new TargetProcess(new Rectangle(area.Right - 500, area.Bottom - 400, 320, 200), "C81E1E");
        Desktop.GainForegroundRights();
        Desktop.Activate(calm.Handle);
        Desktop.Activate(busy.Handle);
        Desktop.Activate(source.Handle);
        Point mouse = Desktop.CursorPosition();
        Desktop.MoveMouse(new Point(area.Right - 4, area.Bottom - 4));
        try
        {
            OnUiThread(pump =>
            {
                using var pip = new PipWindow(source.Handle, new WindowController([]));
                pip.ShowOnTop();
                pip.Location = new Point(busyPart.Left + 60, busyPart.Top + 60);
                pump(200);

                pip.SetAutoMove(true);
                bool moved = Desktop.WaitFor(() => !pip.Bounds.IntersectsWith(busyPart), 5000, () => pump(50));

                Assert.True(moved, $"The view stayed at {pip.Bounds} over {busyPart}.");
                Point settled = pip.Location;
                pump(2500);
                Assert.Equal(settled, pip.Location);
            });
        }
        finally
        {
            Desktop.MoveMouse(mouse);
        }
    }

    [Fact]
    public void Auto_move_stays_put_while_the_mouse_is_on_the_view()
    {
        Rectangle area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        using var calm = new TargetProcess(area, "808080");
        var busyPart = new Rectangle(area.Left + 40, area.Top + 40, 720, 460);
        using var busy = new TargetProcess(busyPart, string.Join(",", Enumerable.Repeat("000000,FFFFFF", 45)));
        using var source = new TargetProcess(new Rectangle(area.Right - 500, area.Bottom - 400, 320, 200), "C81E1E");
        Desktop.GainForegroundRights();
        Desktop.Activate(calm.Handle);
        Desktop.Activate(busy.Handle);
        Desktop.Activate(source.Handle);
        Point mouse = Desktop.CursorPosition();
        try
        {
            OnUiThread(pump =>
            {
                using var pip = new PipWindow(source.Handle, new WindowController([]));
                pip.ShowOnTop();
                var spot = new Point(busyPart.Left + 60, busyPart.Top + 60);
                pip.Location = spot;
                Desktop.MoveMouse(new Point(spot.X + 40, spot.Y + 40));
                pump(200);

                pip.SetAutoMove(true);
                pump(2500);

                Assert.Equal(spot, pip.Location);
            });
        }
        finally
        {
            Desktop.MoveMouse(mouse);
        }
    }

    [Fact]
    public void Keeps_the_source_shape()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(300);
            Assert.Equal(PipGeometry.FitHeight(pip.Width, new Rectangle(0, 0, 400, 300)), pip.Height);
        });
    }

    [Fact]
    public void Updates_live_when_the_source_changes()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            Assert.True(Shows(pip, Red, pump));
            source.SetColors("1EB446");
            Assert.True(Shows(pip, Green, pump), $"PiP showed {Desktop.ColorOnScreen(Middle(pip))}.");
        });
    }

    [Fact]
    public void Keeps_showing_a_source_covered_by_another_window()
    {
        using var source = new TargetProcess(SourceSpot);
        using var cover = new TargetProcess(SourceSpot, "1E64C8");
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            Assert.True(Shows(pip, Red, pump), "PiP didn't start with the source.");
            Desktop.Activate(cover.Handle);
            Assert.Equal(cover.Handle, Desktop.TopWindowAt(cover.Center));
            Assert.NotEqual(0, Desktop.ExStyle(pip.Handle) & User32.WS_EX_TOPMOST);
            Assert.Equal(pip.Handle, Desktop.TopWindowAt(Middle(pip).Location));
            Assert.True(Shows(pip, Red, pump), $"PiP showed {Desktop.ColorOnScreen(Middle(pip))}.");
        });
    }

    [Fact]
    public void Crop_shows_only_the_chosen_part()
    {
        using var source = new TargetProcess(SourceSpot, "C81E1E,1E64C8");
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(200);
            pip.SetCrop(new Rectangle(220, 20, 160, 120));
            Assert.True(Shows(pip, Blue, pump), $"Cropped PiP showed {Desktop.ColorOnScreen(Middle(pip))}.");

            pip.SetCrop(new Rectangle(20, 20, 160, 120));
            Assert.True(Shows(pip, Red, pump), $"Cropped PiP showed {Desktop.ColorOnScreen(Middle(pip))}.");
        });
    }

    [Fact]
    public void Hiding_the_original_keeps_the_view_live_and_brings_it_back_afterwards()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            Assert.True(Shows(pip, Red, pump));

            pip.SetOriginalHidden(true);
            Assert.Equal((byte?)1, Desktop.Alpha(source.Handle));
            Assert.NotEqual(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT);
            Assert.True(Shows(pip, Red, pump), $"PiP went blank when the original was hidden: {Desktop.ColorOnScreen(Middle(pip))}.");
            source.SetColors("1EB446");
            Assert.True(Shows(pip, Green, pump), $"PiP stopped updating: {Desktop.ColorOnScreen(Middle(pip))}.");

            pip.SetOriginalHidden(false);
            Assert.Equal((byte?)255, Desktop.Alpha(source.Handle));
            Assert.Equal(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT);
        });
    }

    [Fact]
    public void Closing_the_view_brings_a_hidden_original_back()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(200);
            pip.SetOriginalHidden(true);
            Assert.Equal((byte?)1, Desktop.Alpha(source.Handle));
            pip.Close();
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(source.Handle) == 255, 2000, () => pump(20)));
        });
    }

    [Fact]
    public void Closes_when_the_source_window_closes()
    {
        var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ShowOnTop();
            pump(200);
            source.Dispose();
            Assert.True(Desktop.WaitFor(() => pip.IsDisposed, 3000, () => pump(20)));
        });
    }
}
