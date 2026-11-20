using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

public class ViewTurnTests
{
    private static readonly Size View = new(5, 3);

    public static IEnumerable<object[]> States() =>
        from turns in Enumerable.Range(0, 4) from mirrored in new[] { false, true } select new object[] { turns, mirrored };

    private static IEnumerable<Point> Pixels(Size size) =>
        from y in Enumerable.Range(0, size.Height) from x in Enumerable.Range(0, size.Width) select new Point(x, y);

    [Fact]
    public void There_are_eight_states_and_each_command_cycles_back()
    {
        var reached = new HashSet<ViewTurn> { ViewTurn.Upright };
        var queue = new Queue<ViewTurn>(reached);
        while (queue.TryDequeue(out ViewTurn turn))
            foreach (ViewTurn next in new[] { turn.RotatedRight, turn.RotatedLeft, turn.FlippedAcross, turn.FlippedDown })
                if (reached.Add(next)) queue.Enqueue(next);
        Assert.Equal(8, reached.Count);
        foreach (ViewTurn turn in reached)
        {
            Assert.Equal(turn, turn.RotatedRight.RotatedRight.RotatedRight.RotatedRight);
            Assert.Equal(turn, turn.RotatedRight.RotatedLeft);
            Assert.Equal(turn, turn.FlippedAcross.FlippedAcross);
            Assert.Equal(turn, turn.FlippedDown.FlippedDown);
            Assert.Equal(turn.RotatedRight.RotatedRight, turn.FlippedAcross.FlippedDown);
        }
        Assert.Equal(new ViewTurn(3, false), new ViewTurn(-1, false));
        Assert.True(new ViewTurn(4, false).IsUpright);
    }

    // Where a pixel the person sees goes when the whole view turns or flips on screen, and the view's new size.
    [Theory]
    [MemberData(nameof(States))]
    public void Each_command_moves_what_is_shown_the_way_it_says(int turns, bool mirrored)
    {
        var turn = new ViewTurn(turns, mirrored);
        Size size = turn.Oriented(View);
        (ViewTurn After, Func<Point, Point> Moved, Size Size)[] commands =
        [
            (turn.RotatedRight, p => new Point(size.Height - 1 - p.Y, p.X), new Size(size.Height, size.Width)),
            (turn.RotatedLeft, p => new Point(p.Y, size.Width - 1 - p.X), new Size(size.Height, size.Width)),
            (turn.FlippedAcross, p => new Point(size.Width - 1 - p.X, p.Y), size),
            (turn.FlippedDown, p => new Point(p.X, size.Height - 1 - p.Y), size),
        ];
        foreach ((ViewTurn after, Func<Point, Point> moved, Size newSize) in commands)
        {
            Assert.Equal(newSize, after.Oriented(View));
            foreach (Point pixel in Pixels(size))
                Assert.Equal(turn.ToUpright(pixel, size), after.ToUpright(moved(pixel), newSize));
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Showing_and_reading_back_are_inverses_and_cover_every_pixel_once(int turns, bool mirrored)
    {
        var turn = new ViewTurn(turns, mirrored);
        Size viewSize = turn.Oriented(View);
        var seen = new HashSet<Point>();
        foreach (Point pixel in Pixels(viewSize))
        {
            Point upright = turn.ToUpright(pixel, viewSize);
            Assert.InRange(upright.X, 0, View.Width - 1);
            Assert.InRange(upright.Y, 0, View.Height - 1);
            Assert.True(seen.Add(upright));
            Assert.Equal(pixel, turn.ToView(upright, View));
        }
        Assert.Equal(View.Width * View.Height, seen.Count);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Shares_and_directions_agree_with_pixels(int turns, bool mirrored)
    {
        var turn = new ViewTurn(turns, mirrored);
        Size viewSize = turn.Oriented(View);
        foreach (Point pixel in Pixels(viewSize))
        {
            Point upright = turn.ToUpright(pixel, viewSize);
            PointF share = turn.ToUpright(new PointF((pixel.X + .5f) / viewSize.Width, (pixel.Y + .5f) / viewSize.Height));
            Assert.Equal((upright.X + .5f) / View.Width, share.X, 4);
            Assert.Equal((upright.Y + .5f) / View.Height, share.Y, 4);
        }
        var from = new Point(1, 1);
        foreach (Point to in Pixels(viewSize))
        {
            Point a = turn.ToUpright(from, viewSize), b = turn.ToUpright(to, viewSize);
            var step = new Size(to.X - from.X, to.Y - from.Y);
            Assert.Equal(new Size(b.X - a.X, b.Y - a.Y), turn.ToUpright(step));
            SizeF stepF = turn.ToUpright(new SizeF(step));
            Assert.Equal(b.X - a.X, stepF.Width);
            Assert.Equal(b.Y - a.Y, stepF.Height);
        }
    }

    [Fact]
    public void Only_a_quarter_or_three_quarter_turn_swaps_the_sides()
    {
        Assert.Equal(View, new ViewTurn(0, true).Oriented(View));
        Assert.Equal(new Size(3, 5), new ViewTurn(1, false).Oriented(View));
        Assert.Equal(View, new ViewTurn(2, false).Oriented(View));
        Assert.Equal(new Size(3, 5), new ViewTurn(3, true).Oriented(View));
        Assert.Equal(new Rectangle(7, 8, 3, 5), new ViewTurn(1, false).Oriented(new Rectangle(7, 8, 5, 3)));
    }
}
