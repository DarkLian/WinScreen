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
        return Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle(window)), color), 3000,
            () => pump(20));
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
    public void A_location_chosen_before_the_deferred_Shown_event_is_preserved()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            bool shown = false;
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.Shown += (_, _) => shown = true;
            Desktop.GainForegroundRights();
            pip.ShowOnTop();
            bool shownBeforeMove = shown;
            var desired = new Point(100, 100);
            pip.Location = desired;
            pump(200);
            Assert.True(pip.Location == desired,
                $"Shown before explicit move: {shownBeforeMove}; after pump: {shown}; position: {pip.Location}; expected: {desired}");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_click_on_a_view_never_reaches_or_raises_the_original(bool clickThrough)
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            if (clickThrough) windows.ToggleClickThrough(source.Handle);
            using var pip = new PipWindow(source.Handle, windows);
            Desktop.GainForegroundRights();
            pip.ShowOnTop();
            pump(150);
            Point pointerBefore = Desktop.CursorPosition();
            uint inputBefore = Desktop.LastInputTick();
            int packed = (50 << 16) | 50;
            User32.PostMessage(pip.Handle, 0x0201, 1, (nint)packed);
            User32.PostMessage(pip.Handle, 0x0202, 0, (nint)packed);
            pump(150);
            Assert.Null(source.NextReport(100));
            Assert.Equal(SourceSpot, Desktop.BoundsOf(source.Handle));
            Assert.NotEqual(source.Handle, User32.GetForegroundWindow());
            // A physical mouse move during a desktop test is not evidence that PiP moved the cursor.
            if (inputBefore == Desktop.LastInputTick())
                Assert.Equal(pointerBefore, Desktop.CursorPosition());
            windows.RestoreAll();
        });
    }

    [Fact]
    [Trait("Category", "Interactive")]
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
    [Trait("Category", "Interactive")]
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
            Assert.Equal((byte?)0, Desktop.Alpha(source.Handle));
            Assert.NotEqual(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TRANSPARENT);
            Assert.True(Shows(pip, Red, pump),
                $"PiP went blank when the original was hidden: {Desktop.ColorOnScreen(Middle(pip))}.");
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
            Assert.Equal((byte?)0, Desktop.Alpha(source.Handle));
            pip.Close();
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(source.Handle) == 255, 2000, () => pump(20)));
        });
    }

    /// <summary>A real framed window, unlike the borderless test target, with a colour along each far edge.</summary>
    private sealed class EdgeStripes : Form
    {
        public const int Stripe = 24;

        public EdgeStripes()
        {
            ResizeRedraw = true;
            DoubleBuffered = true;
            BackColor = Green;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "WinScreen edge test";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int width = ClientSize.Width, height = ClientSize.Height;
            using var left = new SolidBrush(Red);
            using var right = new SolidBrush(Blue);
            using var bottom = new SolidBrush(Color.Yellow);
            e.Graphics.FillRectangle(left, 0, 0, Stripe, height);
            e.Graphics.FillRectangle(right, width - Stripe, 0, Stripe, height);
            e.Graphics.FillRectangle(bottom, Stripe, height - Stripe, width - 2 * Stripe, Stripe);
        }
    }

    private static bool Near(Point spot, Color color, Action<int> pump)
    {
        return Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(new Rectangle(spot, new Size(3, 3))), color),
            3000, () => pump(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shows_all_four_edges_of_a_framed_window(bool maximized)
    {
        OnUiThread(pump =>
        {
            using var source = new EdgeStripes();
            source.Show();
            pump(200);
            User32.ShowWindow(source.Handle, maximized ? 3 : User32.SW_RESTORE);
            pump(300);
            if (!maximized)
                source.Bounds = new Rectangle(120, 90, 900, 600);
            pump(300);

            Desktop.GainForegroundRights();
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ShowOnTop();
            pump(300);

            Rectangle b = pip.Bounds;
            const int inset = PipGeometry.Border + 2;
            int middle = b.Top + b.Height / 2;
            Assert.True(Near(new Point(b.Left + inset, middle), Red, pump),
                $"Left edge: {Desktop.ColorOnScreen(new Rectangle(b.Left + inset, middle, 3, 3))}");
            Assert.True(Near(new Point(b.Right - inset - 3, middle), Blue, pump),
                $"Right edge: {Desktop.ColorOnScreen(new Rectangle(b.Right - inset - 3, middle, 3, 3))}");
            Assert.True(Near(new Point(b.Left + b.Width / 2, b.Bottom - inset - 3), Color.Yellow, pump),
                $"Bottom edge: {Desktop.ColorOnScreen(new Rectangle(b.Left + b.Width / 2, b.Bottom - inset - 3, 3, 3))}");
        });
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public void The_title_bar_close_button_is_really_there_to_click()
    {
        using var source = new TargetProcess(SourceSpot);
        Point mouse = Desktop.CursorPosition();
        try
        {
            OnUiThread(pump =>
            {
                using PipWindow pip = OpenAwayFromToasts(source.Handle);
                pump(150);
                Desktop.MoveMouse(new Point(pip.Left + 50, pip.Top + 50));
                Assert.True(Desktop.WaitFor(() => pip.TitleBarBounds is not null, 2000, () => pump(20)));
                Rectangle bar = pip.TitleBarBounds!.Value;
                var corner = new Point(bar.Right - 16, bar.Top + bar.Height / 2);
                Desktop.MoveMouse(corner);

                // Only what the pointer would hit counts; a window can be visible behind the view.
                bool onTop = Desktop.WaitFor(() =>
                {
                    nint root = User32.GetAncestor(Desktop.TopWindowAt(corner), User32.GA_ROOT);
                    return root != pip.Handle && (Desktop.ExStyle(root) & User32.WS_EX_NOACTIVATE) != 0;
                }, 2000, () => pump(20));
                Assert.True(onTop, "The title bar close button is behind the view.");
            });
        }
        finally
        {
            Desktop.MoveMouse(mouse);
        }
    }

    [Fact]
    public void Opens_ready_on_top_without_taking_the_focus()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(source.Handle);
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ShowOnTop();

            // Nothing has been pumped yet, so this is what the very first frame shows.
            Assert.Equal(PipGeometry.FitHeight(pip.Width, new Rectangle(0, 0, 400, 300)), pip.Height);
            Assert.NotEqual(0, Desktop.ExStyle(pip.Handle) & User32.WS_EX_TOPMOST);
            Assert.NotEqual(0, Desktop.ExStyle(pip.Handle) & User32.WS_EX_TOOLWINDOW);
            Assert.NotEqual(0, Desktop.ExStyle(pip.Handle) & User32.WS_EX_NOACTIVATE);
            Assert.Equal(source.Handle, User32.GetForegroundWindow());
            Assert.True(Shows(pip, Red, pump));
            Assert.Equal(source.Handle, User32.GetForegroundWindow());
        });
    }

    [Fact]
    public void Tall_windows_open_a_sensible_size()
    {
        using var source = new TargetProcess(new Rectangle(240, 60, 300, 800));
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(100);
            Assert.InRange(pip.Height, 470, 490);
            Assert.InRange(pip.Width, 170, 190);
        });
    }

    [Fact]
    public void Scrolling_while_the_source_is_minimized_keeps_the_view_whole()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(200);
            double shape = pip.Height / (double)pip.Width;

            User32.ShowWindow(source.Handle, 6);
            pump(500);
            Wheel(pip, 120);
            pump(200);
            Assert.InRange(pip.Height / (double)pip.Width, shape - 0.02, shape + 0.02);

            User32.ShowWindow(source.Handle, User32.SW_RESTORE);
            pump(500);
            Assert.InRange(pip.Height / (double)pip.Width, shape - 0.02, shape + 0.02);
            Assert.True(Shows(pip, Red, pump));
        });
    }

    [Fact]
    public void A_crop_survives_the_source_shrinking_and_growing_back()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(200);
            var crop = new Rectangle(100, 100, 250, 150);
            pip.SetCrop(crop);
            pump(150);

            Resize(source.Handle, 200, 200);
            pump(300);
            Assert.False(pip.IsDisposed);
            Resize(source.Handle, SourceSpot.Width, SourceSpot.Height);
            pump(300);

            Assert.Equal(crop, pip.Crop);
            Assert.Equal(PipGeometry.FitLongSide(480, new Rectangle(0, 0, 250, 150), new Size(1920, 1040)), pip.Size);
        });
    }

    [Fact]
    public void A_crop_stays_on_the_same_part_when_the_source_is_maximized_and_restored()
    {
        OnUiThread(pump =>
        {
            using var source = new EdgeStripes();
            source.Show();
            pump(200);
            User32.ShowWindow(source.Handle, User32.SW_RESTORE);
            pump(300);
            source.Bounds = new Rectangle(120, 90, 900, 600);
            pump(300);

            Desktop.GainForegroundRights();
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ShowOnTop();
            pump(200);
            var crop = new Rectangle(0, 200, 16, 16);
            pip.SetCrop(crop);
            pump(200);
            Assert.True(Shows(pip, Red, pump), $"Cropped view: {Desktop.ColorOnScreen(Middle(pip))}.");

            User32.ShowWindow(source.Handle, 3);
            pump(600);
            Assert.Equal(crop, pip.Crop);
            Assert.True(Shows(pip, Red, pump), $"Maximized: {Desktop.ColorOnScreen(Middle(pip))}.");

            User32.ShowWindow(source.Handle, User32.SW_RESTORE);
            pump(600);
            Assert.Equal(crop, pip.Crop);
            Assert.True(Shows(pip, Red, pump), $"Restored: {Desktop.ColorOnScreen(Middle(pip))}.");
        });
    }

    [Fact]
    public void Live_resizing_the_source_does_not_shrink_the_view()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            using PipWindow pip = OpenAwayFromToasts(source.Handle);
            pump(200);
            for (int i = 0; i < 12; i++)
            {
                Resize(source.Handle, 500 + 7 * i, 300 + 3 * i);
                pump(90);
                Assert.Equal(480, pip.Width);
            }
        });
    }

    [Fact]
    public void Two_views_never_open_on_top_of_each_other()
    {
        using var first = new TargetProcess(SourceSpot);
        using var second = new TargetProcess(new Rectangle(700, 160, 400, 300), "1E64C8");
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            using var one = new PipWindow(first.Handle, windows);
            using var two = new PipWindow(second.Handle, windows);
            one.ShowOnTop();
            two.ShowOnTop();
            pump(100);
            Assert.False(one.Bounds.IntersectsWith(two.Bounds), $"{one.Bounds} and {two.Bounds}");
        });
    }

    [Fact]
    public void A_hidden_original_stays_on_top_without_counting_as_pinned()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows);
            Desktop.GainForegroundRights();
            pip.ShowOnTop();
            pump(150);

            pip.SetOriginalHidden(true);
            Assert.NotEqual(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TOPMOST);
            Assert.False(windows.IsPinned(source.Handle));
            Assert.Empty(windows.VisiblePinnedWindows());

            pip.SetOriginalHidden(false);
            Assert.Equal(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TOPMOST);
            windows.RestoreAll();
        });
    }

    [Fact]
    public void A_hidden_original_that_the_person_pinned_stays_pinned()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            Desktop.GainForegroundRights();
            windows.TogglePin(source.Handle);
            using var pip = new PipWindow(source.Handle, windows);
            pip.ShowOnTop();
            pump(150);

            pip.SetOriginalHidden(true);
            pip.SetOriginalHidden(false);

            Assert.NotEqual(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TOPMOST);
            Assert.True(windows.IsPinned(source.Handle));
            windows.RestoreAll();
        });
    }

    [Fact]
    public void Restore_all_takes_a_hidden_original_off_the_top()
    {
        using var source = new TargetProcess(SourceSpot);
        OnUiThread(pump =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows);
            Desktop.GainForegroundRights();
            pip.ShowOnTop();
            pump(150);
            pip.SetOriginalHidden(true);

            windows.RestoreAll();

            Assert.Equal(0, Desktop.ExStyle(source.Handle) & User32.WS_EX_TOPMOST);
            Assert.Equal((byte?)255, Desktop.Alpha(source.Handle) ?? 255);
        });
    }

    private static void Resize(nint window, int width, int height)
    {
        User32.SetWindowPos(window, 0, 0, 0, width, height,
            User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    private static void Wheel(PipWindow pip, int delta)
    {
        Point spot = pip.PointToScreen(new Point(30, 30));
        User32.PostMessage(pip.Handle, 0x020A, unchecked((nint)(delta << 16)), (spot.Y << 16) | spot.X);
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
