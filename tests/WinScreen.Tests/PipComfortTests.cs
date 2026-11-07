using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipPreviewGeometryTests
{
    [Theory]
    [InlineData(-100, -100)]
    [InlineData(900, 800)]
    [InlineData(120, 90)]
    public void Lens_keeps_its_sample_inside_the_shown_crop(int x, int y)
    {
        var shown = new Rectangle(50, 40, 300, 200);
        Rectangle lens = PipPreviewGeometry.Lens(shown, new Point(x, y), new Size(240, 160));
        Assert.Equal(new Size(240, 160), lens.Size);
        Assert.True(shown.Contains(lens));
    }

    [Fact]
    public void A_small_source_and_negative_monitor_coordinates_stay_in_bounds()
    {
        var crop = new Rectangle(10, 20, 40, 30);
        Assert.Equal(crop, PipPreviewGeometry.Lens(crop, Point.Empty, new Size(240, 160)));
        var work = new Rectangle(-1920, -200, 1920, 1080);
        Rectangle preview = PipPreviewGeometry.Place(new Rectangle(-100, -50, 80, 60), new Size(800, 600), work);
        Assert.True(work.Contains(preview));
        Assert.True(preview.Right <= -100);
    }
}

[Trait("Category", "RealWindows")]
public sealed class PipComfortTests
{
    [Fact]
    public void Click_through_opacity_is_not_applied_twice_on_open_or_restore()
    {
        using var source = new TargetProcess(new Rectangle(240, 160, 400, 300), "C81E1E");
        using var backdrop = new TargetProcess(new Rectangle(650, 100, 600, 500), "1E64C8");
        User32.SetWindowPos(backdrop.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            view.Overrides = view.Overrides with { ClickThrough = true, OpacityPercent = 50 };
            view.ShowOnTop();
            view.Location = new Point(700, 120);
            Rectangle sample = new(view.Left + view.Width / 2, view.Top + view.Height / 2, 20, 20);
            bool HasExpectedOpacity() => Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample),
                Color.FromArgb(115, 65, 115)), 3000, () => { Application.DoEvents(); Thread.Sleep(10); });
            Assert.True(HasExpectedOpacity(), Desktop.ColorOnScreen(sample).ToString());
            view.SuspendView();
            view.ResumeView();
            Assert.True(HasExpectedOpacity(), Desktop.ColorOnScreen(sample).ToString());
        });
    }

    [Fact]
    public void Preview_thumbnail_follows_the_real_sources_pixels()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300), "C81E1E");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var owner = new Form { ShowInTaskbar = false };
            using var popup = new WinScreen.Pip.PipHoverPreview(owner, target.Handle);
            popup.UpdatePreview(new Rectangle(0, 0, 400, 300), new Rectangle(700, 120, 320, 240));
            Rectangle sample = new(popup.Left + 100, popup.Top + 100, 20, 20);
            bool Shows(Color color) => Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), color),
                3000, () => { Application.DoEvents(); Thread.Sleep(10); });
            Assert.True(Shows(Color.FromArgb(0xC8, 0x1E, 0x1E)), Desktop.ColorOnScreen(sample).ToString());
            target.SetColors("1E64C8");
            Assert.True(Shows(Color.FromArgb(0x1E, 0x64, 0xC8)), Desktop.ColorOnScreen(sample).ToString());
            Assert.NotEqual(popup.Handle, Desktop.TopWindowAt(new Point(sample.Left, sample.Top)));
        });
    }

    [WgcFact]
    public void Screen_view_title_bar_is_excluded_from_capture_and_has_no_window_preview()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(new ScreenPipSource(Screen.PrimaryScreen!.DeviceName, null), new WindowController([]));
            view.ApplyViewPreferences(new() { HoverPreview = WinScreen.Settings.PipHoverPreview.WholeView });
            view.ShowOnTop();
            var point = new Point(view.Left + view.Width / 2, view.Top + view.Height / 2);
            view.UpdateHover(point, 1000);
            view.UpdateHover(point, 2000);
            var bar = Assert.Single(view.OwnedForms.OfType<PipTitleBar>());
            Assert.True(User32.GetWindowDisplayAffinity(bar.Handle, out uint affinity));
            Assert.Equal(User32.WDA_EXCLUDEFROMCAPTURE, affinity);
            Assert.Empty(view.OwnedForms.OfType<WinScreen.Pip.PipHoverPreview>());
        });
    }

    [Fact]
    public void Title_bar_drag_moves_the_view_and_respects_its_position_lock()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(target.Handle, new WindowController([]));
            view.ShowOnTop();
            view.Location = new Point(700, 120);
            view.UpdateHover(new Point(750, 180), 1000);
            var bar = Assert.Single(view.OwnedForms.OfType<PipTitleBar>());
            var title = Assert.Single(bar.Controls.OfType<Label>());
            void Mouse(string method, int x, int y) => typeof(Control).GetMethod(method,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(title, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);
            Point original = view.Location;
            Mouse("OnMouseDown", 5, 5);
            Mouse("OnMouseMove", 25, 15);
            Mouse("OnMouseUp", 25, 15);
            Assert.Equal(original + new Size(20, 10), view.Location);
            view.Overrides = view.Overrides with { PositionLocked = true };
            original = view.Location;
            Mouse("OnMouseDown", 5, 5);
            Mouse("OnMouseMove", 50, 50);
            Mouse("OnMouseUp", 50, 50);
            Assert.Equal(original, view.Location);
        });
    }

    [WgcFact]
    public void Suspending_an_advanced_view_stops_capture_and_restores_the_same_bounds()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(target.Handle, new WindowController([]));
            view.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            view.ShowOnTop();
            Assert.True(Desktop.WaitFor(() => view.BackendKind != PipBackendKind.DwmThumbnail, 10000, Application.DoEvents), view.DiagnosticSnapshot);
            Rectangle before = view.Bounds;
            view.SuspendView();
            Assert.Equal("Stopped", view.DiagnosticSnapshot);
            Assert.False(view.Visible);
            view.ResumeView();
            Assert.Equal(before, view.Bounds);
            Assert.True(view.Visible);
        });
    }

    [Theory]
    [InlineData(WinScreen.Settings.PipHoverPreview.WholeView)]
    [InlineData(WinScreen.Settings.PipHoverPreview.Lens)]
    public void Hover_opens_after_rest_is_click_through_and_is_destroyed_on_leave(WinScreen.Settings.PipHoverPreview mode)
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(target.Handle, new WindowController([]));
            view.ApplyViewPreferences(new() { CompactTitleBar = false, HoverPreview = mode });
            view.ShowOnTop();
            view.Location = new Point(700, 120);
            view.SetCrop(new Rectangle(30, 40, 200, 150));
            var point = new Point(view.Left + view.Width / 2, view.Top + view.Height / 2);
            nint focus = User32.GetForegroundWindow();
            view.UpdateHover(point, 1000);
            Assert.Empty(view.OwnedForms.OfType<WinScreen.Pip.PipHoverPreview>());
            view.UpdateHover(point, 1700);
            var popup = Assert.Single(view.OwnedForms.OfType<WinScreen.Pip.PipHoverPreview>());
            Assert.True(popup.Visible);
            Assert.Equal(focus, User32.GetForegroundWindow());
            long style = Desktop.ExStyle(popup.Handle);
            Assert.NotEqual(0, style & User32.WS_EX_TRANSPARENT);
            Assert.NotEqual(0, style & User32.WS_EX_NOACTIVATE);
            Assert.True(Screen.FromControl(view).WorkingArea.Contains(popup.Bounds));
            view.UpdateHover(new Point(view.Left - 1, view.Top - 1), 1800);
            Assert.True(popup.IsDisposed);
        });
    }

    [Fact]
    public void Title_bar_shows_the_source_title_and_minimize_preserves_view_options()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300), title: "Document & title");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(target.Handle, windows);
            try
            {
                view.ShowOnTop();
                view.Location = new Point(700, 120);
                view.SetCrop(new Rectangle(20, 30, 200, 160));
                view.Overrides = view.Overrides with { PositionLocked = true, OpacityPercent = 75 };
                view.SetOriginalHidden(true);
                Rectangle bounds = view.Bounds;
                view.UpdateHover(new Point(view.Left + 50, view.Top + 70), 1000);
                var bar = Assert.Single(view.OwnedForms.OfType<PipTitleBar>());
                Assert.Contains(bar.Controls.OfType<Label>(), label => label.Text == "Document & title");
                var minimize = Assert.Single(bar.Controls.OfType<Button>(), button => button.AccessibleName == Localization.UiText.T("pip.minimize"));
                minimize.PerformClick();
                Assert.True(view.IsSuspended);
                Assert.False(view.Visible);
                Assert.Equal(PipSessionState.Stopped, view.BackendState);
                Assert.True(windows.IsHidden(target.Handle));
                view.ResumeView();
                Assert.False(view.IsSuspended);
                Assert.True(view.Visible);
                Assert.Equal(bounds, view.Bounds);
                Assert.Equal(new Rectangle(20, 30, 200, 160), view.Crop);
                Assert.Equal(75, view.EffectivePreferences.OpacityPercent);
                Assert.True(view.Overrides.PositionLocked);
                view.Close();
                Assert.False(windows.IsHidden(target.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void Disabled_hover_and_click_through_views_create_no_preview_popup()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(target.Handle, new WindowController([]));
            view.ApplyViewPreferences(new() { CompactTitleBar = false });
            view.ShowOnTop();
            var pointer = new Point(view.Left + 50, view.Top + 70);
            view.UpdateHover(pointer, 1000);
            view.UpdateHover(pointer, 2000);
            Assert.Empty(view.OwnedForms.OfType<WinScreen.Pip.PipHoverPreview>());
            view.ApplyViewPreferences(new() { HoverPreview = WinScreen.Settings.PipHoverPreview.Lens });
            view.Overrides = view.Overrides with { ClickThrough = true };
            view.UpdateHover(pointer, 3000);
            view.UpdateHover(pointer, 4000);
            Assert.DoesNotContain(view.OwnedForms, form => form.Visible);
        });
    }
}
