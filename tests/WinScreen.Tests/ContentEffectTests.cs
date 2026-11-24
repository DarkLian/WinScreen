using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class ContentShaderTests
{
    // A mistake in a shader only shows when Glass starts on a PC with a graphics card; compiling needs none.
    [Theory]
    [InlineData(nameof(ContentShaders.Stats))]
    [InlineData(nameof(ContentShaders.Composite))]
    [InlineData(nameof(ContentShaders.Backdrop))]
    public void Every_shader_compiles(string name)
    {
        string source = (string)typeof(ContentShaders).GetField(name)!.GetValue(null)!;
        Assert.False(Vortice.D3DCompiler.Compiler.Compile(source, name, name, "cs_5_0").IsEmpty);
    }
}

public sealed class KeptAreaTests
{
    private static readonly Size Window = new(1000, 700);

    [Fact]
    public void An_area_marked_comes_back_where_it_was()
    {
        foreach (Rectangle part in new[] { new Rectangle(0, 40, 1000, 600), new Rectangle(620, 500, 300, 150), new Rectangle(30, 20, 200, 100) })
            Assert.Equal(part, KeptArea.Of(part, Window, 1).In(Window, 1));
    }

    // A player's picture sits between a top bar and a bottom bar that keep their height: each edge goes with the side
    // it was nearer, so the area stays on the picture as the window grows.
    [Fact]
    public void Each_edge_keeps_its_distance_from_the_nearer_side_when_the_window_is_resized()
    {
        KeptArea picture = KeptArea.Of(Rectangle.FromLTRB(0, 40, 1000, 640), Window, 1);
        Assert.Equal(Rectangle.FromLTRB(0, 40, 1600, 1040), picture.In(new Size(1600, 1100), 1));

        KeptArea corner = KeptArea.Of(Rectangle.FromLTRB(700, 450, 980, 680), Window, 1);
        Assert.True(corner.LeftFromRight);
        Assert.True(corner.TopFromBottom);
        Assert.Equal(Rectangle.FromLTRB(1300, 850, 1580, 1080), corner.In(new Size(1600, 1100), 1));
    }

    [Fact]
    public void Areas_are_kept_at_100_percent_scaling_and_drawn_at_the_window_s_own()
    {
        KeptArea area = KeptArea.Of(new Rectangle(30, 60, 300, 150), new Size(1500, 1050), 1.5f);
        Assert.Equal(20, area.Left);
        Assert.Equal(40, area.Top);
        Assert.Equal(new Rectangle(20, 40, 200, 100), area.In(Window, 1));
    }

    [Fact]
    public void An_area_the_window_is_too_small_for_is_left_out()
    {
        KeptArea area = KeptArea.Of(Rectangle.FromLTRB(50, 50, 950, 650), Window, 1);
        Assert.True(area.In(new Size(80, 80), 1).IsEmpty);
    }

    [Fact]
    public void Saved_areas_are_tidied_and_found_by_program()
    {
        KeptAreaPreferences tidy = AppSettings.Parse("""
            {"KeptAreas":{"Apps":[
              {"Program":"VLC.exe","Areas":[{"Top":30},{"Top":30},{"Left":-5}]},
              {"Program":"vlc","Areas":[{"Top":1}]},
              {"Program":" ","Areas":[{"Top":1}]},
              {"Program":"edge","Areas":[]},
              null]}}
            """).KeptAreas;
        AppKeptAreas vlc = Assert.Single(tidy.Apps);
        Assert.Equal("VLC", vlc.Program);
        Assert.Equal(2, vlc.Areas.Count); // The same area twice is one; a negative edge becomes 0.
        Assert.Equal(2, tidy.For("vlc.EXE").Count);
        Assert.Empty(tidy.For("edge"));

        var many = new KeptAreaPreferences().With("code", Enumerable.Range(0, 20).Select(top => new KeptArea { Top = top }));
        Assert.Equal(KeptAreaPreferences.MostPerApp, many.For("code").Count);
        Assert.Empty(many.With("code", []).Apps);
        Assert.Empty(many.Without(["Code.exe"]).Apps);
    }

    [Fact]
    public void Marking_adds_a_dragged_part_and_a_click_on_a_marked_part_takes_it_away()
    {
        using var overlay = new KeptAreaOverlay(new Rectangle(0, 0, 800, 600), [new Rectangle(10, 10, 100, 100)], 2);
        overlay.Choose(new Rectangle(200, 200, 300, 150), new Point(500, 350));
        Assert.Equal(2, overlay.Areas.Count);
        overlay.Choose(new Rectangle(600, 400, 100, 100), new Point(700, 500)); // One too many: refused.
        Assert.Equal(2, overlay.Areas.Count);
        overlay.Choose(Rectangle.Empty, new Point(50, 50));
        Assert.Equal(new[] { new Rectangle(200, 200, 300, 150) }, overlay.Areas);
    }
}

public sealed class BackdropTests
{
    private static readonly Size Picture = new(1600, 900), Window = new(800, 600);

    [Theory]
    [InlineData(BackdropFit.Fill, -133.33f, 0, 1066.67f, 600)]
    [InlineData(BackdropFit.Fit, 0, 75, 800, 450)]
    [InlineData(BackdropFit.Stretch, 0, 0, 800, 600)]
    [InlineData(BackdropFit.Center, -400, -150, 1600, 900)]
    [InlineData(BackdropFit.Tile, 0, 0, 1600, 900)]
    public void Each_fit_places_the_picture_as_Windows_does_for_the_desktop(BackdropFit fit, float x, float y, float width, float height)
    {
        RectangleF place = BackdropChoice.Place(fit, Picture, Window);
        Assert.Equal(x, place.X, 0.01f);
        Assert.Equal(y, place.Y, 0.01f);
        Assert.Equal(width, place.Width, 0.01f);
        Assert.Equal(height, place.Height, 0.01f);
    }

    [Fact]
    public void Fill_always_covers_the_window_and_fit_always_shows_the_whole_picture()
    {
        foreach (Size window in new Size[] { new(300, 900), new(1920, 200), new(1600, 900) })
        {
            RectangleF fill = BackdropChoice.Place(BackdropFit.Fill, Picture, window);
            Assert.True(fill.Left <= 0.01f && fill.Top <= 0.01f && fill.Right >= window.Width - 0.01f && fill.Bottom >= window.Height - 0.01f);
            RectangleF fit = BackdropChoice.Place(BackdropFit.Fit, Picture, window);
            Assert.True(fit.Left >= -0.01f && fit.Top >= -0.01f && fit.Right <= window.Width + 0.01f && fit.Bottom <= window.Height + 0.01f);
            Assert.Equal(16 / 9f, fit.Width / fit.Height, 0.01f);
        }
    }

    [Fact]
    public void Every_app_has_no_background_and_no_sound_until_one_is_chosen()
    {
        BackgroundPreferences none = AppSettings.Parse("{}").Backgrounds;
        Assert.Null(none.For("chrome"));
        Assert.Empty(none.Apps);
        Assert.Equal(0, none.Everywhere.Volume);
    }

