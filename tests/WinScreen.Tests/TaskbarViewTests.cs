using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class TaskbarViewGeometryTests
{
    [Theory]
    [InlineData(400, 300, 200, 120, 160, 120)]
    [InlineData(300, 600, 200, 120, 60, 120)]
    [InlineData(10, 10, 200, 120, 120, 120)]
    [InlineData(0, 10, 200, 120, 200, 120)]
    public void A_preview_keeps_its_shape_inside_what_Windows_asked_for(int width, int height, int limitWidth, int limitHeight, int expectedWidth, int expectedHeight) =>
        Assert.Equal(new Size(expectedWidth, expectedHeight), PipGeometry.FitInside(new Size(width, height), new Size(limitWidth, limitHeight)));

    [Fact]
    public void Older_settings_keep_minimizing_to_the_tray() =>
        Assert.False(AppSettings.Parse("{}").PipViews.MinimizeToTaskbar);
}

[Trait("Category", "RealWindows")]
public sealed class TaskbarViewTests
{
    private const int WM_SYSCOMMAND = 0x0112, SC_RESTORE = 0xF120, Thumbnail = 0x0323, LivePreview = 0x0326, WM_CLOSE = 0x0010;

    private static void Pump(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static PipWindow Open(nint source, bool taskbar = true)
    {
        var view = new PipWindow(source, new WindowController([]));
        view.ApplyViewPreferences(new PipViewPreferences { MinimizeToTaskbar = taskbar });
        view.ShowOnTop();
        Pump(150);
        return view;
    }

    [Fact]
    public void A_minimized_view_has_one_button_uses_no_backend_and_comes_back_in_place()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using PipWindow view = Open(target.Handle);
            view.Location = new Point(700, 200);
            Rectangle place = view.Bounds;
            nint focus = User32.GetForegroundWindow();
            view.SuspendView();
            Pump(200);
            PipTaskbarProxy proxy = view.TaskbarProxy!;
            nint button = proxy.Handle;
            Assert.True(User32.IsIconic(button));
            Assert.Equal(view.Text.Replace(Localization.UiText.T("pip.titlePrefix"), ""), proxy.Text);
            Assert.False(view.Visible);
            Assert.Equal(PipSessionState.Stopped, view.BackendState);
            Assert.Equal(focus, User32.GetForegroundWindow());

            // Windows refuses larger pictures until the taskbar has really asked for one, which takes a hover.
            User32.SendMessageTimeout(button, Thumbnail, 0, (32 << 16) | 32, 2, 1000, out _);
            Assert.True(proxy.LastPreviewResult == 0, $"Thumbnail: {proxy.LastPreviewResult:X}");
            User32.SendMessageTimeout(button, LivePreview, 0, 0, 2, 1000, out _);
            Assert.True(proxy.LastPreviewResult == 0, $"Live preview: {proxy.LastPreviewResult:X}");

            User32.SendMessageTimeout(button, WM_SYSCOMMAND, SC_RESTORE, 0, 2, 1000, out _);
            Pump(200);
            Assert.False(view.IsSuspended);
            Assert.True(view.Visible);
            Assert.Equal(place, view.Bounds);
            Assert.Null(view.TaskbarProxy);
            Assert.False(User32.IsWindow(button));
            view.Close();
        });
    }

    [Fact]
    public void Closing_the_button_closes_the_view_and_leaves_no_button_behind()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            PipWindow view = Open(target.Handle);
            view.SuspendView();
            nint button = view.TaskbarProxy!.Handle;
            User32.SendMessageTimeout(button, WM_CLOSE, 0, 0, 2, 1000, out _);
            Pump(200);
            Assert.True(view.IsDisposed);
            Assert.False(User32.IsWindow(button));
        });
    }

    [Fact]
    public void A_view_closed_by_its_owner_takes_its_button_with_it()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            PipWindow view = Open(target.Handle);
            view.SuspendView();
            nint button = view.TaskbarProxy!.Handle;
            view.Close();
            Pump(100);
            Assert.False(User32.IsWindow(button));
        });
    }

    [Fact]
    public void With_the_option_off_a_minimized_view_has_no_button()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using PipWindow view = Open(target.Handle, taskbar: false);
            view.SuspendView();
            Assert.Null(view.TaskbarProxy);
            view.Close();
        });
    }
}
