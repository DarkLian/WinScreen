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
}