    [Fact]
    public void An_app_with_its_own_background_uses_it_and_every_other_app_uses_every_apps()
    {
        BackgroundPreferences chosen = AppSettings.Parse("""
            {"Backgrounds":{"Everywhere":{"File":"C:\\Pictures\\sea.mp4","Fit":3,"Volume":40},
             "Apps":[{"Program":"Code.exe","Background":{"File":"C:\\Pictures\\code.png","Fit":1}},
                     {"Program":"notepad","Background":{"File":""}}]}}
            """).Backgrounds;
        Assert.Equal(new BackdropSet([new BackdropChoice(@"C:\Pictures\sea.mp4", BackdropFit.Tile, 40)]), chosen.For("chrome"));
        // Names are matched as Windows reports a process: no ".exe", any case.
        Assert.Equal(new BackdropSet([new BackdropChoice(@"C:\Pictures\code.png", BackdropFit.Fit)]), chosen.For("code"));
        Assert.Equal("Code", chosen.Apps[0].Program);
        // Listed with no file: none, whatever every app has.
        Assert.Null(chosen.For("Notepad"));
    }

    [Fact]
    public void Settings_from_elsewhere_are_tidied_without_losing_the_rest()
    {
        BackgroundPreferences tidied = AppSettings.Parse("""
            {"Backgrounds":{"Everywhere":{"File":"  C:\\a.jpg ","Fit":42,"Volume":250},
             "Apps":[null,{"Program":" "},{"Program":"code","Background":{"File":"C:\\b.png","Fit":77,"Volume":-5}},
                     {"Program":"CODE.EXE","Background":{"File":"C:\\c.png"}},{"Program":"edge"}]}}
            """).Backgrounds;
        Assert.Equal(new BackdropSet([new BackdropChoice(@"C:\a.jpg", BackdropFit.Fill, 100)]), tidied.Everywhere.Backdrop);
        Assert.Equal(["code", "edge"], tidied.Apps.Select(app => app.Program));
        Assert.Equal(new BackdropSet([new BackdropChoice(@"C:\b.png", BackdropFit.Fill, 0)]), tidied.For("code"));
        Assert.Null(tidied.For("edge"));
    }

    [Fact]
    public void A_new_fit_or_volume_is_the_same_file_and_doesnt_start_a_video_over()
    {
        var playing = new BackdropChoice(@"C:\v\sea.mp4", BackdropFit.Fill, 0);
        Assert.True((playing with { Fit = BackdropFit.Tile, Volume = 60 }).SameFile(playing));
        Assert.True(new BackdropChoice(@"c:\V\SEA.mp4", BackdropFit.Fill).SameFile(playing));
        Assert.False(new BackdropChoice(@"C:\v\lake.mp4", BackdropFit.Fill).SameFile(playing));
        Assert.False(playing.SameFile(null));
    }

    // The owner, 23 Nov: several pictures and videos for one background, taking turns. The first stays where older
    // copies of WinScreen read it, so going back to one still shows that.
    [Fact]
    public void A_set_keeps_its_first_where_older_copies_read_it_and_the_rest_after()
    {
        var choice = new BackgroundChoice().WithItems([
            new BackgroundItem { File = @"C:\v\sea.mp4", Volume = 30, Speed = 150 },
            new BackgroundItem { File = @"C:\p\hill.png", Fit = BackdropFit.Fit, Brightness = 60 },
            new BackgroundItem { File = "https://example.com/clip.mp4" }]) with { Order = BackdropOrder.Shuffle, PictureMinutes = 2 };
        string saved = System.Text.Json.JsonSerializer.Serialize(new AppSettings { Backgrounds = new BackgroundPreferences { Everywhere = choice } });
        using (var json = System.Text.Json.JsonDocument.Parse(saved))
        {
            System.Text.Json.JsonElement everywhere = json.RootElement.GetProperty("Backgrounds").GetProperty("Everywhere");
            Assert.Equal(@"C:\v\sea.mp4", everywhere.GetProperty("File").GetString());
            Assert.Equal(30, everywhere.GetProperty("Volume").GetInt32());
            Assert.Equal(2, everywhere.GetProperty("More").GetArrayLength());
        }
        BackgroundChoice read = AppSettings.Parse(saved).Backgrounds.Everywhere;
        Assert.Equal(choice, read);
        BackdropSet set = read.Backdrop!;
        Assert.Equal(3, set.Items.Count);
        Assert.Equal((BackdropOrder.Shuffle, 2), (set.Order, set.Minutes));
        Assert.Equal(new BackdropChoice(@"C:\p\hill.png", BackdropFit.Fit, 0, 100, 60), set.Items[1]);
        Assert.True(set.HasSound);
        // Taking the first away moves the next up.
        BackgroundChoice fewer = read.WithItems(read.Items.Skip(1));
        Assert.Equal(@"C:\p\hill.png", fewer.File);
        Assert.Single(fewer.More);
        Assert.Null(read.WithItems([]).Backdrop);
        // Out of range from an edited file: the nearest that works.
        BackgroundChoice wild = AppSettings.Parse("""
            {"Backgrounds":{"Everywhere":{"File":"","Speed":9000,"PictureMinutes":0,"Order":7,
             "More":[null,{"File":" "},{"File":"C:\\x.png","Brightness":1,"Speed":1}]}}}
            """).Backgrounds.Everywhere;
        Assert.Equal(@"C:\x.png", wild.File);
        Assert.Equal((BackgroundItem.DimmestBrightness, BackgroundItem.SlowestSpeed), (wild.Brightness, wild.Speed));
        Assert.Equal((1, BackdropOrder.InOrder), (wild.PictureMinutes, wild.Order));
        Assert.Empty(wild.More);
    }

