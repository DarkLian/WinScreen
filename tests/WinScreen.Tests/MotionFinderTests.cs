using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Finding the largest moving picture in a window from tile brightness (roadmap step 8.2).</summary>
public sealed class MotionFinderTests
{
    private static readonly Size Surface = new(640, 360); // 80 by 45 tiles.

    /// <summary>A frame where the given areas, in pixels, show a pattern that changes with <paramref name="frame"/>.</summary>
    private static FrameSample Frame(int frame, params Rectangle[] moving)
    {
        int columns = Surface.Width / FrameSample.Tile, rows = Surface.Height / FrameSample.Tile;
        var brightness = new byte[columns * rows];
        Array.Fill(brightness, (byte)200);
        foreach (Rectangle area in moving)
            for (int y = area.Top / FrameSample.Tile; y < area.Bottom / FrameSample.Tile; y++)
                for (int x = area.Left / FrameSample.Tile; x < area.Right / FrameSample.Tile; x++)
                    brightness[y * columns + x] = (byte)((x * 37 + y * 11 + frame * 53) % 200 + 20);
        return new FrameSample(Surface, columns, rows, brightness);
    }

    private static Rectangle? Find(int frames, Func<int, FrameSample> make)
    {
        var finder = new MotionFinder();
        for (int i = 0; i < frames; i++) finder.Add(make(i));
        return finder.Largest();
    }

    [Fact]
    public void A_playing_area_is_found_to_the_tile()
    {
        var video = new Rectangle(80, 40, 320, 184);
        Assert.Equal(video, Find(10, i => Frame(i, video)));
    }

    [Fact]
    public void The_largest_of_two_wins_and_small_or_still_changes_are_ignored()
    {
        Rectangle small = new(16, 16, 120, 80), large = new(200, 120, 400, 224), blinking = new(560, 16, 16, 24);
        Assert.Equal(large, Find(10, i => Frame(i, small, large, blinking)));
        Assert.Null(Find(10, i => Frame(i, blinking)));
        Assert.Null(Find(10, _ => Frame(0, large))); // Shown but paused: nothing moves.
        Assert.Null(Find(1, i => Frame(i, large))); // One frame can't say.
    }

    [Fact]
    public void A_scene_with_still_patches_is_still_one_picture()
    {
        var video = new Rectangle(80, 40, 320, 184);
        // Two tiles in every five rows stay still, like a dark corner of a scene.
        Rectangle? found = Find(10, i =>
        {
            FrameSample sample = Frame(i, video);
            for (int y = 7; y < 28; y += 5) sample.Brightness[y * 80 + 20] = sample.Brightness[y * 80 + 21] = 200;
            return sample;
        });
        Assert.Equal(video, found);
    }

    [Fact]
    public void A_window_that_changed_size_starts_again()
    {
        var finder = new MotionFinder();
        var video = new Rectangle(80, 40, 320, 184);
        for (int i = 0; i < 5; i++) finder.Add(Frame(i, video));
        finder.Add(new FrameSample(new Size(320, 200), 40, 25, new byte[1000]));
        Assert.Equal(0, finder.Comparisons);
        Assert.Null(finder.Largest());
    }

    [Fact]
    public void Brightness_is_read_from_four_points_in_each_tile()
    {
        var pixels = new byte[16 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 8; x < 16; x++)
                pixels[(y * 16 + x) * 4 + 0] = pixels[(y * 16 + x) * 4 + 1] = pixels[(y * 16 + x) * 4 + 2] = 255;
        FrameSample sample;
        unsafe { fixed (byte* start = pixels) sample = FrameSample.From(start, 16 * 4, new Size(16, 8)); }
        Assert.Equal(2, sample.Columns);
        Assert.Equal(1, sample.Rows);
        Assert.Equal(0, sample.Brightness[0]);
        Assert.True(sample.Brightness[1] >= 254);
    }

    [Theory]
    [InlineData(0, 0, 100, 100, 0, 0, 100, 100, 1.0)]
    [InlineData(0, 0, 100, 100, 200, 0, 100, 100, 0.0)]
    [InlineData(0, 0, 100, 100, 50, 0, 100, 100, 1.0 / 3)]
    public void Overlap_is_shared_area_over_joint_area(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh, double expected) =>
        Assert.Equal(expected, PipWindow.Overlap(new Rectangle(ax, ay, aw, ah), new Rectangle(bx, by, bw, bh)), 3);

