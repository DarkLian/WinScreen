using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

public class PipGeometryTests
{
    private static readonly Rectangle Screen = new(0, 0, 320, 320);

    /// <summary>A 320x320 screen in 16 pixel cells, busy wherever the rectangles say.</summary>
    private static bool[,] Busy(params Rectangle[] parts)
    {
        var cells = new bool[20, 20];
        foreach (Rectangle part in parts)
        {
            for (int row = part.Top / 16; row < part.Bottom / 16; row++)
            {
                for (int column = part.Left / 16; column < part.Right / 16; column++)
                    cells[row, column] = true;
            }
        }

        return cells;
    }

    [Fact]
    public void A_view_on_plain_background_stays_put()
    {
        bool[,] busy = Busy(new Rectangle(0, 0, 160, 160));

        Assert.Null(PipGeometry.CalmerSpot(busy, Screen, new Rectangle(160, 160, 96, 96)));
    }

    [Fact]
    public void A_view_on_content_goes_to_the_nearest_plain_spot()
    {
        bool[,] busy = Busy(new Rectangle(0, 0, 160, 320));

        Point? spot = PipGeometry.CalmerSpot(busy, Screen, new Rectangle(32, 32, 96, 96));

        Assert.Equal(new Point(160, 32), spot);
    }

    [Fact]
    public void A_full_screen_of_content_leaves_the_view_where_it_is()
    {
        Assert.Null(PipGeometry.CalmerSpot(Busy(Screen), Screen, new Rectangle(32, 32, 96, 96)));
    }

    [Fact]
    public void The_spot_is_inside_a_work_area_that_does_not_start_at_zero()
    {
        var area = new Rectangle(1920, 40, 320, 320);
        bool[,] busy = Busy(new Rectangle(0, 0, 160, 320));

        Point? spot = PipGeometry.CalmerSpot(busy, area, new Rectangle(1952, 72, 100, 100));

        Assert.NotNull(spot);
        Assert.True(area.Contains(new Rectangle(spot.Value, new Size(100, 100))), $"The view went to {spot}.");
        Assert.True(spot.Value.X >= area.Left + 160);
    }

    [Fact]
    public void Text_and_pictures_count_as_content_and_plain_colour_does_not()
    {
        using var bitmap = new Bitmap(64, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.SteelBlue);
            graphics.FillRectangle(Brushes.White, 4, 4, 4, 4);
        }

        bool[,] busy = ScreenContent.BusyCells(bitmap);

        Assert.Equal(2, busy.GetLength(0));
        Assert.Equal(4, busy.GetLength(1));
        Assert.True(busy[0, 0]);
        Assert.False(busy[0, 3]);
        Assert.False(busy[1, 1]);
    }

    [Fact]
    public void Fit_height_keeps_the_source_shape()
    {
        Assert.Equal(244, PipGeometry.FitHeight(484, new Rectangle(0, 0, 800, 400)));
    }

    [Fact]
    public void Fit_height_never_goes_below_the_minimum()
    {
        Assert.Equal(PipGeometry.MinimumHeight, PipGeometry.FitHeight(200, new Rectangle(0, 0, 4000, 10)));
    }

    [Fact]
    public void View_area_sits_inside_the_border()
    {
        Assert.Equal(new Rectangle(2, 2, 476, 296), PipGeometry.ViewArea(new Size(480, 300)));
    }

    [Fact]
    public void Selection_maps_to_the_same_part_of_the_source()
    {
        var view = new Rectangle(2, 2, 400, 200);
        var source = new Rectangle(0, 0, 800, 400);
        Assert.Equal(new Rectangle(400, 0, 400, 400),
            PipGeometry.SelectionToSource(new Rectangle(202, 2, 200, 200), view, source));
    }

    [Fact]
    public void Selection_inside_a_crop_stays_relative_to_that_crop()
    {
        var view = new Rectangle(2, 2, 400, 200);
        var crop = new Rectangle(100, 50, 400, 200);
        Assert.Equal(new Rectangle(200, 100, 200, 100),
            PipGeometry.SelectionToSource(new Rectangle(102, 52, 200, 100), view, crop));
    }

    [Fact]
    public void Selection_past_the_edge_is_cut_to_the_visible_part()
    {
        var view = new Rectangle(2, 2, 400, 200);
        var source = new Rectangle(0, 0, 400, 200);
        Assert.Equal(new Rectangle(0, 0, 100, 100),
            PipGeometry.SelectionToSource(new Rectangle(-50, -50, 152, 152), view, source));
    }

    [Fact]
    public void A_point_in_the_view_maps_to_the_same_spot_in_the_source()
    {
        Assert.Equal(new Point(600, 200),
            PipGeometry.PointToSource(new Point(302, 102), new Rectangle(2, 2, 400, 200), new Rectangle(0, 0, 800, 400)));
    }

    [Fact]
    public void A_point_in_a_crop_maps_inside_that_crop()
    {
        Assert.Equal(new Point(200, 100),
            PipGeometry.PointToSource(new Point(102, 52), new Rectangle(2, 2, 200, 100), new Rectangle(100, 50, 200, 100)));
    }

    [Fact]
    public void The_real_window_lands_with_the_clicked_spot_under_the_cursor()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Point(500, 300), PipGeometry.PeekPosition(new Point(800, 450), new Point(300, 150), new Size(800, 600), area));
    }

    [Fact]
    public void The_real_window_stays_on_screen_near_an_edge()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Point(1120, 440), PipGeometry.PeekPosition(new Point(1900, 1030), new Point(300, 150), new Size(800, 600), area));
    }

    [Fact]
    public void Between_works_whichever_way_you_drag()
    {
        Assert.Equal(new Rectangle(10, 20, 30, 40), PipGeometry.Between(new Point(40, 60), new Point(10, 20)));
    }
}