    [Fact]
    public void A_set_takes_turns_in_order_or_shuffled_with_none_twice_running()
    {
        var inOrder = new BackdropSequence(3, BackdropOrder.InOrder, new Random(1));
        Assert.Equal([0, 1, 2, 0, 1], Enumerable.Range(0, 5).Select(_ => inOrder.Next()));
        var shuffled = new BackdropSequence(4, BackdropOrder.Shuffle, new Random(7));
        int[] turns = [.. Enumerable.Range(0, 40).Select(_ => shuffled.Next())];
        for (int round = 0; round < 10; round++)
            Assert.Equal([0, 1, 2, 3], turns.Skip(round * 4).Take(4).Order());
        Assert.All(turns.Zip(turns.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        inOrder.Ask(0);
        Assert.Equal([0, 1, 2], new[] { inOrder.Next(), inOrder.Next(), inOrder.Next() }); // On from the one asked for.
        var one = new BackdropSequence(1, BackdropOrder.Shuffle, new Random(1));
        Assert.Equal([0, 0], new[] { one.Next(), one.Next() });
    }

    // Changing a fit, sound or speed carries on with what shows; a different set starts again.
    [Fact]
    public void Only_a_different_set_starts_over()
    {
        var set = new BackdropSet([new(@"C:\a.mp4", BackdropFit.Fill), new(@"C:\b.png", BackdropFit.Fill)]);
        Assert.True(set.SameItems(set with { Items = [new(@"c:\A.mp4", BackdropFit.Tile, 40, 200), new(@"C:\b.png", BackdropFit.Fit)] }));
        Assert.False(set.SameItems(set with { Order = BackdropOrder.Shuffle }));
        Assert.False(set.SameItems(set with { Switch = BackdropSwitch.Timed }));
    }

    // The owner, 24 Nov: like a media player, a video stays until it ends and a picture for its minutes; on a timer,
    // both for theirs. Alone, it stays.
    [Fact]
    public void A_set_goes_on_when_a_video_ends_or_on_a_timer()
    {
        var set = new BackdropSet([new(@"C:\a.mp4", BackdropFit.Fill), new(@"C:\b.png", BackdropFit.Fill)]);
        Assert.True(set.PlaysOnce);
        Assert.Equal(new long?[] { null, 300_000 }, set.Items.Select(set.StayMilliseconds));
        BackdropSet timed = set with { Switch = BackdropSwitch.Timed, Minutes = 2 };
        Assert.False(timed.PlaysOnce);
        Assert.Equal(new long?[] { 120_000, 120_000 }, timed.Items.Select(timed.StayMilliseconds));
        BackdropSet alone = set with { Items = [set.Items[0]] };
        Assert.False(alone.PlaysOnce);
        Assert.Null(alone.StayMilliseconds(alone.Items[0]));
        BackgroundChoice saved = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(new AppSettings
        {
            Backgrounds = new BackgroundPreferences { Everywhere = new BackgroundChoice { File = @"C:\a.mp4", Switch = BackdropSwitch.Timed } },
        })).Backgrounds.Everywhere;
        Assert.Equal(BackdropSwitch.Timed, saved.Switch);
        Assert.False(set.SameItems(set with { Items = [new(@"C:\b.png", BackdropFit.Fill), new(@"C:\a.mp4", BackdropFit.Fill)] }));
        Assert.Equal(set, new BackdropSet([new(@"C:\a.mp4", BackdropFit.Fill), new(@"C:\b.png", BackdropFit.Fill)]));
    }

    // The owner, 24 Nov: a picture is kept only as large as it can ever show, so no quality is lost; centred and tiled
    // ones show their own pixels and stay whole.
    [Theory]
    [InlineData(BackdropFit.Fill, 2560, 1440)]
    [InlineData(BackdropFit.Fit, 2560, 1440)]
    [InlineData(BackdropFit.Stretch, 2560, 1440)]
    [InlineData(BackdropFit.Center, 7680, 4320)]
    [InlineData(BackdropFit.Tile, 7680, 4320)]
    public void A_picture_is_kept_as_large_as_it_can_show(BackdropFit fit, int width, int height)
    {
        var room = new Size(2560, 1440);
        Assert.Equal(new Size(width, height), PictureBackdrop.Kept(new Size(7680, 4320), fit, room));
        Assert.Equal(new Size(800, 600), PictureBackdrop.Kept(new Size(800, 600), fit, room)); // Never enlarged.
        // Whatever the window, what is kept is never enlarged where the whole picture wouldn't be.
        foreach (Size picture in new[] { new Size(7680, 4320), new Size(6000, 900), new Size(900, 6000) })
            foreach (Size window in new[] { room, new Size(400, 1440), new Size(2560, 300), new Size(1000, 1000) })
            {
                Size kept = PictureBackdrop.Kept(picture, fit, room);
                RectangleF shown = BackdropChoice.Place(fit, kept, window), whole = BackdropChoice.Place(fit, picture, window);
                if (fit is BackdropFit.Center or BackdropFit.Tile) { Assert.Equal(whole, shown); continue; }
                Assert.True(shown.Width / kept.Width <= Math.Max(1.001f, whole.Width / picture.Width + 0.001f), $"{fit} {picture} in {window}: kept {kept}");
                Assert.True(shown.Height / kept.Height <= Math.Max(1.001f, whole.Height / picture.Height + 0.001f), $"{fit} {picture} in {window}: kept {kept}");
            }
    }

    // A link is a video unless its path ends as a picture's; a file that was moved or deleted is known before opening.
    [Fact]
    public void Links_and_missing_files_are_known_by_their_names()
    {
        Assert.True(new BackdropChoice("https://example.com/sea.mp4", BackdropFit.Fill).IsVideo);
        Assert.True(new BackdropChoice("https://example.com/live/stream", BackdropFit.Fill).IsVideo);
        Assert.False(new BackdropChoice("https://example.com/hill.JPG?size=big", BackdropFit.Fill).IsVideo);
        Assert.True(new BackdropChoice("https://example.com/hill.png", BackdropFit.Fill).IsLink);
        Assert.False(BackdropChoice.IsLinkText(@"C:\pictures\hill.png"));
        Assert.False(BackdropChoice.IsLinkText("file:///C:/hill.png"));
        Assert.True(new BackdropChoice(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), BackdropFit.Fill).IsMissing);
        Assert.False(new BackdropChoice("https://example.com/gone.png", BackdropFit.Fill).IsMissing);
        Assert.Throws<FileNotFoundException>(() => Backdrop.Open(new BackdropChoice(@"C:\nowhere\gone.mp4", BackdropFit.Fill)));
    }
}

public sealed class PageReaderTests
{
    private static TileStats[] Tiles(int dark, int light) =>
        [.. Enumerable.Repeat(new TileStats(0.05f, 0.05f, 0.05f, 1, false), dark),
            .. Enumerable.Repeat(new TileStats(1, 1, 1, 1, false), light)];

    [Fact]
    public void The_page_is_the_most_common_plain_colour()
    {
        var reader = new PageReader();
        reader.Read(Tiles(10, 90));
        Assert.Equal((1f, 1f, 1f), reader.Page);
        Assert.False(reader.AlreadyDark);
    }

    // Each pixel's see-through amount follows the page colour: a page about evenly split between two colours made it
    // swap from frame to frame while scrolling.
    [Fact]
    public void A_page_split_about_evenly_keeps_its_colour_until_another_clearly_takes_over()
    {
        var reader = new PageReader();
        reader.Read(Tiles(40, 60));
        reader.Read(Tiles(55, 45));
        Assert.Equal((1f, 1f, 1f), reader.Page);
        reader.Read(Tiles(70, 30));
        Assert.True(reader.Page.R < 0.1f, $"The page stayed {reader.Page}.");
    }

