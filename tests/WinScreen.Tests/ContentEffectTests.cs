using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

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
    public void The_picture_and_its_fit_are_kept_in_settings()
    {
        Assert.Null(AppSettings.Parse("{}").Effects.Backdrop);
        EffectPreferences chosen = AppSettings.Parse("""{"Effects":{"BackdropFile":"C:\\Pictures\\sea.mp4","BackdropFit":3}}""").Effects;
        Assert.Equal(new BackdropChoice(@"C:\Pictures\sea.mp4", BackdropFit.Tile), chosen.Backdrop);
        Assert.Equal(BackdropFit.Fill, AppSettings.Parse("""{"Effects":{"BackdropFit":42}}""").Effects.Validated().BackdropFit);
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