    [Fact]
    public void A_found_video_keeps_its_crop_unless_it_grows_or_is_clearly_elsewhere_twice()
    {
        var crop = new Rectangle(100, 100, 400, 225);
        int elsewhere = 0;
        // Part of the video went still: the smaller area inside is not a reason to shrink.
        Assert.Null(PipWindow.NextCrop(crop, new Rectangle(150, 120, 200, 100), ref elsewhere));
        // A little off, as one look sees it: kept.
        Assert.Null(PipWindow.NextCrop(crop, new Rectangle(108, 100, 400, 225), ref elsewhere));
        // The player grew around it: follows.
        Assert.Equal(new Rectangle(80, 80, 600, 340), PipWindow.NextCrop(crop, new Rectangle(80, 80, 600, 340), ref elsewhere));
        // Somewhere else once is a passing animation; twice running is the video after a scroll.
        var moved = new Rectangle(100, 600, 400, 225);
        Assert.Null(PipWindow.NextCrop(crop, moved, ref elsewhere));
        Assert.Equal(moved, PipWindow.NextCrop(crop, moved, ref elsewhere));
        // Seen back in place between two such looks: starts counting again.
        elsewhere = 0;
        Assert.Null(PipWindow.NextCrop(crop, moved, ref elsewhere));
        Assert.Null(PipWindow.NextCrop(crop, crop, ref elsewhere));
        Assert.Null(PipWindow.NextCrop(crop, moved, ref elsewhere));
    }

    [Fact]
    public void A_calm_scene_that_changes_only_now_and_then_is_still_found()
    {
        var video = new Rectangle(80, 40, 320, 184);
        // Each tile changes only every fourth frame, at different times.
        Rectangle? found = Find(20, i =>
        {
            FrameSample sample = Frame(0, video);
            FrameSample moving = Frame(i, video);
            for (int k = 0; k < sample.Brightness.Length; k++)
                if ((k + i) % 4 == 0) sample.Brightness[k] = moving.Brightness[k];
            return sample;
        });
        Assert.Equal(video, found);
    }

    /// <summary>A player of one still shade on a page of another, with only part of the picture moving.</summary>
    private static FrameSample Player(int frame, byte page, byte player, Rectangle box, Rectangle moving)
    {
        FrameSample sample = Frame(frame, moving);
        int columns = sample.Columns;
        for (int y = 0; y < sample.Rows; y++)
            for (int x = 0; x < columns; x++)
            {
                var tile = new Point(x * FrameSample.Tile, y * FrameSample.Tile);
                if (!moving.Contains(tile)) sample.Brightness[y * columns + x] = box.Contains(tile) ? player : page;
            }
        return sample;
    }

    [Theory]
    [InlineData(200, 30)] // A dark player on a light page.
    [InlineData(15, 0)] // A black player on a dark page.
    public void Only_part_of_the_picture_moving_still_finds_the_whole_player(byte page, byte player)
    {
        var box = new Rectangle(80, 40, 400, 224);
        var face = new Rectangle(200, 104, 160, 96);
        Assert.Equal(box, Find(10, i => Player(i, page, player, box, face)));
    }

    [Fact]
    public void Movement_with_no_border_around_it_is_what_is_shown()
    {
        var face = new Rectangle(200, 104, 160, 96);
        Assert.Equal(face, Find(10, i => Player(i, 200, 200, Rectangle.Empty, face)));
    }

    /// <summary>
    /// A rich, picture-like player on a white page, still but for a patch in its middle, as real pixels: the picture
    /// finder's block for the player is what is shown, not just the patch.
    /// </summary>
    [Fact]
    public unsafe void Movement_inside_a_picture_gives_the_whole_picture()
    {
        var player = new Rectangle(96, 48, 384, 224);
        var patch = new Rectangle(240, 128, 96, 64);
        byte[] Pixels(int frame)
        {
            var pixels = new byte[Surface.Width * Surface.Height * 4];
            var still = new Random(7);
            var moving = new Random(100 + frame);
            for (int y = 0; y < Surface.Height; y++)
                for (int x = 0; x < Surface.Width; x++)
                {
                    int k = (y * Surface.Width + x) * 4;
                    var at = new Point(x, y);
                    if (patch.Contains(at)) { byte v = (byte)moving.Next(256); pixels[k] = v; pixels[k + 1] = (byte)(255 - v); pixels[k + 2] = (byte)moving.Next(256); }
                    else if (player.Contains(at)) { pixels[k] = (byte)still.Next(40, 200); pixels[k + 1] = (byte)still.Next(60, 220); pixels[k + 2] = (byte)still.Next(20, 180); }
                    else { pixels[k] = pixels[k + 1] = pixels[k + 2] = 255; }
                    pixels[k + 3] = 255;
                }
            return pixels;
        }
        var finder = new MotionFinder();
        for (int i = 0; i < 10; i++)
        {
            byte[] pixels = Pixels(i);
            fixed (byte* start = pixels) finder.Add(FrameSample.From(start, Surface.Width * 4, Surface, pictures: i == 9));
        }
        Assert.Equal(player, finder.Largest());
    }
}