    // Dark leaves an already dark window as it is (the owner, 18 Nov: Dark flipped parts of a dark forum's art).
    [Fact]
    public void A_dark_page_is_already_dark_and_a_mid_grey_one_doesnt_switch_back_and_forth()
    {
        var reader = new PageReader();
        reader.Read(Tiles(90, 10));
        Assert.True(reader.AlreadyDark);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.25f, 0.25f, 0.25f, 1, false), 100)]);
        Assert.True(reader.AlreadyDark);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.35f, 0.35f, 0.35f, 1, false), 100)]);
        Assert.False(reader.AlreadyDark);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.25f, 0.25f, 0.25f, 1, false), 100)]);
        Assert.False(reader.AlreadyDark);
    }

    // Light or Dark text flips a page that is the other way round; a mid-grey page must not flip back and forth.
    [Fact]
    public void A_light_page_is_light_and_a_mid_grey_one_doesnt_switch_back_and_forth()
    {
        var reader = new PageReader();
        reader.Read(Tiles(10, 90));
        Assert.True(reader.Light);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.5f, 0.5f, 0.5f, 1, false), 100)]);
        Assert.True(reader.Light);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.4f, 0.4f, 0.4f, 1, false), 100)]);
        Assert.False(reader.Light);
        reader.Read([.. Enumerable.Repeat(new TileStats(0.5f, 0.5f, 0.5f, 1, false), 100)]);
        Assert.False(reader.Light);
        reader.Reset();
        Assert.False(reader.Light);
    }

    [Fact]
    public void Text_stays_as_it_is_with_its_shadow_until_chosen_and_each_app_keeps_its_own()
    {
        EffectPreferences effects = AppSettings.Parse("{}").Effects;
        Assert.Equal((GlassText.AsItIs, true), (effects.TextColor, effects.TextShadow));
        Assert.Equal((GlassText.AsItIs, true), (effects.Levels.TextColor, effects.Levels.Shadow));
        EffectPreferences chosen = AppSettings.Parse(
            @"{""Effects"":{""TextColor"":9,""Apps"":[{""Program"":""Chrome"",""TextColor"":1,""TextShadow"":false},{""Program"":""Notes""}]}}").Effects;
        Assert.Equal(GlassText.AsItIs, chosen.TextColor);
        Assert.Equal((GlassText.Light, false), (chosen.LevelsFor("chrome").TextColor, chosen.LevelsFor("chrome").Shadow));
        Assert.Equal((GlassText.AsItIs, true), (chosen.LevelsFor("notes").TextColor, chosen.LevelsFor("notes").Shadow));
        // An app's own levels start from every app's, text color and shadow included.
        AppGlassLevels fresh = (chosen with { TextColor = GlassText.Dark, TextShadow = false }).LevelsOf("paint");
        Assert.Equal((GlassText.Dark, false), (fresh.TextColor, fresh.TextShadow));
        string saved = System.Text.Json.JsonSerializer.Serialize(new AppSettings { Effects = chosen with { TextColor = GlassText.Dark } });
        Assert.Equal(GlassText.Dark, AppSettings.Parse(saved).Effects.TextColor);
    }

    [Fact]
    public void Settings_that_never_chose_get_layered_glass_and_a_choice_stays()
    {
        EffectPreferences effects = AppSettings.Parse("{}").Effects;
        Assert.True(effects.LayeredGlass); // The default since 15 Nov 2026, at the owner's choice.
        Assert.False(AppSettings.Parse("""{"Effects":{"LayeredGlass":false}}""").Effects.LayeredGlass);
        Assert.Equal(15, effects.BackgroundPercent);
        EffectPreferences wild = AppSettings.Parse("""{"Effects":{"BackgroundPercent":400,"TextPercent":-3}}""").Effects;
        Assert.Equal(100, wild.BackgroundPercent);
        Assert.Equal(20, wild.TextPercent);
        // The full range is the band Glass always had, so nobody's Glass changes on update.
        Assert.Equal(100, effects.FadeRangePercent);
        Assert.Equal(1f, effects.Levels.FadeRange);
        Assert.Equal(1, AppSettings.Parse("""{"Effects":{"FadeRangePercent":0}}""").Effects.FadeRangePercent);
        // Up to 650%, where every colour is in the band (the owner, 23 Nov).
        Assert.Equal(500, AppSettings.Parse("""{"Effects":{"FadeRangePercent":500}}""").Effects.FadeRangePercent);
        Assert.Equal(650, AppSettings.Parse("""{"Effects":{"FadeRangePercent":900}}""").Effects.FadeRangePercent);
        // Settings saved before Dark and Glass went pixel by pixel still load.
        Assert.True(AppSettings.Parse("""{"Effects":{"PictureSafeDark":false,"PicturePercent":50}}""").Effects.LayeredGlass);
    }

    [Fact]
    public void An_app_with_its_own_glass_levels_uses_them_and_every_other_app_the_main_ones()
    {
        EffectPreferences effects = AppSettings.Parse("""
            {"Effects":{"BackgroundPercent":20,"Apps":[
              {"Program":"Chrome.exe","BackgroundPercent":60,"TextPercent":5,"FadeRangePercent":0},
              {"Program":"chrome","BackgroundPercent":10},
              {"Program":" ","BackgroundPercent":10}]}}
            """).Effects;
        AppGlassLevels chrome = Assert.Single(effects.Apps);
        Assert.Equal("Chrome", chrome.Program);
        Assert.Equal((60, 20, 1), (chrome.BackgroundPercent, chrome.TextPercent, chrome.FadeRangePercent));
        Assert.Equal(0.6f, effects.LevelsFor("chrome").Background);
        Assert.Equal(0.2f, effects.LevelsFor("notepad").Background);
        // A new app starts from the main levels.
        Assert.Equal(20, effects.LevelsOf("notepad").BackgroundPercent);

        EffectPreferences changed = effects.With("CHROME", chrome with { BackgroundPercent = 30 });
        Assert.Equal(30, Assert.Single(changed.Apps).BackgroundPercent);
        Assert.Empty(changed.With("chrome.exe", null).Apps);
        Assert.Equal(60, Assert.Single(effects.Apps).BackgroundPercent); // The original stays as it was.
    }

    [Fact]
    public void A_window_set_on_its_own_goes_back_to_its_apps_levels_when_cleared()
    {
        using var effects = new ContentEffects(new WindowController([]));
        effects.SetLevels(_ => new ContentLevels(0.3f, 1));
        const nint window = 0x7FFF0001; // No such window: its program is unknown, and so it gets the main levels.
        Assert.Equal(new ContentLevels(0.3f, 1), effects.LevelsOf(window));
        effects.SetWindowLevels(window, new ContentLevels(0.5f, 0.8f, 0.2f));
        Assert.True(effects.HasOwnLevels(window));
        Assert.Equal(new ContentLevels(0.5f, 0.8f, 0.2f), effects.LevelsOf(window));
        effects.SetWindowLevels(window, null);
        Assert.False(effects.HasOwnLevels(window));
        Assert.Equal(new ContentLevels(0.3f, 1), effects.LevelsOf(window));
    }
}

[Trait("Category", "RealWindows")]
public sealed class ContentEffectTests
{
    private static readonly Rectangle Spot = new(300, 220, 420, 300);

    private static void Pump(Func<bool> until, int milliseconds = 3000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end && !until())
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static Color Middle(Rectangle bounds) =>
        Desktop.ColorOnScreen(new Rectangle(bounds.Left + bounds.Width / 2 - 5, bounds.Top + bounds.Height / 2 - 5, 10, 10));

