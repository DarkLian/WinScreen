using WinScreen.Settings;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class SourceBookmarkTests
{
    [Fact]
    public void Regions_resolve_from_the_display_corner_and_layout_changes_require_preview()
    {
        var bookmark = new PipSourceBookmark { Name = "Reference", Device = "left", DisplayX = -1920,
            DisplayWidth = 1920, DisplayHeight = 1080, Region = new PipCropShares(.5, .25, .25, .5) };
        var same = bookmark.Resolve([new("left", new Rectangle(-1920, 0, 1920, 1080))])!;
        Assert.False(same.NeedsPreview);
        Assert.Equal(new Rectangle(960, 270, 480, 540), same.Region);
        var changed = bookmark.Resolve([new("left", new Rectangle(0, -2160, 3840, 2160))])!;
        Assert.True(changed.NeedsPreview);
        Assert.Equal(new Rectangle(1920, 540, 960, 1080), changed.Region);
        var missing = bookmark.Resolve([new("replacement", new Rectangle(0, 0, 1280, 720), true)])!;
        Assert.True(missing.NeedsPreview);
        Assert.Equal("replacement", missing.Display.Device);
        Assert.Equal(new Rectangle(640, 180, 320, 360), missing.Region);
        Assert.Null(bookmark.Resolve([]));
    }

    [Fact]
    public void Bookmarks_and_explicit_unlocked_layouts_survive_storage_and_bad_entries_are_isolated()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-bookmarks-").FullName;
        try
        {
            string path = Path.Combine(folder, "settings.json");
            new AppSettings { PipBookmarks = [new() { Name = "Desktop", Device = "display", DisplayWidth = 1920,
                DisplayHeight = 1080, View = new PipPreset { Name = "Layout", LongSide = 480, PositionLocked = false } }] }.Save(path);
            PipSourceBookmark loaded = Assert.Single(AppSettings.Load(path).PipBookmarks);
            Assert.Null(loaded.Region);
            Assert.False(loaded.View!.PositionLocked);
            var damaged = AppSettings.Parse("""{"DefaultOpacityPercent":55,"PipBookmarks":[null,42,{"Name":"bad"},{"Name":"valid","Device":"one","DisplayWidth":100,"DisplayHeight":80}]}""");
            Assert.Equal(55, damaged.DefaultOpacityPercent);
            Assert.Equal("valid", Assert.Single(damaged.PipBookmarks).Name);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Moving_displays_preserves_relative_position_with_dpi_size_changes_and_fits_oversize_windows()
    {
        Rectangle from = new(-1920, 0, 1920, 1080), to = new(0, -1200, 2560, 1440);
        var original = new Rectangle(-1440, 270, 960, 540);
        Rectangle moved = PlacementGeometry.FrameOnDisplay(original, from, to, new Size(1440, 810));
        Assert.Equal(new Rectangle(560, -885, 1440, 810), moved);
        Assert.True(to.Contains(moved));
        Rectangle small = new(-1280, -720, 1280, 720);
        Assert.Equal(small, PlacementGeometry.FrameOnDisplay(original, from, small, new Size(4000, 3000)));
    }
}
