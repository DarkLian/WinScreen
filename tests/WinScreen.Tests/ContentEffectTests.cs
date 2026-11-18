using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PictureFinderTests
{
    private const int Columns = 40, Rows = 30;

    /// <summary>A white page with a photo-like block: busy, many colours, never flat.</summary>
    private static TileStats[] Page(Rectangle picture, Func<int, int, TileStats?>? extra = null)
    {
        var tiles = new TileStats[Columns * Rows];
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                TileStats? special = extra?.Invoke(x, y);
                tiles[y * Columns + x] = special ?? (picture.Contains(x, y)
                    ? new TileStats((x * 37 % 100) / 100f, (y * 53 % 100) / 100f, ((x + y) * 29 % 100) / 100f, 0.05f, true)
                    : new TileStats(1, 1, 1, 1, false));
            }

        return tiles;
    }

    private static Rectangle Found(float[] mask)
    {
        int x0 = Columns, y0 = Rows, x1 = -1, y1 = -1;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
                if (mask[y * Columns + x] > 0.75f)
                {
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }

        return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }

    [Fact]
    public void A_picture_on_a_page_is_found_and_the_page_is_not()
    {
        var picture = new Rectangle(5, 4, 12, 8);
        float[] mask = new PictureFinder().Find(Page(picture), Columns, Rows);
        Assert.Equal(picture, Found(mask));
        Assert.Equal(PictureFinder.Panel, mask[0]); // Plain page is flat, so it's marked as a panel, never a picture.
    }

    [Fact]
    public void A_plain_bar_under_a_picture_stays_out_and_a_black_letterbox_comes_in()
    {
        var picture = new Rectangle(5, 6, 12, 8);
        TileStats? Extras(int x, int y) =>
            x >= 5 && x < 17 && (y == 4 || y == 5) ? new TileStats(0, 0, 0, 1, false) :        // Letterbox above.
            x >= 5 && x < 17 && (y == 14 || y == 15) ? new TileStats(0.13f, 0.13f, 0.13f, 1, false) : // Control bar below.
            null;
        float[] mask = new PictureFinder().Find(Page(picture, Extras), Columns, Rows);
        Assert.Equal(Rectangle.FromLTRB(5, 4, 17, 14), Found(mask));
    }

    /// <summary>
    /// For Dark, a dark bar or panel stays as it is, but one tile that is mostly the ink of a big bold letter isn't a
    /// dark part: it stayed dark on the darkened page, a smudge on the heading (the owner, 15 Nov).
    /// </summary>
    [Fact]
    public void A_dark_bar_stays_dark_and_one_bold_letter_does_not()
    {
        var ink = new TileStats(0.1f, 0.1f, 0.12f, 0.4f, false);
        TileStats? Parts(int x, int y) =>
            y is 0 or 1 ? new TileStats(0.12f, 0.12f, 0.12f, 1, false) :   // A title bar two tiles tall.
            y == 10 && x < 6 ? new TileStats(0.15f, 0.15f, 0.15f, 1, false) : // A thin toolbar, one tile tall.
            (x, y) == (20, 5) ? ink :                                          // The letter.
            (x, y) is (25, 8) or (26, 8) ? ink :                               // Two letters side by side.
            null;
        TileStats[] tiles = Page(Rectangle.Empty, Parts);
        float[] mask = new PictureFinder().Find(tiles, Columns, Rows);
        PictureFinder.MarkAlreadyDark(mask, tiles, Columns, Rows);
        Assert.Equal(PictureFinder.AlreadyDark, mask[0 * Columns + 7]);
        Assert.Equal(PictureFinder.AlreadyDark, mask[1 * Columns + 39]);
        Assert.Equal(PictureFinder.AlreadyDark, mask[10 * Columns + 3]);
        Assert.NotEqual(PictureFinder.AlreadyDark, mask[5 * Columns + 20]);
        Assert.NotEqual(PictureFinder.AlreadyDark, mask[8 * Columns + 25]);
    }

    [Fact]
    public void Text_and_small_things_are_not_pictures()
    {
        TileStats? Text(int x, int y) => y is 3 or 4 && x < 30 ? new TileStats(1, 1, 1, 0.7f, false) : null;
        float[] mask = new PictureFinder().Find(Page(new Rectangle(30, 20, 2, 2), Text), Columns, Rows);
        Assert.Equal(Rectangle.Empty, Found(mask));
    }

    [Fact]
    public void A_picture_that_fades_to_black_is_held_for_a_few_seconds()
    {
        long now = 0;
        var finder = new PictureFinder(() => now);
        var picture = new Rectangle(5, 4, 12, 8);
        finder.Find(Page(picture), Columns, Rows);
        TileStats? Faded(int x, int y) => picture.Contains(x, y) ? new TileStats(0.06f, 0.06f, 0.06f, 1, false) : null;
        now = 2000;
        Assert.Equal(picture, Found(finder.Find(Page(Rectangle.Empty, Faded), Columns, Rows)));
        now = 7001;
        Assert.Equal(Rectangle.Empty, Found(finder.Find(Page(Rectangle.Empty, Faded), Columns, Rows)));
    }

    // A forum's banner and the small picture in the sidebar beside it were joined by the thin frame between them, and
    // the sidebar's icons ran on below: one block whose box took in the dark panel under the banner, drawn solid black,
    // its height changing with the tile grid as the page scrolled (the owner, 18 Nov, on a forum's dark theme).
    [Fact]
    public void A_picture_joined_to_a_sidebar_by_a_frame_keeps_its_own_size()
    {
        var banner = new Rectangle(5, 6, 12, 8);
        var frame = new TileStats(0.3f, 0.25f, 0.2f, 0.2f, false);
        // Text on a panel a shade off the page: not flat, so no plain bar to trim, and not page, so taken in solid.
        var panel = new TileStats(0.95f, 0.95f, 0.95f, 0.7f, false);
        TileStats Busy(int x, int y) => new((x * 37 % 100) / 100f, (y * 53 % 100) / 100f, ((x + y) * 29 % 100) / 100f, 0.05f, true);
        TileStats? Parts(int x, int y) =>
            x == 17 && y >= 6 && y < 19 ? frame :                                   // The frame between them.
            x >= 18 && x < 22 && y >= 6 && y < 10 ? Busy(x, y) :                    // The sidebar's picture.
            x >= 18 && x < 22 && y >= 10 && y < 19 ? Busy(x, y) :                   // Its icons, running on down.
            x >= 3 && x < 17 && y >= 14 && y < 24 ? panel :                         // The posts under the banner.
            null;
        float[] mask = new PictureFinder().Find(Page(banner, Parts), Columns, Rows);
        Rectangle found = Found(mask);
        Assert.True(found.Contains(banner), $"The banner wasn't found whole: {found}.");
        for (int y = banner.Bottom; y < Rows; y++)
            for (int x = 3; x < 17; x++)
                Assert.True(mask[y * Columns + x] < 0.75f, $"The panel under the banner was taken in at {x},{y}.");
    }

    // On a dark page the fade can't be told from the page by colour, so it is held by time alone; a hold only ends
    // early when the page around it moves.
    [Fact]
    public void A_picture_that_fades_to_black_on_a_dark_page_is_held_while_the_page_stays()
    {
        long now = 0;
        var finder = new PictureFinder(() => now);
        var picture = new Rectangle(5, 4, 12, 8);
        var dark = new TileStats(0.06f, 0.06f, 0.06f, 1, false);
        TileStats? Text(int x, int y) => y == 20 && x < 30 ? new TileStats(0.06f, 0.06f, 0.06f, 0.6f, false) : null;
        TileStats? Page(int x, int y) => Text(x, y) ?? dark;
        finder.Find(PageWith(picture, Page), Columns, Rows);
        now = 2000;
        float[] mask = finder.Find(PageWith(Rectangle.Empty, Page), Columns, Rows);
        for (int y = picture.Top; y < picture.Bottom; y++)
            for (int x = picture.Left; x < picture.Right; x++)
                Assert.True(mask[y * Columns + x] >= PictureFinder.PerPixel, $"Tile {x},{y} let go of the faded picture.");
    }

    // Scrolling took a picture past the window's edge, and the place it was last seen stayed a solid block for five
    // seconds: under Glass on a dark page, a black box over whatever had scrolled in (the owner, 18 Nov).
    [Fact]
    public void A_picture_scrolled_away_leaves_nothing_behind()
    {
        long now = 0;
        var finder = new PictureFinder(() => now);
        var dark = new TileStats(0.06f, 0.06f, 0.06f, 1, false);
        // Lines of dense light text every third row, moving up with the page: too much ink to count as plain page.
        TileStats? Page(int scroll, int x, int y) =>
            (y + scroll) % 3 == 0 && x < 30 ? new TileStats(0.06f, 0.06f, 0.06f, 0.4f, false) : dark;
        for (int scroll = 0; scroll <= 12; scroll += 2)
        {
            int s = scroll;
            var picture = Rectangle.Intersect(new Rectangle(5, 6 - s, 12, 8), new Rectangle(0, 0, Columns, Rows));
            finder.Find(PageWith(picture.Height >= 4 ? picture : Rectangle.Empty, (x, y) => Page(s, x, y)), Columns, Rows);
            now += 16;
        }

        Assert.Equal(Rectangle.Empty, Found(finder.Find(PageWith(Rectangle.Empty, (x, y) => Page(12, x, y)), Columns, Rows)));
    }

    // A smooth scroll ends in steps of a pixel or two, which change text tiles only a little; holds from those last
    // steps stayed, solid blocks where a picture had just been, until the page changed again (the owner, 18 Nov).
    [Fact]
    public void The_last_small_steps_of_a_scroll_end_a_hold_too()
    {
        long now = 0;
        var finder = new PictureFinder(() => now);
        var picture = new Rectangle(5, 4, 12, 8);
        TileStats? Text(float exact, int x, int y) =>
            y % 2 == 0 ? new TileStats(0.95f, 0.95f, 0.95f, exact, false) : null; // Text on a panel a shade off the page.
        finder.Find(PageWith(picture, (x, y) => Text(0.6f, x, y)), Columns, Rows);
        now = 16;
        float[] mask = finder.Find(PageWith(Rectangle.Empty, (x, y) => Text(0.62f, x, y)), Columns, Rows);
        Assert.Equal(Rectangle.Empty, Found(mask));
    }

    private static TileStats[] PageWith(Rectangle picture, Func<int, int, TileStats?> page) =>
        Page(picture, (x, y) => picture.Contains(x, y) ? null : page(x, y));

    [Fact]
    public void Settings_that_never_chose_get_picture_keeping_dark_and_glass_and_a_choice_stays()
    {
        EffectPreferences effects = AppSettings.Parse("{}").Effects;
        Assert.True(effects.LayeredGlass); // The default since 15 Nov 2026, at the owner's choice.
        Assert.False(AppSettings.Parse("""{"Effects":{"LayeredGlass":false}}""").Effects.LayeredGlass);
        Assert.True(effects.PictureSafeDark); // The default since 14 Nov 2026, at the owner's choice.
        Assert.False(AppSettings.Parse("""{"Effects":{"PictureSafeDark":false}}""").Effects.PictureSafeDark);
        Assert.Equal(15, effects.BackgroundPercent);
        EffectPreferences wild = AppSettings.Parse("""{"Effects":{"BackgroundPercent":400,"TextPercent":-3}}""").Effects;
        Assert.Equal(100, wild.BackgroundPercent);
        Assert.Equal(20, wild.TextPercent);
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
            effects.SetLevels(new ContentLevels(0.15f, 1, 0.9f));
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
    public void Picture_safe_Dark_turns_a_light_page_dark_and_follows_the_window()
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