    [GraphicsCardTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sent_to_the_bottom_an_effect_goes_down_with_its_window(bool dark)
    {
        using var other = new TargetProcess(new Rectangle(250, 180, 520, 400), "1E64C8");
        using var target = new TargetProcess(Spot, "E8E8E8");
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, dark ? ContentLook.Dark : ContentLook.Glass));
                Pump(() => false);
                windows.SendToBottom(target.Handle);
                Color blue = Color.FromArgb(0x1E, 0x64, 0xC8);
                Pump(() => Desktop.IsNear(Middle(bounds), blue, 12));
                Assert.True(Desktop.IsNear(Middle(bounds), blue, 12), $"{Middle(bounds)} rather than the window in front");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    // The border and the effect each want to be the one just above the window: the effect stays, the border goes above
    // it, and neither keeps moving. A pin another program takes comes back with the effect still on.
    [GraphicsCardTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pinned_window_under_an_effect_keeps_its_border_and_its_pin(bool dark)
    {
        using var target = new TargetProcess(Spot, "E8E8E8");
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            using var borders = new PinBorders { OverlayOf = effects.OverlayOf };
            borders.Configure(new() { ColorHex = "#E82020", Thickness = 6, OpacityPercent = 100 });
            try
            {
                Assert.True(windows.TogglePin(target.Handle));
                Assert.True(effects.Toggle(target.Handle, dark ? ContentLook.Dark : ContentLook.Glass));
                Assert.Equal([target.Handle], windows.VisiblePinnedWindows());
                borders.Show(windows.VisiblePinnedWindows());
                Assert.True(WindowInfo.TryGetFrameBounds(target.Handle, out Rectangle frame, out _));
                var edge = new Rectangle(frame.Left + frame.Width / 2 - 20, frame.Bottom - 5, 40, 3);
                bool Border() => Desktop.IsNear(Desktop.ColorOnScreen(edge), Color.FromArgb(0xE8, 0x20, 0x20), 12);
                Pump(Border);
                Assert.True(Border(), $"No border: {Desktop.ColorOnScreen(edge)}.");
                Pump(() => false, 500);
                int before = effects.RestacksOf(target.Handle);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int wrong = 0;
                Pump(() => { if (!Border()) wrong++; return clock.ElapsedMilliseconds > 2000; });
                Assert.True(effects.RestacksOf(target.Handle) - before <= 1, $"Restacked {effects.RestacksOf(target.Handle) - before} times in two seconds.");
                Assert.Equal(0, wrong);

                User32.SetWindowPos(target.Handle, User32.HWND_NOTOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
                windows.Refresh(new Point(bounds.Left + 50, bounds.Top + 50), opaqueOnHover: true, pausePinWhenMaximized: false);
                Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
                Assert.True(windows.IsDrawnByEffect(target.Handle));
                Assert.True(Desktop.Alpha(target.Handle) < 10, "The window itself shows through the effect.");
                Pump(() => Border() && (Desktop.ExStyle(effects.OverlayOf(target.Handle)) & User32.WS_EX_TOPMOST) != 0);
                Assert.True(Border(), "The border didn't come back with the pin.");
                Assert.NotEqual(0, Desktop.ExStyle(effects.OverlayOf(target.Handle)) & User32.WS_EX_TOPMOST);

                // Unpinned for real, not only in WinScreen's records.
                Assert.Equal(0, windows.UnpinEverywhere());
                Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
                Assert.Empty(windows.VisiblePinnedWindows());
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    [GraphicsCardFact]
    public void Pausing_pins_of_maximized_windows_works_under_an_effect()
    {
        using var target = new TargetProcess(Spot, "E8E8E8");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            bool Topmost(nint window) => (Desktop.ExStyle(window) & User32.WS_EX_TOPMOST) != 0;
            void Refresh() { windows.Refresh(Point.Empty, false, pausePinWhenMaximized: true); Pump(() => false, 300); }
            try
            {
                windows.TogglePin(target.Handle);
                Assert.True(effects.Toggle(target.Handle, ContentLook.Dark));
                User32.ShowWindow(target.Handle, 3);
                Refresh();
                Assert.False(Topmost(target.Handle));
                Assert.False(Topmost(effects.OverlayOf(target.Handle)));
                User32.ShowWindow(target.Handle, User32.SW_RESTORE);
                Desktop.Activate(target.Handle);
                // As WinScreen does four times a second: Windows can refuse the first try.
                Pump(() => { Refresh(); return Topmost(target.Handle) && Topmost(effects.OverlayOf(target.Handle)); });
                Assert.True(Topmost(target.Handle), $"Not pinned again; in front: {WindowInfo.Title(User32.GetForegroundWindow())}.");
                Assert.True(Topmost(effects.OverlayOf(target.Handle)));
                int before = effects.RestacksOf(target.Handle);
                Pump(() => false, 1000);
                Assert.True(effects.RestacksOf(target.Handle) - before <= 2, $"Restacked {effects.RestacksOf(target.Handle) - before} times in a second.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    [GraphicsCardFact]
    public void Turning_off_click_through_everywhere_reaches_a_window_under_an_effect()
    {
        using var target = new TargetProcess(Spot, "E8E8E8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(windows.ToggleClickThrough(target.Handle));
                Assert.True(effects.Toggle(target.Handle, ContentLook.Dark));
                Assert.NotEqual(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
                Assert.Equal(0, windows.TurnOffClickThroughEverywhere());
                Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
                Assert.False(windows.IsClickThrough(target.Handle));
                Assert.Equal(255, windows.OpacityOf(target.Handle));
                Assert.True(Desktop.Alpha(target.Handle) < 10, "The window itself shows through the effect.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    // Borderless and Fill display with Dark or Glass, in either order, and either one first off.
    [GraphicsCardTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void A_borderless_or_filled_window_takes_an_effect_in_either_order(bool dark, bool fill, bool effectFirst)
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.UseFrame();
        long style = User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE);
        Rectangle before = Desktop.BoundsOf(target.Handle);
        using var behind = new TargetProcess(Screen.PrimaryScreen!.WorkingArea, "1E64C8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            ContentLook look = dark ? ContentLook.Dark : ContentLook.Glass;
            Color Shown() => Middle(Desktop.BoundsOf(target.Handle));
            bool Drawn() => dark ? Desktop.IsNear(Shown(), Desktop.DarkPage) : Shown() is { R: > 45 and < 200 } shown && shown.B > shown.R + 20; // White page over blue, not the blue alone.
            bool Covered() => WindowInfo.TryGetFrameBounds(target.Handle, out Rectangle frame, out _) &&
                Desktop.BoundsOf(effects.OverlayOf(target.Handle)) == frame;
            void Check(string when)
            {
                Pump(() =>
                {
                    windows.Refresh(Point.Empty, false, false); // As WinScreen does four times a second.
                    Pump(() => false, 200);
                    return Drawn() && Covered();
                });
                Assert.True(Drawn(), $"{when}: {Shown()}.");
                Assert.True(Covered(), $"{when}: the effect is at {Desktop.BoundsOf(effects.OverlayOf(target.Handle))}, the window at {Desktop.BoundsOf(target.Handle)}.");
            }
            try
            {
                if (effectFirst) { Assert.True(effects.Toggle(target.Handle, look)); Check("The effect alone"); }
                Assert.True(windows.ToggleBorderless(target.Handle, fill));
                if (!effectFirst) Assert.True(effects.Toggle(target.Handle, look));
                Check("Both");
                Assert.True(windows.IsBorderless(target.Handle));
                if (fill) Assert.Equal(Screen.FromHandle(target.Handle).Bounds, Desktop.BoundsOf(target.Handle));
                Assert.Equal(0, User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE) & WindowController.BorderlessMask);

                if (effectFirst)
                {
                    Assert.False(windows.ToggleBorderless(target.Handle, fill));
                    Check("Its frame back");
                    effects.TurnAllOff();
                }
                else
                {
                    effects.TurnAllOff();
                    Pump(() => Desktop.IsNear(Shown(), Color.White, 12));
                    Assert.True(Desktop.IsNear(Shown(), Color.White, 12), $"The window itself: {Shown()}.");
                    Assert.True(windows.IsBorderless(target.Handle));
                    Assert.False(windows.ToggleBorderless(target.Handle, fill));
                }
                Pump(() => Desktop.BoundsOf(target.Handle) == before);
                Assert.Equal(before, Desktop.BoundsOf(target.Handle));
                Assert.Equal(style, User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE));
                Assert.Equal((byte)255, Desktop.Alpha(target.Handle) ?? 255);
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    [GraphicsCardFact]
    public void Glass_shows_what_is_behind_through_the_page_and_turning_it_off_restores_the_window()
    {
        using var behind = new TargetProcess(new Rectangle(250, 180, 520, 400), "1E64C8");
        using var target = new TargetProcess(Spot);
        User32.SetWindowPos(target.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.SetLevels(new ContentLevels(0.15f, 1));
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Assert.True(WindowInfo.IsConcealed(target.Handle));
                Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT); // Still clickable.
                Color expected = Color.FromArgb(0x1E + (int)((0xC8 - 0x1E) * 0.15), 0x64 + (int)((0x1E - 0x64) * 0.15), 0xC8 + (int)((0x1E - 0xC8) * 0.15));
                Pump(() => Desktop.IsNear(Middle(bounds), expected, 20));
                Assert.True(Desktop.IsNear(Middle(bounds), expected, 20), $"{Middle(bounds)} rather than {expected}");

                Assert.False(effects.Toggle(target.Handle, ContentLook.Glass));
                Assert.False(WindowInfo.IsConcealed(target.Handle));
                Pump(() => Desktop.IsNear(Middle(bounds), Color.FromArgb(0xC8, 0x1E, 0x1E)));
                Assert.True(Desktop.IsNear(Middle(bounds), Color.FromArgb(0xC8, 0x1E, 0x1E)), Middle(bounds).ToString());
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    /// <summary>
    /// Dark mode draws the window again, so it shows the window as see-through as the person made it, before or after
    /// turning Dark on; and making it more see-through under Dark leaves the hidden original hidden (the owner, 16 Nov).
    /// </summary>
    [GraphicsCardTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dark_mode_keeps_the_windows_see_through(bool seeThroughFirst)
    {
        using var behind = new TargetProcess(new Rectangle(250, 180, 520, 400), "1E64C8");
        using var target = new TargetProcess(Spot, "FFFFFF");
        User32.SetWindowPos(target.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                if (seeThroughFirst) windows.SetOpacity(target.Handle, 128);
                Assert.True(effects.Toggle(target.Handle, ContentLook.Dark));
                if (!seeThroughFirst) windows.SetOpacity(target.Handle, 128);
                Assert.True(WindowInfo.IsConcealed(target.Handle), "The original came out from under the effect.");
                // Half the dark page, half the blue window behind.
                Color expected = Color.FromArgb((25 + 0x1E) / 2, (25 + 0x64) / 2, (25 + 0xC8) / 2);
                Pump(() => Desktop.IsNear(Middle(bounds), expected, 24));
                Assert.True(Desktop.IsNear(Middle(bounds), expected, 24), $"{Middle(bounds)} rather than {expected}");
                Assert.Equal(128, windows.OpacityOf(target.Handle));
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    // A grey band on a white page: at the usual fade range it stays solid; at the widest, grey is halfway from the page
    // to black, so about half of what is behind shows through it.
    [GraphicsCardFact]
    public void The_widest_fade_range_fades_colours_far_from_the_page_too()
    {
        using var behind = new TargetProcess(new Rectangle(250, 180, 520, 400), "1E64C8");
        using var target = new TargetProcess(Spot, "FFFFFF,808080,FFFFFF");
        User32.SetWindowPos(target.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        Rectangle band = new(bounds.Left + bounds.Width / 2 - 5, bounds.Top + bounds.Height / 2 - 5, 10, 10);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.SetLevels(new ContentLevels(0, 1, 1, Shadow: false));
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Color grey = Color.FromArgb(0x80, 0x80, 0x80);
                Pump(() => Desktop.IsNear(Desktop.ColorOnScreen(band), grey, 20));
                Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(band), grey, 20), $"{Desktop.ColorOnScreen(band)} at 100%");

                effects.SetLevels(new ContentLevels(0, 1, WinScreen.Settings.EffectPreferences.MostFadeRange / 100f, Shadow: false));
                bool Faded() => Desktop.ColorOnScreen(band) is var now && now.B - now.R > 40;
                Pump(Faded);
                Assert.True(Faded(), $"{Desktop.ColorOnScreen(band)} at the widest: the blue window behind should show through the grey.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    // The owner, 23 Nov: a background file moved or deleted is passed over and named once; the next in the set shows,
    // and Brightness darkens the picture.
    [GraphicsCardFact]
    public void A_missing_file_is_passed_over_for_the_next_and_brightness_darkens_the_picture()
    {
        string folder = Path.Combine(Path.GetTempPath(), "WinScreen background " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string red = Path.Combine(folder, "red.png"), gone = Path.Combine(folder, "gone.png");
        using (var picture = new Bitmap(64, 64))
        {
            using (Graphics g = Graphics.FromImage(picture)) g.Clear(Color.FromArgb(0xD0, 0x20, 0x20));
            picture.Save(red, System.Drawing.Imaging.ImageFormat.Png);
        }
        using var target = new TargetProcess(Spot, "FFFFFF");
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]);
                using var effects = new ContentEffects(windows);
                var problems = new List<string>();
                effects.Problem += problems.Add;
                effects.SetLevels(new ContentLevels(0, 1, Shadow: false));
                BackdropSet Set(int brightness) => new([new(gone, BackdropFit.Fill), new(red, BackdropFit.Fill, Brightness: brightness)]);
                effects.SetBackgrounds(_ => Set(100));
                try
                {
                    Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                    Color bright = Color.FromArgb(0xD0, 0x20, 0x20);
                    Pump(() => Desktop.IsNear(Middle(bounds), bright, 24));
                    Assert.True(Desktop.IsNear(Middle(bounds), bright, 24), $"{Middle(bounds)} rather than the red picture");
                    Pump(() => problems.Count > 0);
                    Assert.Contains("gone.png", Assert.Single(problems));
                    Assert.Equal(red, effects.ShowingOf(target.Handle)?.File);

                    effects.SetBackgrounds(_ => Set(50));
                    Color dim = Color.FromArgb(0x68, 0x10, 0x10);
                    Pump(() => Desktop.IsNear(Middle(bounds), dim, 24));
                    Assert.True(Desktop.IsNear(Middle(bounds), dim, 24), $"{Middle(bounds)} rather than half as bright");
                }
                finally
                {
                    effects.TurnAllOff();
                    windows.RestoreAll();
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string ColourVideo(string folder, string name, Color colour)
    {
        string path = Path.Combine(folder, name);
        var size = new Size(320, 240);
        var frame = new byte[size.Width * size.Height * 4];
        for (int i = 0; i < frame.Length; i += 4) (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = (colour.B, colour.G, colour.R, 255);
        using var writer = WinScreen.Pip.VideoFileWriter.Open(path, size, 30, sound: false, softwareOnly: true, frame);
        for (int tick = 1; tick < writer.Rate; tick++) writer.Write(tick);
        writer.Finish();
        return path;
    }

    // The owner, 24 Nov: like a media player, the next shows when a video ends. Nothing plain shows in between, and
    // held still (a full-screen game) it stays where it is.
    [GraphicsCardFact]
    public void Videos_of_a_set_follow_one_another_when_each_ends()
    {
        if (!WinScreen.Pip.ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        string folder = Path.Combine(Path.GetTempPath(), "WinScreen background " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var target = new TargetProcess(Spot, "FFFFFF");
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                string red = ColourVideo(folder, "red.mp4", Color.FromArgb(0xC8, 0x1E, 0x1E)), blue = ColourVideo(folder, "blue.mp4", Color.FromArgb(0x1E, 0x1E, 0xC8));
                var windows = new WindowController([]);
                using var effects = new ContentEffects(windows);
                effects.SetLevels(new ContentLevels(0, 1, Shadow: false));
                effects.SetBackgrounds(_ => new BackdropSet([new(red, BackdropFit.Fill), new(Path.Combine(folder, "gone.mp4"), BackdropFit.Fill), new(blue, BackdropFit.Fill)]));
                try
                {
                    Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                    char Seen() => Middle(bounds) is var now && now.R > now.B + 80 ? 'r' : now.B > now.R + 80 ? 'b' : '-';
                    Pump(() => Seen() == 'r', 5000);
                    var order = new List<char>();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 5200)
                    {
                        Application.DoEvents();
                        char now = Seen();
                        if (order.Count == 0 || order[^1] != now) order.Add(now);
                        Thread.Sleep(5);
                    }
                    Assert.StartsWith("rbrb", new string([.. order]));
                    effects.Hold(true);
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
                    char held = Seen();
                    for (int i = 0; i < 130; i++)
                    {
                        Application.DoEvents();
                        Assert.Equal(held, Seen());
                        Thread.Sleep(20);
                    }
                    effects.Hold(false);
                    Pump(() => Seen() != held, 4000);
                    Assert.NotEqual(held, Seen());
                }
                finally
                {
                    effects.TurnAllOff();
                    windows.RestoreAll();
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    // Each time Glass was turned off, its graphics device and the driver's threads stayed until a garbage collection
    // came by: 5 GB and 500 threads in 100 s of turning effects on and off (found 24 Nov).
    [GraphicsCardTheory]
    [InlineData("Glass")]
    [InlineData("Dark")]
    [InlineData("Video")]
    public void Turning_an_effect_off_lets_its_graphics_device_go(string kind)
    {
        if (kind == "Video" && !WinScreen.Pip.ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        ContentLook look = kind == "Dark" ? ContentLook.Dark : ContentLook.Glass;
        string folder = Directory.CreateTempSubdirectory("WinScreen background ").FullName;
        using var target = new TargetProcess(Spot, "FFFFFF");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            if (kind == "Video") effects.SetBackgrounds(_ => new BackdropSet([new(ColourVideo(folder, "red.mp4", Color.Red), BackdropFit.Fill)]));
            int Threads() { using var self = System.Diagnostics.Process.GetCurrentProcess(); return self.Threads.Count; }
            void OnAndOff(int times)
            {
                for (int i = 0; i < times; i++)
                {
                    Assert.True(effects.Toggle(target.Handle, look));
                    for (int k = 0; k < 25; k++) { Application.DoEvents(); Thread.Sleep(20); }
                    effects.TurnOff(target.Handle);
                }
            }
            try
            {
                OnAndOff(2);
                int before = Threads();
                OnAndOff(10);
                Pump(() => Threads() < before + 12, 4000);
                Assert.True(Threads() < before + 12, $"{before} threads, then {Threads()} after ten more.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
        Directory.Delete(folder, true);
    }

    // A picture given as a link is fetched and shown; one that isn't there is named like a missing file.
    [GraphicsCardFact]
    public void A_picture_from_a_link_shows_and_a_dead_link_is_named()
    {
        using var listener = new System.Net.HttpListener();
        int port;
        using (var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { free.Start(); port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port; }
        string at = $"http://localhost:{port}/";
        listener.Prefixes.Add(at);
        listener.Start();
        byte[] png;
        using (var picture = new Bitmap(64, 64))
        {
            using (Graphics g = Graphics.FromImage(picture)) g.Clear(Color.FromArgb(0x20, 0xB0, 0x40));
            using var bytes = new MemoryStream();
            picture.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
            png = bytes.ToArray();
        }
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext asked;
                try { asked = await listener.GetContextAsync(); }
                catch (Exception stopped) when (stopped is System.Net.HttpListenerException or ObjectDisposedException) { return; }
                bool there = asked.Request.Url!.AbsolutePath == "/green.png";
                asked.Response.StatusCode = there ? 200 : 404;
                if (there) asked.Response.OutputStream.Write(png);
                asked.Response.Close();
            }
        });
        using var target = new TargetProcess(Spot, "FFFFFF");
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            var problems = new List<string>();
            effects.Problem += problems.Add;
            effects.SetLevels(new ContentLevels(0, 1, Shadow: false));
            effects.SetBackgrounds(_ => new BackdropSet([new(at + "gone.png", BackdropFit.Fill), new(at + "green.png", BackdropFit.Fill)]));
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Color green = Color.FromArgb(0x20, 0xB0, 0x40);
                Pump(() => Desktop.IsNear(Middle(bounds), green, 24) && problems.Count > 0, 8000);
                Assert.True(Desktop.IsNear(Middle(bounds), green, 24), $"{Middle(bounds)} rather than the linked picture");
                Assert.Contains("gone.png", Assert.Single(problems));
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
                listener.Stop();
            }
        });
        Assert.True(serving.Wait(5000));
    }

    // The owner, 24 Nov: the window's own panel changes its background live, for that window only, and shows the one of a
    // set that is being changed.
    [GraphicsCardFact]
    public void A_window_can_have_its_own_background_and_show_any_of_its_set()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-own-background-").FullName;
        string Picture(string name, Color color)
        {
            string file = Path.Combine(folder, name + ".png");
            using var picture = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(picture)) g.Clear(color);
            picture.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        Color red = Color.FromArgb(0xC0, 0x20, 0x20), green = Color.FromArgb(0x20, 0xB0, 0x40), blue = Color.FromArgb(0x20, 0x40, 0xC0);
        string r = Picture("red", red), g = Picture("green", green), b = Picture("blue", blue);
        using var target = new TargetProcess(Spot, "FFFFFF");
        using var other = new TargetProcess(new Rectangle(800, 220, 420, 300), "FFFFFF");
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var windows = new WindowController([]);
                using var effects = new ContentEffects(windows);
                effects.SetLevels(new ContentLevels(0, 1, Shadow: false));
                effects.SetBackgrounds(_ => new BackdropSet([new(r, BackdropFit.Fill)]));
                void Shows(TargetProcess window, Color color)
                {
                    Color Now() => Middle(Desktop.BoundsOf(window.Handle));
                    Pump(() => Desktop.IsNear(Now(), color, 16), 5000);
                    Assert.True(Desktop.IsNear(Now(), color, 16), $"{Now()} rather than {color}");
                }
                try
                {
                    Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                    Assert.True(effects.Toggle(other.Handle, ContentLook.Glass));
                    Shows(target, red);
                    effects.SetWindowBackground(target.Handle, new BackdropSet([new(g, BackdropFit.Fill), new(b, BackdropFit.Fill)]));
                    Shows(target, green);
                    Shows(other, red);
                    Assert.True(effects.HasOwnBackground(target.Handle));
                    effects.ShowBackground(target.Handle, 1);
                    Shows(target, blue);
                    Assert.Equal(1, effects.ShowingAt(target.Handle));
                    effects.ShowBackground(target.Handle, null);
                    Shows(target, green);
                    // Dimmed as it shows, without starting over.
                    effects.SetWindowBackground(target.Handle, new BackdropSet([new(g, BackdropFit.Fill, Brightness: 50), new(b, BackdropFit.Fill)]));
                    Shows(target, Color.FromArgb(0x10, 0x58, 0x20));
                    // A change to every app's leaves it alone; none at all is a choice too.
                    effects.SetBackgrounds(_ => new BackdropSet([new(b, BackdropFit.Fill)]));
                    Shows(other, blue);
                    Shows(target, Color.FromArgb(0x10, 0x58, 0x20));
                    effects.SetWindowBackground(target.Handle, new BackdropSet([]));
                    Pump(() => effects.BackdropOf(target.Handle) is null);
                    Assert.Null(effects.BackdropOf(target.Handle));
                    effects.SetWindowBackground(target.Handle, null);
                    Shows(target, blue);
                    Assert.False(effects.HasOwnBackground(target.Handle));
                }
                finally
                {
                    effects.TurnAllOff();
                    windows.RestoreAll();
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    // The owner, 23 Nov, after Lively: a background's sound plays only while its app is in front, unless the person
    // turns that off; WinScreen's own windows coming forward (its menu) change nothing.
    [GraphicsCardFact]
    public void A_backgrounds_sound_follows_its_app_to_the_front()
    {
        using var other = new TargetProcess(new Rectangle(700, 120, 300, 200), "1E64C8");
        using var target = new TargetProcess(Spot, "FFFFFF");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.SetBackgrounds(_ => new BackdropSet([new(@"C:\nowhere\sea.mp4", BackdropFit.Fill, 50)]));
            using var own = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(40, 40, 200, 120) };
            void Pump(Func<bool> until)
            {
                for (int i = 0; i < 150 && !until(); i++) { Application.DoEvents(); Thread.Sleep(20); }
            }
            try
            {
                Desktop.GainForegroundRights();
                Desktop.Activate(target.Handle);
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Assert.Equal(target.Handle, effects.Audible);
                Assert.True(effects.Hearing);
                own.Show();
                own.Activate();
                for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(20); }
                Assert.True(effects.Hearing, "WinScreen's own window came forward, and the sound stopped.");
                Desktop.GainForegroundRights();
                Desktop.Activate(other.Handle);
                Pump(() => !effects.Hearing);
                Assert.False(effects.Hearing);
                effects.SoundOnlyInFront = false;
                Assert.True(effects.Hearing);
                effects.SoundOnlyInFront = true;
                Desktop.GainForegroundRights();
                Desktop.Activate(target.Handle);
                Pump(() => effects.Hearing);
                Assert.True(effects.Hearing);
                effects.Hold(true);
                Assert.True(effects.Held);
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    // Light text on a white page with a black band: the page turns dark and see-through, the band light; the shadow
    // keeps a strip of the page solid beside the band, and without it that strip is as see-through as the rest.
    [GraphicsCardFact]
    public void Light_text_flips_a_light_page_and_the_shadow_can_be_turned_off()
    {
        using var behind = new TargetProcess(new Rectangle(250, 180, 520, 400), "1E64C8");
        using var target = new TargetProcess(Spot, "FFFFFF,000000,FFFFFF");
        User32.SetWindowPos(target.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        int bandLeft = bounds.Left + bounds.Width / 3;
        Rectangle page = new(bounds.Left + 20, bounds.Top + bounds.Height / 2 - 5, 30, 10);
        Rectangle band = new(bandLeft + bounds.Width / 6 - 5, bounds.Top + bounds.Height / 2 - 5, 10, 10);
        Rectangle beside = new(bandLeft - 3, bounds.Top + bounds.Height / 2 - 5, 2, 10);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.SetLevels(new ContentLevels(0.15f, 1, TextColor: GlassText.Light));
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                // The page flipped to #191919, 15% of it over the blue window behind.
                Color expected = Color.FromArgb((int)(25 * 0.15 + 0x1E * 0.85), (int)(25 * 0.15 + 0x64 * 0.85), (int)(25 * 0.15 + 0xC8 * 0.85));
                Pump(() => Desktop.IsNear(Desktop.ColorOnScreen(page), expected, 20));
                Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(page), expected, 20), $"Page {Desktop.ColorOnScreen(page)} rather than {expected}");
                Color text = Desktop.ColorOnScreen(band);
                Assert.True(text.GetBrightness() > 0.8f, $"The black band stayed {text}.");
                Color shadow = Desktop.ColorOnScreen(beside);
                Assert.True(shadow.B < 120, $"Beside the band {shadow}: no shadow.");

                effects.SetLevels(new ContentLevels(0.15f, 1, TextColor: GlassText.Light, Shadow: false));
                Pump(() => Desktop.ColorOnScreen(beside).B > 140);
                Color none = Desktop.ColorOnScreen(beside);
                Assert.True(Desktop.IsNear(none, expected, 30), $"Beside the band {none} rather than {expected} with the shadow off.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    [GraphicsCardFact]
    public void Dark_turns_a_light_page_dark_and_follows_the_window()
    {
        using var target = new TargetProcess(Spot, "F0F0F0");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Dark));
                Rectangle bounds = Desktop.BoundsOf(target.Handle);
                Pump(() => Middle(bounds).GetBrightness() < 0.2f);
                Assert.True(Middle(bounds).GetBrightness() < 0.2f, Middle(bounds).ToString());

                User32.SetWindowPos(target.Handle, 0, 500, 260, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                Rectangle moved = Desktop.BoundsOf(target.Handle);
                Pump(() => Middle(moved).GetBrightness() < 0.2f);
                Assert.True(Middle(moved).GetBrightness() < 0.2f, "The effect didn't follow the window.");
                Assert.True(effects.Has(target.Handle, ContentLook.Dark));
                Assert.False(effects.Has(target.Handle, ContentLook.Glass));

                // Choosing Glass on a dark window swaps the effect rather than stacking them.
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Assert.True(effects.Has(target.Handle, ContentLook.Glass));
                Assert.Equal(1, effects.Count);
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }

            Assert.False(WindowInfo.IsConcealed(target.Handle));
        });
    }

    // A browser's tooltips, menus and full-screen notice are popups it owns, which Windows keeps right above it. The
    // overlay kept trying to get between them and was told of each of its own tries: 1.2.0-beta.1 logged about 240
    // tries a second for a minute, and the gesture hook was dropped for the stalled thread.
    [GraphicsCardFact]
    public void Glass_settles_above_a_window_that_shows_a_popup_of_its_own()
    {
        using var target = new TargetProcess(Spot);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                Pump(() => false, 500);
                int before = effects.RestacksOf(target.Handle);
                nint popup = target.ShowPopup(new Point(Spot.Left + 60, Spot.Top + 60));
                Pump(() => false, 1500);
                int restacks = effects.RestacksOf(target.Handle) - before;
                Assert.True(restacks <= 3, $"The effect moved {restacks} times in a second and a half.");
                Assert.True(effects.Has(target.Handle, ContentLook.Glass));
                nint overlay = effects.OverlayOf(target.Handle);
                Assert.True(Desktop.IsAbove(popup, overlay), "The popup went under the effect.");
                Assert.True(Desktop.IsAbove(overlay, target.Handle), "The effect went under its window.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    [GraphicsCardFact]
    public void A_window_that_closes_takes_its_effect_with_it()
    {
        var target = new TargetProcess(Spot);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.Toggle(target.Handle, ContentLook.Glass);
            target.Dispose();
            Pump(() => !User32.IsWindow(target.Handle));
            effects.DropClosed();
            Assert.Equal(0, effects.Count);
        });
    }
}
