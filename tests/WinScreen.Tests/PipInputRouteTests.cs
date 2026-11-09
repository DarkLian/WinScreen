using WinScreen.Pip;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipInputRouteTests
{
    [Theory]
    [InlineData("Chrome_RenderWidgetHostHWND", true, true, InputRoute.Messages)]
    [InlineData("DirectUIHWND", true, true, InputRoute.Messages)]
    [InlineData("WindowsForms10.Window.8.app.0.1", true, true, InputRoute.Messages)]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", true, true, InputRoute.Automation)]
    [InlineData("Windows.UI.Core.CoreWindow", false, true, InputRoute.Automation)]  // Store app content: another process
    [InlineData("ApplicationFrameInputSinkWindow", true, true, InputRoute.Automation)]
    [InlineData("Chrome_RenderWidgetHostHWND", false, true, InputRoute.Refused)]     // Another program's child
    [InlineData("Windows.UI.Core.CoreWindow", false, false, InputRoute.Refused)]     // Not inside the original
    [InlineData("Chrome_WidgetWin_1", true, false, InputRoute.Refused)]
    public void Each_window_gets_one_route(string className, bool sameProcess, bool inside, InputRoute route) =>
        Assert.Equal(route, PipInputRoutes.RouteFor(className, sameProcess, inside));

    [Theory]
    [InlineData(InputOperation.Move, false)]
    [InlineData(InputOperation.Click, true)]
    [InlineData(InputOperation.DoubleClick, true)]   // Acted on only where it means two clicks; the control decides.
    [InlineData(InputOperation.ContextClick, true)]
    [InlineData(InputOperation.OtherButton, false)]
    [InlineData(InputOperation.Drag, false)]
    [InlineData(InputOperation.Wheel, true)]          // Scrolled through the scroll pattern.
    public void Automation_carries_only_what_has_an_equivalent(InputOperation operation, bool supported) =>
        Assert.Equal(supported, PipInputRoutes.Supports(InputRoute.Automation, operation));

    [Fact]
    public void Messages_carry_everything_and_a_refused_window_nothing()
    {
        foreach (InputOperation operation in Enum.GetValues<InputOperation>())
        {
            Assert.True(PipInputRoutes.Supports(InputRoute.Messages, operation));
            Assert.False(PipInputRoutes.Supports(InputRoute.Refused, operation));
        }
    }

    [Fact]
    public void Parking_prefers_the_windows_own_monitor()
    {
        // Side by side: the right monitor's bottom edge comes first for a window on it, not the left one's.
        Rectangle left = new(0, 0, 1920, 1080), right = new(1920, 0, 2560, 1440);
        Rectangle parked = WindowController.ParkingPlace(new Size(800, 600), [left, right], right);
        Assert.Equal(new Point(1920, 1438), parked.Location);
        Assert.Equal(new Point(0, 1078), WindowController.ParkingPlace(new Size(800, 600), [left, right], left).Location);
    }

    [Fact]
    public void Parking_leaves_the_own_monitor_only_when_none_of_its_edges_is_outside()
    {
        // Three stacked monitors: the middle one has no outer bottom or top edge, but outer left and right ones.
        Rectangle top = new(0, -1080, 1920, 1080), middle = new(0, 0, 1920, 1080), bottom = new(0, 1080, 1920, 1080);
        Rectangle parked = WindowController.ParkingPlace(new Size(800, 600), [top, middle, bottom], middle);
        Assert.Equal(new Point(1918, 0), parked.Location); // Its right edge.
    }
}
