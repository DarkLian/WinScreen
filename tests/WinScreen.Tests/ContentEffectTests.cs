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

    [Fact]
    public void Older_settings_leave_both_effects_off()
    {
        EffectPreferences effects = AppSettings.Parse("{}").Effects;
        Assert.False(effects.LayeredGlass);
        Assert.False(effects.PictureSafeDark);
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
