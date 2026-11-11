using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class SubmenuPlacementTests
{
    /// <summary>
    /// A submenu opens against the edge of the menu it comes from, on either side. Windows left a few pixels between
    /// them; where another menu lay under that gap, crossing it chose that menu's row and closed the submenus on the way.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_submenu_opens_against_the_edge_of_its_menu(bool toTheRight)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var menu = new ContextMenuStrip();
            var outer = new ToolStripMenuItem("Outer");
            var inner = new ToolStripMenuItem("Inner");
            inner.DropDownItems.Add("Leaf");
            outer.DropDownItems.Add(inner);
            menu.Items.Add(outer);
            MenuRenderer.Apply(menu);
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            if (!toTheRight) outer.DropDownDirection = inner.DropDownDirection = ToolStripDropDownDirection.Left;
            menu.Show(toTheRight ? new Point(area.Left + 100, area.Top + 100) : new Point(area.Right - 400, area.Top + 100));
            outer.ShowDropDown();
            inner.ShowDropDown();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
            foreach ((ToolStripDropDown parent, ToolStripDropDown child) in new[] { ((ToolStripDropDown)menu, outer.DropDown), (outer.DropDown, inner.DropDown) })
            {
                Assert.True(child.Visible);
                Assert.True(Native.User32.GetWindowRect(parent.Handle, out Native.Rect from));
                Assert.True(Native.User32.GetWindowRect(child.Handle, out Native.Rect to));
                string where = $"parent {from.Left}..{from.Right}, submenu {to.Left}..{to.Right}";
                if (toTheRight) Assert.True(from.Right == to.Left, where);
                else Assert.True(from.Left == to.Right, where);
            }
            menu.Close();
        });
    }
}
