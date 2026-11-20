using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

public class PipMappingTests
{
    // A view twice the size of the part it shows, on a screen, of a part away from the source's corner.
    private static readonly Rectangle Shown = new(5, 7, 50, 100);

    private static Rectangle ViewFor(ViewTurn turn) => new(new Point(10, 20), turn.Oriented(new Size(100, 200)));

    public enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    private static Point At(Rectangle r, Corner corner) => corner switch
    {
        Corner.TopLeft => new(r.Left, r.Top),
        Corner.TopRight => new(r.Right - 1, r.Top),
        Corner.BottomRight => new(r.Right - 1, r.Bottom - 1),
        _ => new(r.Left, r.Bottom - 1),
    };

    // The source corner shown at the view's top left, top right, bottom right and bottom left, worked out by hand.
    [Theory]
    [InlineData(0, false, Corner.TopLeft, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft)]
    [InlineData(1, false, Corner.BottomLeft, Corner.TopLeft, Corner.TopRight, Corner.BottomRight)]
    [InlineData(2, false, Corner.BottomRight, Corner.BottomLeft, Corner.TopLeft, Corner.TopRight)]
    [InlineData(3, false, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft, Corner.TopLeft)]
    [InlineData(0, true, Corner.TopRight, Corner.TopLeft, Corner.BottomLeft, Corner.BottomRight)]
    [InlineData(1, true, Corner.TopLeft, Corner.BottomLeft, Corner.BottomRight, Corner.TopRight)]
    [InlineData(2, true, Corner.BottomLeft, Corner.BottomRight, Corner.TopRight, Corner.TopLeft)]
    [InlineData(3, true, Corner.BottomRight, Corner.TopRight, Corner.TopLeft, Corner.BottomLeft)]
    public void Each_corner_of_the_view_reaches_the_source_corner_it_shows(int turns, bool mirrored,
        Corner topLeft, Corner topRight, Corner bottomRight, Corner bottomLeft)
    {
        var turn = new ViewTurn(turns, mirrored);
        var map = new PipMapping(ViewFor(turn), Shown, turn);
        Corner[] expected = [topLeft, topRight, bottomRight, bottomLeft];
        Corner[] corners = [Corner.TopLeft, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(At(Shown, expected[i]), map.ToSource(At(map.View, corners[i])));
            // Outside the picture, a point is kept to its nearest edge, as upright.
            Point outside = At(map.View, corners[i]) + new Size(corners[i] is Corner.TopRight or Corner.BottomRight ? 30 : -30,
                corners[i] is Corner.BottomLeft or Corner.BottomRight ? 30 : -30);
            Assert.Equal(At(Shown, expected[i]), map.ToSource(outside));
            Rectangle back = map.ToView(new Rectangle(At(Shown, expected[i]), new Size(1, 1)));
            Assert.True(back.Contains(At(map.View, corners[i])), $"{turn} {corners[i]}: {back}");
        }
        Assert.Equal(Shown, map.SelectionToSource(map.View));
        Assert.Equal(map.View, map.ToView(Shown));
    }

    [Fact]
    public void Upright_is_exactly_what_it_was()
    {
        var view = new Rectangle(3, 4, 333, 177);
        var shown = new Rectangle(11, 13, 1280, 683);
        var map = new PipMapping(view, shown);
        for (int y = view.Top - 5; y < view.Bottom + 5; y += 7)
            for (int x = view.Left - 5; x < view.Right + 5; x += 5)
                Assert.Equal(PipGeometry.PointToSource(new Point(x, y), view, shown), map.ToSource(new Point(x, y)));
        var selection = new Rectangle(40, 30, 120, 77);
        Assert.Equal(PipGeometry.SelectionToSource(selection, view, shown), map.SelectionToSource(selection));
        var caret = new Rectangle(500, 200, 1, 22);
        Assert.Equal(InputMethodWindows.Mapped(caret, view, shown), InputMethodWindows.Mapped(caret, map));
        Assert.Equal(new Size(3, -4), map.DirectionToSource(new Size(3, -4)));
    }

    [Fact]
    public void A_selection_in_a_view_turned_a_quarter_comes_back_upright_and_normalized()
    {
        var turn = new ViewTurn(1, false);
        var map = new PipMapping(ViewFor(turn), Shown, turn);
        // The left half of a view turned clockwise shows the source's bottom half.
        Rectangle left = new(map.View.Left, map.View.Top, map.View.Width / 2, map.View.Height);
        Assert.Equal(new Rectangle(Shown.Left, Shown.Top + 50, 50, 50), map.SelectionToSource(left));
        Assert.Equal(Rectangle.Empty, map.SelectionToSource(new Rectangle(0, 0, 5, 5)));
    }

    [Fact]
    public void A_caret_in_a_turned_view_becomes_the_rectangle_around_it()
    {
        var turn = new ViewTurn(1, false);
        var map = new PipMapping(ViewFor(turn), Shown, turn);
        // An upright caret at the source's top left, one wide and ten high, lies across the view's top right.
        Rectangle line = InputMethodWindows.Mapped(new Rectangle(Shown.Left, Shown.Top, 1, 10), map);
        Assert.Equal(map.View.Top, line.Top);
        Assert.Equal(map.View.Right, line.Right);
        Assert.True(line.Width > line.Height, line.ToString());
        Assert.Equal(new Size(0, -1), map.DirectionToSource(new Size(1, 0)));
    }
}
