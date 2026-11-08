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

    [Theory]
    [InlineData("hidden")]
    [InlineData("crop")]
    [InlineData("rollup")]
    [InlineData("minimized")]
    public void Incompatible_states_refuse_borderless_without_removing_the_frame(string state)
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        var windows = new WindowController([]);
        try
        {
            switch (state)
            {
                case "hidden": windows.Hide(target.Handle); break;
                case "crop": windows.Crop(target.Handle, new Rectangle(0, 0, 150, 100)); break;
                case "rollup": windows.ToggleRollUp(target.Handle); break;
                case "minimized": User32.ShowWindow(target.Handle, 6); break;
            }
            long style = Style(target.Handle);
            Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(target.Handle));
            Assert.Equal(style, Style(target.Handle));
        }
        finally { windows.RestoreAll(); }
    }

    [Fact]
    public void A_window_without_a_native_caption_refuses_borderless_and_dark_title_bar()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        Assert.False(WindowController.HasNativeTitleBar(target.Handle));
        Assert.Throws<WindowOperationException>(() => windows.ToggleBorderless(target.Handle));
        Assert.Throws<WindowOperationException>(() => windows.ToggleDarkTitleBar(target.Handle));
        windows.RestoreAll();
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
