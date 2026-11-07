using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

public class ToolbarLayoutTests
{
    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Every_available_button_gets_a_place_before_the_toolbar_is_ever_shown()
    {
        OnUiThread(() =>
        {
            const int buttons = 13, size = 34;
            using var toolbar = new WindowToolbar(_ => true, (_, _) => false, _ => true, (_, _) => { });
            Assert.True(toolbar.ClientSize.Width >= buttons * size);
            Assert.Equal(size, toolbar.ClientSize.Height);
        });
    }

    [Fact]
    public void Commands_that_are_off_leave_no_gap_in_the_toolbar()
    {
        OnUiThread(() =>
        {
            using var all = new WindowToolbar(_ => true, (_, _) => false, _ => true, (_, _) => { });
            using var some = new WindowToolbar(_ => true, (_, _) => false,
                command => !Settings.CommandCatalog.IsExperimental(command), (_, _) => { });
            Assert.Equal(all.ClientSize.Width - 4 * 34, some.ClientSize.Width);
        });
    }
}

public class ToolbarGeometryTests
{
    private const int Reserved = 150;
    private const int Caption = 34;
    private static readonly Size Bar = new(466, 34);
    private static readonly Rectangle Work = new(0, 0, 1920, 1032);

    private static Rectangle Place(Rectangle window) =>
        new(ToolbarGeometry.Place(window, Bar, Work, Reserved, Caption), Bar);

    [Fact]
    public void A_wide_window_gets_the_bar_centred_on_its_title_bar()
    {
        var window = new Rectangle(100, 100, 1500, 500);
        Rectangle bar = Place(window);
        Assert.Equal(window.Top, bar.Top);
        Assert.Equal(window.Left + window.Width / 2, bar.Left + bar.Width / 2, 1.0);
    }

    [Fact]
    public void The_hover_zone_stays_within_reach_of_the_bar_so_it_cannot_flicker()
    {
        var window = new Rectangle(100, 100, 1500, 500);
        Rectangle bar = Place(window);
        Rectangle zone = ToolbarGeometry.HoverZone(window, bar, Work, Reserved, Caption);
        Assert.True(Rectangle.Inflate(bar, 12, 12).Contains(zone));
    }

    [Theory]
    [InlineData(1560, 100, 360, 300)]
    [InlineData(-200, 100, 400, 300)]
    [InlineData(0, 900, 300, 300)]
    public void The_bar_stays_on_screen(int left, int top, int width, int height)
    {
        Rectangle bar = Place(new Rectangle(left, top, width, height));
        Assert.True(Work.Contains(bar), bar.ToString());
    }

    [Fact]
    public void A_maximized_window_gets_a_whole_bar_at_the_top_of_the_screen()
    {
        Assert.Equal(0, Place(new Rectangle(-8, -8, 1936, 1048)).Top);
    }

    [Fact]
    public void A_narrow_window_keeps_its_own_buttons_uncovered()
    {
        var window = new Rectangle(0, 100, 360, 300);
        Rectangle bar = Place(window);
        Assert.Equal(window.Top + Caption, bar.Top);
    }

    [Fact]
    public void A_mid_sized_window_moves_the_bar_left_of_its_buttons_when_there_is_room()
    {
        var window = new Rectangle(1000, 100, 500, 300);
        Rectangle bar = Place(window);
        Assert.Equal(window.Top, bar.Top);
        Assert.True(bar.Right <= window.Right - Reserved);
    }
}
