using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The displays change under a view whose original is hidden. Neither the test VM nor a one-monitor PC can add or
/// remove a monitor, so this reproduces what such a change does to the windows: Windows moves a window from a monitor
/// that went away back onto a remaining screen, and every top-level window hears WM_DISPLAYCHANGE.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class DisplayChangeTests
{
    private const int WM_DISPLAYCHANGE = 0x007E;

    /// <summary>The window overlaps the screen by a thin strip only, at an outer edge.</summary>
    private static bool ParkedAtAnEdge(Rectangle window)
    {
        Rectangle overlap = Rectangle.Intersect(window, SystemInformation.VirtualScreen);
        return !overlap.IsEmpty && Math.Min(overlap.Width, overlap.Height) <= 2;
    }

    [Fact]
    public void After_a_display_change_the_hidden_original_is_parked_again_and_the_view_stays_on_screen()
    {
        using var source = new TargetProcess(new Rectangle(200, 150, 700, 500));
        source.UseFrame();
        Rectangle home = Desktop.BoundsOf(source.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows);
            view.ShowOnTop();
            view.SetOriginalHidden(true);
            Assert.True(windows.IsParked(source.Handle));
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Assert.True(ParkedAtAnEdge(parked), "Not parked at first: " + parked);

            // As after its monitor went away: Windows brought it onto the screen that's left, and the view's
            // monitor is gone too, so it now hangs off the edge.
            User32.SetWindowPos(source.Handle, 0, 300, 200, parked.Width, parked.Height, User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            view.Location = new Point(area.Right - view.Width / 3, area.Bottom - view.Height / 3);
            User32.SendMessageTimeout(view.Handle, WM_DISPLAYCHANGE, 32, (nint)((area.Height << 16) | area.Width), 0, 2000, out _);
            Assert.True(Desktop.WaitFor(() => ParkedAtAnEdge(Desktop.BoundsOf(source.Handle)), 3000, Application.DoEvents),
                "After the change the original is at " + Desktop.BoundsOf(source.Handle));
            Assert.True(WindowInfo.IsConcealed(source.Handle));
            Assert.True(Desktop.WaitFor(() => area.Contains(view.Bounds), 3000, Application.DoEvents),
                $"The view is at {view.Bounds}, off the {area} working area.");

            view.Close();
            Assert.Equal(home, Desktop.BoundsOf(source.Handle)); // Back where it was, visible.
            Assert.False(WindowInfo.IsConcealed(source.Handle));
        });
    }
}
