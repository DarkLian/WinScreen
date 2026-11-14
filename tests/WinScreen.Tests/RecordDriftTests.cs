using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// What WinScreen thinks a window has must follow what the person or the app did to it since (the owner's report on
/// 1.1.0-beta, F10): a window rolled up and then maximized or made taller is no longer rolled up.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class RecordDriftTests
{
    private static readonly Rectangle Spot = new(260, 180, 520, 380);

    private static void Run(Action<TargetProcess, WindowController> test)
    {
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            try { test(target, windows); }
            finally { windows.RestoreAll(); }
        });
    }

    [Fact]
    public void Maximizing_a_rolled_up_window_ends_the_roll_up_and_restoring_it_brings_its_height_back()
    {
        Run((target, windows) =>
        {
            int height = Desktop.BoundsOf(target.Handle).Height;
            Assert.True(User32.GetWindowRect(target.Handle, out Rect start));
            Assert.True(windows.ToggleRollUp(target.Handle));
            Assert.True(Desktop.BoundsOf(target.Handle).Height < height / 2);

            User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
            Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle), 2000, Application.DoEvents));
            Assert.False(windows.IsRolledUp(target.Handle));
            // Windows' restore size is the height from before. (This test window's own code puts a restored window
            // back where it last put it, whatever Windows says, so its restore isn't a fair measure.)
            var placement = new WindowPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<WindowPlacement>() };
            Assert.True(User32.GetWindowPlacement(target.Handle, ref placement));
            Assert.Equal(start.Bottom - start.Top, placement.NormalPosition.Bottom - placement.NormalPosition.Top);

            User32.ShowWindow(target.Handle, User32.SW_RESTORE);
            Assert.True(Desktop.WaitFor(() => !User32.IsZoomed(target.Handle), 2000, Application.DoEvents));
            User32.SetWindowPos(target.Handle, 0, 0, 0, start.Right - start.Left, start.Bottom - start.Top, User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

            // The menu's Roll up rolls it up again, rather than "restoring" a size from before.
            Assert.True(windows.ToggleRollUp(target.Handle));
            Assert.True(windows.IsRolledUp(target.Handle));
            Assert.False(windows.ToggleRollUp(target.Handle));
            Assert.Equal(height, Desktop.BoundsOf(target.Handle).Height);
        });
    }

    [Fact]
    public void Making_a_rolled_up_window_taller_ends_the_roll_up_and_keeps_the_new_size()
    {
        Run((target, windows) =>
        {
            Assert.True(windows.ToggleRollUp(target.Handle));
            Rectangle rolled = Desktop.BoundsOf(target.Handle);
            User32.SetWindowPos(target.Handle, 0, 0, 0, rolled.Width, 300, User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Assert.False(windows.IsRolledUp(target.Handle));
            Assert.Equal(300, Desktop.BoundsOf(target.Handle).Height);
            windows.RestoreAll();
            Assert.Equal(300, Desktop.BoundsOf(target.Handle).Height); // Putting it back leaves the person's size alone.
        });
    }

    [Fact]
    public void A_rolled_up_window_minimized_and_shown_again_is_still_rolled_up()
    {
        Run((target, windows) =>
        {
            int height = Desktop.BoundsOf(target.Handle).Height;
            Assert.True(windows.ToggleRollUp(target.Handle));
            User32.ShowWindow(target.Handle, User32.SW_MINIMIZE);
            Assert.True(Desktop.WaitFor(() => User32.IsIconic(target.Handle), 2000, Application.DoEvents));
            Assert.True(windows.IsRolledUp(target.Handle));
            User32.ShowWindow(target.Handle, User32.SW_RESTORE);
            Assert.True(Desktop.WaitFor(() => !User32.IsIconic(target.Handle), 2000, Application.DoEvents));
            Assert.True(windows.IsRolledUp(target.Handle));
            Assert.False(windows.ToggleRollUp(target.Handle));
            Assert.Equal(height, Desktop.BoundsOf(target.Handle).Height);
        });
    }
}
