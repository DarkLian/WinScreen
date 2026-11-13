using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed partial class AppCompatibilityTests
{
    /// <summary>
    /// A standard menu bar, as older desktop apps have (LTspice is one): a click on one of its menus through the view
    /// opens that menu where it can be used.
    /// </summary>
    [Fact]
    public void A_standard_menu_bar_opens_the_menu_clicked_in_a_view()
    {
        using var target = new TargetProcess(new Rectangle(120, 120, 300, 200));
        nint window = target.CreatePlainWindow(new Rectangle(160, 160, 700, 500), menu: true);
        User32.GetWindowThreadProcessId(window, out uint process);
        var log = new StringBuilder();
        try
        {
            Visit(window, (view, at) =>
            {
                nint bar = GetMenu(window);
                Assert.NotEqual(0, bar);
                Assert.True(GetMenuItemRect(window, bar, 1, out Rect item)); // "Edit", the second.
                Rectangle bounds = Desktop.BoundsOf(window);
                Point edit = new((item.Left + item.Right) / 2 - bounds.Left, (item.Top + item.Bottom) / 2 - bounds.Top);
                int before = Popups((int)process, window).Count;
                Click(at(edit));
                List<nint> menus = [];
                Assert.True(Desktop.WaitFor(() => (menus = Popups((int)process, window)).Count > before, 3000), "The menu didn't open.");
                Rectangle menu = Desktop.BoundsOf(menus[0]);
                log.AppendLine($"menu {menu} ({WindowInfo.ClassName(menus[0])})");
                Assert.False(WindowController.OutOfReach(menu, [.. Screen.AllScreens.Select(s => s.Bounds)]), $"The menu opened out of reach at {menu}.");
                Desktop.Press(Keys.Escape);
                Desktop.Press(Keys.Escape);
                Assert.True(Desktop.WaitFor(() => Popups((int)process, window).Count == before, 2000), "The menu didn't close.");
                Assert.Contains(PressOutcome.Delivered, view.PressOutcomes);
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally { output.WriteLine(log.ToString()); }
    }
}
