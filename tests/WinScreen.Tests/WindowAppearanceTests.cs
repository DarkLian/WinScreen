using System.Reflection;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class WindowAppearanceTests
{
    private static readonly Rectangle Spot = new(260, 200, 500, 350);
    private static string JournalPath() => Path.Combine(Directory.CreateTempSubdirectory("winscreen-style").FullName, "recovery.json");
    private static long Style(nint window) => User32.GetWindowLongPtr(window, User32.GWL_STYLE);
    private static Rectangle Bounds(nint window)
    {
        Assert.True(User32.GetWindowRect(window, out Rect r));
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }
    private static Size Client(nint window)
    {
        Assert.True(User32.GetClientRect(window, out Rect r));
        return new Size(r.Right, r.Bottom);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Borderless_preserves_client_size_or_fills_and_restores_exactly(bool fill)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        var windows = new WindowController([], new ChangeJournal(JournalPath()));
        Rectangle before = Bounds(target.Handle);
        Size client = Client(target.Handle);
        long style = Style(target.Handle);
        try
        {
            Assert.True(windows.ToggleBorderless(target.Handle, fill));
            Assert.Equal(0, Style(target.Handle) & WindowController.BorderlessMask);
            if (fill) Assert.Equal(Screen.FromHandle(target.Handle).Bounds, Bounds(target.Handle));
            else Assert.Equal(client, Client(target.Handle));
            Assert.False(windows.ToggleBorderless(target.Handle));
            Assert.Equal(style, Style(target.Handle));
            Assert.Equal(before, Bounds(target.Handle));
        }
        finally { windows.RestoreAll(); }
    }

    [Fact]
    public void Borderless_recovery_keeps_a_move_and_unrelated_style_changes()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var windows = new WindowController([], new ChangeJournal(path));
        Rectangle before = Bounds(target.Handle);
        windows.ToggleBorderless(target.Handle);
        Rectangle stripped = Bounds(target.Handle);
        User32.SetWindowPos(target.Handle, 0, stripped.X + 80, stripped.Y + 60, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
        const long disabled = 0x08000000;
        User32.SetWindowLongPtr(target.Handle, User32.GWL_STYLE, (nint)(Style(target.Handle) | disabled));
        var restarted = new WindowController([], new ChangeJournal(path));
        restarted.RecoverFromJournal();
        Assert.Empty(restarted.RestoreFailures);
        Assert.Equal(new Rectangle(before.X + 80, before.Y + 60, before.Width, before.Height), Bounds(target.Handle));
        Assert.NotEqual(0, Style(target.Handle) & disabled);
        Assert.Empty(new ChangeJournal(path).Load());
    }

    [Fact]
    public void Fill_restores_a_maximized_window_to_its_original_placement()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle)));
        var windows = new WindowController([], new ChangeJournal(JournalPath()));
        Rectangle before = Bounds(target.Handle);
        try
        {
            Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(target.Handle));
            windows.ToggleBorderless(target.Handle, true);
            Assert.False(User32.IsZoomed(target.Handle));
            Assert.Equal(Screen.FromHandle(target.Handle).Bounds, Bounds(target.Handle));
            windows.ToggleBorderless(target.Handle);
            Assert.True(User32.IsZoomed(target.Handle));
            Assert.Equal(before, Bounds(target.Handle));
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>
    /// Windows 11 draws a window's backdrop over its whole size even when only part is kept, so a cropped Chrome window
    /// left a dark panel over what was behind (the owner, 15 Nov). The backdrop is off while cropped and back after.
    /// </summary>
    [Fact]
    public void A_crop_turns_the_backdrop_off_and_puts_it_back()
    {
        if (Environment.OSVersion.Version.Build < 22621) { Skipped.Because("Window backdrops are Windows 11 22H2 and later."); return; }
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        nint window = target.CreatePlainWindow(Spot, framed: true);
        Assert.Equal(0, Dwmapi.DwmSetWindowAttribute(window, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, 2, sizeof(int))); // Mica
        var windows = new WindowController([]);
        try
        {
            windows.Crop(window, new Rectangle(0, 0, 150, 100));
            Assert.Equal(Dwmapi.DWMSBT_NONE, Backdrop(window));
            Assert.Equal(new Rectangle(0, 0, 150, 100), WindowInfo.ShapeOf(window));
            windows.RemoveCrop(window);
            Assert.Equal(2, Backdrop(window));
            windows.Crop(window, new Rectangle(0, 0, 150, 100));
            windows.RestoreAll();
            Assert.Equal(2, Backdrop(window));
        }
        finally { windows.RestoreAll(); }

        static int Backdrop(nint window) =>
            Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, out int value, sizeof(int)) == 0 ? value : -1;
    }

    /// <summary>
    /// Windows stops drawing the frame of a window with a shape, but a cropped Task Manager kept its whole frame, in
    /// the title bar's colour with its buttons, around the part kept (the owner, 27 Nov). A window without Windows'
    /// title bar, with a WinUI content child and asking for its frame always, stands in for it: the frame is off while
    /// cropped, and Windows' own choice is back after. Since 29 Nov one without the child goes the same way, as Chrome
    /// does (A_cropped_browser_keeps_its_crop_with_no_frame_round_it).
    /// </summary>
    [Fact]
    public void A_crop_stops_the_frame_a_window_would_keep_drawing_around_it()
    {
        using var target = new TargetProcess(Spot);
        nint window = target.CreatePlainWindow(Spot, winUi: true);
        // A second one, left alone, shows what Windows chooses for such a window: a crop's end goes back to that.
        nint untouched = target.CreatePlainWindow(new Rectangle(Spot.X + 40, Spot.Y + 40, Spot.Width, Spot.Height), winUi: true);
        nint other = target.CreatePlainWindow(new Rectangle(Spot.X + 80, Spot.Y + 80, Spot.Width, Spot.Height));
        const int Enabled = 2; // DWMNCRP_ENABLED
        foreach (nint each in new[] { window, other })
            Assert.Equal(0, Dwmapi.DwmSetWindowAttribute(each, Dwmapi.DWMWA_NCRENDERING_POLICY, Enabled, sizeof(int)));
        Assert.True(FrameDrawn(window));
        var windows = new WindowController([]);
        try
        {
            windows.Crop(window, new Rectangle(40, 40, 150, 100));
            Assert.False(FrameDrawn(window));
            Assert.Equal(new Rectangle(40, 40, 150, 100), WindowInfo.ShapeOf(window));
            windows.RemoveCrop(window);
            Assert.Equal(FrameDrawn(untouched), FrameDrawn(window));
            windows.Crop(window, new Rectangle(40, 40, 150, 100));
            Assert.False(FrameDrawn(window));
            windows.RestoreAll();
            Assert.Equal(FrameDrawn(untouched), FrameDrawn(window));
            Assert.Null(WindowInfo.ShapeOf(window));

            windows.Crop(other, new Rectangle(40, 40, 150, 100));
            Assert.False(FrameDrawn(other));
        }
        finally { windows.RestoreAll(); }
    }

    private static bool FrameDrawn(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_NCRENDERING_ENABLED, out int value, sizeof(int)) == 0 && value != 0;

    /// <summary>
    /// Chrome draws its own title bar too. With its frame left on, Windows drew its accent line and shadow round the whole
    /// window, a dark outline round the part kept (the owner, 29 Nov). With the frame off, Chrome puts its own shape over
    /// the whole window (27 Nov), so the part kept is put back: only that part shows, with nothing round it, and the
    /// frame comes back with the whole window. The owner, 29 Nov: a second crop was refused, as Chrome dropped the shape
    /// at once, and its frame was left off, after which Chrome kept a shape of its own: every crop after was refused. Each
    /// time, maximized too, the part kept stays and the window is whole again after.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cropped_browser_keeps_its_crop_with_no_frame_round_it(bool maximized)
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        using var back = new TargetProcess(Rectangle.Inflate(Spot, 60, 60), "FFFFFF");
        var (browser, window, root) = BrowserInputTests.Open("<!doctype html><title>CropFrame</title><body style='background:#1e6'>",
            "CropFrame", app: false, chrome);
        var windows = new WindowController([]);
        try
        {
            if (User32.IsZoomed(window)) User32.ShowWindow(window, User32.SW_RESTORE);
            User32.SetWindowPos(window, 0, Spot.Left, Spot.Top, Spot.Width, Spot.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            TestDesktop.Pump(500);
            User32.SetWindowPos(back.Handle, window, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            if (maximized) { User32.ShowWindow(window, 3); TestDesktop.Pump(800); }
            bool before = FrameDrawn(window);
            var part = new Rectangle(Spot.Width - 260, Spot.Height - 190, 200, 150);
            for (int time = 1; time <= 3; time++)
            {
                windows.Crop(window, part);
                for (int i = 0; i < 20; i++)
                {
                    TestDesktop.Pump(100);
                    windows.Refresh(Cursor.Position, false, false);
                }
                TestDesktop.Pump(500);
                Assert.True(part == WindowInfo.ShapeOf(window), $"Crop {time}: {WindowInfo.ShapeOf(window)}.");
                Assert.True(windows.IsCropped(window));
                if (time == 1 && !maximized) NothingRound();
                if (time == 2) windows.RestoreWindow(window); else windows.RemoveCrop(window);
                TestDesktop.Pump(2500); // Chrome put its own shape back about a second after its frame came back.
                Assert.True(WindowInfo.ShapeOf(window) is null, $"After crop {time}: {WindowInfo.ShapeOf(window)}.");
                Assert.Equal(before, FrameDrawn(window));
            }

            // Just inside the window's old edges, away from the part kept: what is behind, not a line or a shadow.
            void NothingRound()
            {
            using (var picture = new Bitmap(Spot.Width, Spot.Height))
            {
                using (Graphics g = Graphics.FromImage(picture)) g.CopyFromScreen(Spot.Location, Point.Empty, Spot.Size);
                foreach (Point at in new[] { new Point(120, 0), new Point(120, 1), new Point(0, 120), new Point(Spot.Width - 1, 60), new Point(120, Spot.Height - 1) })
                {
                    Color seen = picture.GetPixel(at.X, at.Y);
                    Assert.True(seen.R > 240 && seen.G > 240 && seen.B > 240, $"At {at} round the part kept: {seen}.");
                }
            }
            }
        }
        finally
        {
            windows.RestoreAll();
            BrowserInputTests.Close(browser, root);
        }
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("crop")]
    [InlineData("rollup")]
    public void Incompatible_states_refuse_borderless_without_removing_the_frame(string state)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        // A Windows Forms form with a frame puts its own shape back, so a crop is tried on a window that keeps it.
        nint window = state == "crop" ? target.CreatePlainWindow(Spot, framed: true) : target.Handle;
        var windows = new WindowController([]);
        try
        {
            switch (state)
            {
                case "hidden": windows.Hide(window); break;
                case "crop": windows.Crop(window, new Rectangle(0, 0, 150, 100)); break;
                case "rollup": windows.ToggleRollUp(window); break;
            }
            long style = Style(window);
            Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(window));
            Assert.Equal(style, Style(window));
        }
        finally { windows.RestoreAll(); }
    }

    /// <summary>
    /// A window with no frame at all has nothing for Borderless to take away, and no title bar of Windows' to darken,
    /// so both say so. It can still fill a display, and comes back exactly from it.
    /// </summary>
    [Fact]
    public void A_window_without_a_frame_refuses_borderless_and_dark_title_bar_but_can_fill_a_display()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        try
        {
            Assert.False(WindowController.HasNativeTitleBar(target.Handle));
            Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(target.Handle));
            Assert.Throws<WindowOperationException>(() => windows.ToggleDarkTitleBar(target.Handle));
            Rectangle before = Desktop.BoundsOf(target.Handle);
            Rectangle display = Screen.FromHandle(target.Handle).Bounds;
            Assert.True(windows.ToggleBorderless(target.Handle, fill: true, display));
            Assert.Equal(display, Desktop.BoundsOf(target.Handle));
            Assert.False(windows.ToggleBorderless(target.Handle, fill: true, display));
            Assert.Equal(before, Desktop.BoundsOf(target.Handle));
        }
        finally { windows.RestoreAll(); }
    }

    [Fact]
    public void A_hung_window_refuses_borderless_without_changing_its_frame()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        long before = Style(target.Handle);
        target.Hang();
        var windows = new WindowController([]);
        Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(target.Handle));
        Assert.Equal(before, Style(target.Handle));
        Assert.False(windows.IsBorderless(target.Handle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dark_title_bar_is_restored_by_toggle_or_recovery(bool recover)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var windows = new WindowController([], new ChangeJournal(path));
        bool before = windows.IsDarkTitleBar(target.Handle);
        windows.ToggleDarkTitleBar(target.Handle);
        Assert.Equal(!before, windows.IsDarkTitleBar(target.Handle));
        if (recover) new WindowController([], new ChangeJournal(path)).RecoverFromJournal();
        else windows.ToggleDarkTitleBar(target.Handle);
        Assert.Equal(before, windows.IsDarkTitleBar(target.Handle));
        windows.RestoreAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Switcher_styles_restore_on_show_or_restart_even_when_original_bits_are_zero(bool recover)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var windows = new WindowController([], new ChangeJournal(path));
        long before = Desktop.ExStyle(target.Handle);
        try
        {
            Assert.Throws<WindowOperationException>(() => windows.SetOutOfSwitcher(target.Handle, true));
            windows.Hide(target.Handle);
            windows.SetOutOfSwitcher(target.Handle, true);
            Assert.True(windows.IsOutOfSwitcher(target.Handle));
            Assert.False(WindowInfo.IsMainWindow(target.Handle));
            Assert.Equal(User32.WS_EX_TOOLWINDOW, Desktop.ExStyle(target.Handle) & WindowController.SwitcherMask);
            Assert.Equal(before & WindowController.SwitcherMask, Assert.Single(new ChangeJournal(path).Load()).SwitcherBits);
            if (recover)
            {
                var next = new WindowController([], new ChangeJournal(path));
                next.RecoverFromJournal();
                Assert.Empty(next.RestoreFailures);
                Assert.Equal(before, Desktop.ExStyle(target.Handle));
            }
            else
            {
                windows.Show(target.Handle);
                Assert.False(windows.IsOutOfSwitcher(target.Handle));
                Assert.False(windows.IsHidden(target.Handle));
                Assert.Equal(before & WindowController.SwitcherMask, Desktop.ExStyle(target.Handle) & WindowController.SwitcherMask);
            }
        }
        finally { windows.RestoreAll(); }
    }

    [Theory]
    [InlineData("borderless")]
    [InlineData("dark")]
    [InlineData("switcher")]
    public void New_changes_are_journaled_before_native_mutation(string feature)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var journal = new ChangeJournal(path);
        var windows = new WindowController([], journal);
        if (feature == "switcher") windows.Hide(target.Handle);
        long style = Style(target.Handle), exStyle = Desktop.ExStyle(target.Handle);
        windows.Checkpoint = (step, _) =>
        {
            if (step != "before-mutation") return;
            WindowRecord record = Assert.Single(journal.Load());
            Assert.True(feature switch
            {
                "borderless" => record.StyleBits != 0 && record.BorderlessPlacement is not null,
                "dark" => record.OriginalDarkTitleBar is not null,
                _ => record.SwitcherBits is not null,
            });
            throw new WindowOperationException("Injected failure");
        };
        try
        {
            Assert.Throws<WindowOperationException>(() =>
            {
                switch (feature)
                {
                    case "borderless": windows.ToggleBorderless(target.Handle); break;
                    case "dark": windows.ToggleDarkTitleBar(target.Handle); break;
                    case "switcher": windows.SetOutOfSwitcher(target.Handle, true); break;
                }
            });
            Assert.Equal(style, Style(target.Handle));
            Assert.Equal(exStyle, Desktop.ExStyle(target.Handle));
        }
        finally { windows.Checkpoint = null; windows.RestoreAll(); }
    }

    [Fact]
    public void Recovery_does_not_write_to_a_reused_window_identity()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var windows = new WindowController([], new ChangeJournal(path));
        windows.ToggleBorderless(target.Handle);
        var identity = WindowIdentity.Capture(target.Handle);
        User32.SetProp(target.Handle, WindowIdentity.PropertyName, (nint)(identity.Token ^ 2));
        long before = Style(target.Handle);
        new WindowController([], new ChangeJournal(path)).RecoverFromJournal();
        Assert.Equal(before, Style(target.Handle));
    }
}
