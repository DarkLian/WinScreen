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
        Assert.Equal(new BackdropChoice(@"C:\Pictures\sea.mp4", BackdropFit.Tile, 40), chosen.For("chrome"));
        // Names are matched as Windows reports a process: no ".exe", any case.
        Assert.Equal(new BackdropChoice(@"C:\Pictures\code.png", BackdropFit.Fit), chosen.For("code"));
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
        Assert.Equal(new BackdropChoice(@"C:\a.jpg", BackdropFit.Fill, 100), tidied.Everywhere.Backdrop);
        Assert.Equal(["code", "edge"], tidied.Apps.Select(app => app.Program));
        Assert.Equal(new BackdropChoice(@"C:\b.png", BackdropFit.Fill, 0), tidied.For("code"));
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
        Assert.Equal(100, AppSettings.Parse("""{"Effects":{"FadeRangePercent":500}}""").Effects.FadeRangePercent);
        // Settings saved before Dark and Glass went pixel by pixel still load.
        Assert.True(AppSettings.Parse("""{"Effects":{"PictureSafeDark":false,"PicturePercent":50}}""").Effects.LayeredGlass);
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
