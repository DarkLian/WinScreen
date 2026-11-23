using WinScreen.Pip;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

// A view's picture as a bitmap (Copy picture, Freeze a copy, Copy text) and its covers, worked out without windows.
public sealed class ViewPictureTests
{
    private static Bitmap Patterned(int width, int height)
    {
        var picture = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) picture.SetPixel(x, y, Color.FromArgb(PipTurnRenderingTests.Pattern(x, y)));
        return picture;
    }

    public static TheoryData<int, bool> Turns()
    {
        var data = new TheoryData<int, bool>();
        foreach (int turns in Enumerable.Range(0, 4))
            foreach (bool mirrored in new[] { false, true }) data.Add(turns, mirrored);
        return data;
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void The_picture_is_the_shown_part_turned_as_the_view_turns_it_with_covers_black(int turns, bool mirrored)
    {
        var turn = new ViewTurn(turns, mirrored);
        using Bitmap whole = Patterned(41, 29);
        // The bitmap's corner is at (100, 50) in the source; the view shows a part of it, one cover inside, one outside.
        var origin = new Point(100, 50);
        var shown = new Rectangle(105, 53, 23, 17);
        var cover = new Rectangle(110, 60, 4, 3);
        using Bitmap picture = ViewPicture.Compose(whole, origin, shown, [cover, new Rectangle(0, 0, 5, 5)], turn)!;
        Assert.Equal(turn.Oriented(shown.Size), picture.Size);
        for (int y = 0; y < shown.Height; y++)
            for (int x = 0; x < shown.Width; x++)
            {
                Point at = turn.ToView(new Point(x, y), shown.Size);
                Point inSource = new(shown.X + x, shown.Y + y);
                int expected = cover.Contains(inSource) ? 0 : PipTurnRenderingTests.Pattern(inSource.X - origin.X, inSource.Y - origin.Y) & 0xFFFFFF;
                Assert.Equal(expected, picture.GetPixel(at.X, at.Y).ToArgb() & 0xFFFFFF);
            }
    }

    [Fact]
    public void A_part_beyond_the_frame_is_cut_to_it_and_none_at_all_gives_no_picture()
    {
        using Bitmap whole = Patterned(20, 10);
        using Bitmap? cut = ViewPicture.Compose(whole, Point.Empty, new Rectangle(15, 5, 20, 20), [], ViewTurn.Upright);
        Assert.Equal(new Size(5, 5), cut!.Size);
        Assert.Null(ViewPicture.Compose(whole, Point.Empty, new Rectangle(30, 30, 5, 5), [], ViewTurn.Upright));
    }

    [Fact]
    public void A_part_of_the_view_is_the_same_part_of_the_picture()
    {
        // A 200 x 100 picture area at (4, 4) showing a 1000 x 500 picture: five pixels a view pixel.
        Assert.Equal(new Rectangle(50, 100, 500, 250), ViewPicture.InPicture(new Rectangle(14, 24, 100, 50), new Rectangle(4, 4, 200, 100), new Size(1000, 500)));
        Assert.Equal(new Rectangle(0, 0, 1000, 500), ViewPicture.InPicture(new Rectangle(-10, -10, 400, 400), new Rectangle(4, 4, 200, 100), new Size(1000, 500)));
    }

    [Fact]
    public void A_blank_print_is_told_from_a_drawn_one()
    {
        using var blank = new Bitmap(50, 40);
        using (Graphics graphics = Graphics.FromImage(blank)) graphics.Clear(Color.Black);
        Assert.True(ViewPicture.Blank(blank));
        using Bitmap drawn = Patterned(50, 40);
        Assert.False(ViewPicture.Blank(drawn));
    }

    [Fact]
    public void Without_covers_the_picture_is_one_piece()
    {
        var pieces = ViewCovers.Pieces(new Rectangle(10, 10, 100, 50), new Rectangle(0, 0, 200, 100), []);
        Assert.Equal([(new Rectangle(10, 10, 100, 50), new Rectangle(0, 0, 200, 100))], pieces);
    }

    public static TheoryData<Rectangle[]> CoverSets() => new()
    {
        new[] { new Rectangle(40, 20, 10, 10) },
        new[] { new Rectangle(10, 10, 30, 50) },
        new[] { new Rectangle(0, 0, 400, 400) },
        new[] { new Rectangle(20, 15, 15, 30), new Rectangle(30, 30, 40, 10), new Rectangle(90, 50, 30, 30) },
        new[] { new Rectangle(20, 20, 10, 10), new Rectangle(60, 40, 10, 10), new Rectangle(200, 200, 5, 5) },
    };

    // Every source pixel outside the covers is in one piece, none inside one is, and the pieces' places in the view
    // cover it without gaps or overlaps outside the covers.
    [Theory]
    [MemberData(nameof(CoverSets))]
    public void Pieces_leave_out_exactly_the_covers_and_meet_edge_to_edge(Rectangle[] covers)
    {
        var source = new Rectangle(10, 10, 100, 50);
        var destination = new Rectangle(3, 7, 300, 150);
        var pieces = ViewCovers.Pieces(source, destination, covers);
        for (int y = source.Top; y < source.Bottom; y++)
            for (int x = source.Left; x < source.Right; x++)
            {
                int holding = pieces.Count(piece => piece.Source.Contains(x, y));
                bool covered = covers.Any(cover => cover.Contains(x, y));
                Assert.Equal(covered ? 0 : 1, holding);
            }
        var counts = new int[destination.Width, destination.Height];
        foreach ((Rectangle _, Rectangle place) in pieces)
            for (int y = place.Top; y < place.Bottom; y++)
                for (int x = place.Left; x < place.Right; x++) counts[x - destination.Left, y - destination.Top]++;
        Assert.True(counts.Cast<int>().All(count => count <= 1), "Pieces overlap in the view.");
        // Each piece is placed where its source is, at the view's scale.
        foreach ((Rectangle part, Rectangle place) in pieces)
        {
            Assert.Equal(destination.Left + (part.Left - source.Left) * 3, place.Left);
            Assert.Equal(destination.Top + (part.Top - source.Top) * 3, place.Top);
            Assert.Equal(part.Width * 3, place.Width);
            Assert.Equal(part.Height * 3, place.Height);
        }
    }

    [Fact]
    public void Words_are_joined_with_spaces_but_chinese_characters_are_not()
    {
        Assert.Equal("Hello world" + Environment.NewLine + "second line",
            PictureText.Joined([["Hello", "world"], ["second", "line"]]));
        Assert.Equal("画中画视图", PictureText.Joined([["画", "中", "画", "视", "图"]]));
        Assert.Equal("复制 WinScreen 画面，好", PictureText.Joined([["复", "制", "WinScreen", "画", "面", "，", "好"]]));
    }

    [Fact]
    public void Small_pictures_are_read_larger_but_never_past_the_recognizer_limit()
    {
        Assert.Equal(new Size(400, 200), PictureText.ReadingSize(new Size(200, 100), 10000));
        Assert.Equal(new Size(2000, 500), PictureText.ReadingSize(new Size(2000, 500), 10000));
        Assert.Equal(new Size(1000, 250), PictureText.ReadingSize(new Size(4000, 1000), 1000));
    }

    [Fact]
    public void Pictures_are_saved_with_screenshot_names_and_a_number_when_taken()
    {
        string folder = Path.Combine(Directory.CreateTempSubdirectory("winscreen-pictures-").FullName, "new folder");
        try
        {
            var copied = new List<Size>();
            var shelf = new PictureShelf
            {
                Saves = () => true, Folder = () => folder, PutPicture = picture => copied.Add(picture.Size),
                Now = () => new DateTime(2026, 11, 23, 16, 5, 9),
            };
            using var picture = new Bitmap(30, 20);
            (string? first, string? problem) = shelf.Keep(picture);
            (string? second, _) = shelf.Keep(picture);
            Assert.Null(problem);
            Assert.Equal(Path.Combine(folder, "Screenshot 2026-11-23 160509.png"), first);
            Assert.Equal(Path.Combine(folder, "Screenshot 2026-11-23 160509 (2).png"), second);
            using (var saved = new Bitmap(first!)) Assert.Equal(new Size(30, 20), saved.Size);
            Assert.Equal(2, copied.Count);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, true); }
    }

    [Fact]
    public void Copy_only_saves_nothing_and_a_folder_that_cant_be_written_still_copies()
    {
        int copies = 0;
        var only = new PictureShelf { Saves = () => false, Folder = () => throw new InvalidOperationException("never asked"), PutPicture = _ => copies++ };
        using var picture = new Bitmap(8, 8);
        Assert.Equal((null, null), only.Keep(picture));
        string file = Path.GetTempFileName();
        try
        {
            // A file where the folder should be.
            var blocked = new PictureShelf { Saves = () => true, Folder = () => file, PutPicture = _ => copies++ };
            (string? saved, string? problem) = blocked.Keep(picture);
            Assert.Null(saved);
            Assert.NotNull(problem);
            Assert.Equal(2, copies);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Windows_screenshots_folder_is_the_default()
    {
        string folder = PictureShelf.DefaultFolder();
        Assert.True(Path.IsPathRooted(folder));
        Assert.Equal(folder, new PictureShelf().FolderInUse);
        Assert.Equal(@"D:\Shots", new PictureShelf { Folder = () => @"D:\Shots" }.FolderInUse);
    }

    [Fact]
    public void Picture_choices_and_covers_in_presets_are_saved_and_read_back()
    {
        var settings = new AppSettings
        {
            PipViews = new PipViewPreferences { SavePictures = false, PictureFolder = @"  D:\Shots  " },
            PipPresets = [new PipPreset { Name = "Private", Covers = [new PipCropShares(0.1, 0.2, 0.3, 0.4), new PipCropShares(2, 0, 1, 1)] }],
        };
        AppSettings loaded = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.False(loaded.PipViews.SavePictures);
        Assert.Equal(@"D:\Shots", loaded.PipViews.PictureFolder);
        Assert.Equal([new PipCropShares(0.1, 0.2, 0.3, 0.4)], loaded.PipPresets[0].Covers);
        Assert.True(new AppSettings().PipViews.SavePictures);
        Assert.Null(new AppSettings().PipViews.PictureFolder);
        Assert.Null(AppSettings.Parse("""{ "PipPresets": [ { "Name": "Old" } ] }""").PipPresets[0].Covers);
    }

    [Fact]
    public void A_word_after_a_copy_shows_on_the_view_unless_it_is_busy()
    {
        Assert.Equal(PipChipState.Said, PipStateChip.Decide(false, PipSessionState.Live, false, false, said: PipChipState.Said));
        Assert.Equal(PipChipState.SavedFile, PipStateChip.Decide(true, PipSessionState.Live, false, false, said: PipChipState.SavedFile));
        Assert.Equal(PipChipState.None, PipStateChip.Decide(false, PipSessionState.Live, false, true, said: PipChipState.Said));
        Assert.True(PipStateChip.OffersAction(PipChipState.SavedFile));
        Assert.False(PipStateChip.OffersAction(PipChipState.Said));
    }
}
