using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

public sealed class SavedViewTests
{
    private sealed class FakeView : SavedViews.IConcealable
    {
        public bool IsGone { get; set; }
        public int Conceals { get; private set; }
        public void Conceal() => Conceals++;
    }

    private static ProgramSetup Setup(string path = @"C:\Apps\one.exe") => new()
    {
        Program = "one", ExecutablePath = path, View = new PipPreset { Name = "one", LongSide = 500 },
    };

    [Fact]
    public void Only_the_first_window_of_a_program_gets_a_view_each_session()
    {
        var views = new SavedViews();
        ProgramSetup setup = Setup();
        Assert.True(views.ShouldOpen(setup, windowHasView: false));
        Assert.False(views.ShouldOpen(setup, windowHasView: true));
        views.Opened(setup);
        Assert.False(views.ShouldOpen(setup, windowHasView: false));
        Assert.False(views.ShouldOpen(Setup(@"c:\APPS\ONE.EXE"), windowHasView: false));
        Assert.True(views.ShouldOpen(Setup(@"C:\Apps\two.exe"), windowHasView: false));
        Assert.False(views.ShouldOpen(new ProgramSetup { Program = "none", Pinned = true }, windowHasView: false));
    }

    [Fact]
    public void A_keep_updating_source_hides_only_after_it_loses_the_foreground()
    {
        var views = new SavedViews();
        var view = new FakeView();
        views.ConcealOnLeaving(10, view);
        views.ConcealIfLeft(10);
        views.ConcealIfLeft(0); // Windows can report no foreground window in passing.
        Assert.Equal(0, view.Conceals);
        views.ConcealIfLeft(20);
        views.ConcealIfLeft(30);
        Assert.Equal(1, view.Conceals);
    }

    [Fact]
    public void A_closed_view_no_longer_waits_to_hide_its_source()
    {
        var views = new SavedViews();
        var view = new FakeView { IsGone = true };
        views.ConcealOnLeaving(10, view);
        views.ConcealIfLeft(20);
        Assert.Equal(0, view.Conceals);
    }

    [Fact]
    public void A_share_crop_follows_the_source_size_and_a_pixel_crop_still_works()
    {
        var preset = new PipPreset
        {
            Name = "crop", CropX = 100, CropY = 50, CropWidth = 200, CropHeight = 100,
            CropShares = PipCropShares.Of(new Rectangle(100, 50, 200, 100), new Size(800, 400)),
        };
        Assert.Equal(new Rectangle(200, 100, 400, 200), preset.CropFor(new Size(1600, 800)));
        Assert.Equal(new Rectangle(100, 50, 200, 100), preset.CropFor(Size.Empty));

        var older = new PipPreset { Name = "old", CropX = 100, CropY = 50, CropWidth = 200, CropHeight = 100 };
        Assert.Equal(new Rectangle(100, 50, 200, 100), older.CropFor(new Size(1600, 800)));
    }

    [Fact]
    public void Older_settings_load_unchanged_and_a_damaged_view_is_dropped()
    {
        AppSettings older = AppSettings.Parse("""{"SavedSetups":[{"Program":"one","Pinned":true,"Alpha":200}]}""");
        ProgramSetup setup = Assert.Single(older.SavedSetups);
        Assert.Null(setup.View);
        Assert.False(setup.ViewKeepsUpdating);

        AppSettings damaged = AppSettings.Parse("""{"SavedSetups":[{"Program":"one","Pinned":true,"View":"nonsense"},{"Program":"two","View":{"Name":"","LongSide":-5}}]}""");
        Assert.Equal(2, damaged.SavedSetups.Count);
        Assert.All(damaged.SavedSetups, each => Assert.Null(each.View));
        Assert.True(damaged.SavedSetups[0].Pinned);
    }

    [Fact]
    public void Bad_crop_shares_are_ignored()
    {
        var preset = new PipPreset { Name = "bad", CropShares = new(0.9, 0, 0.5, 0.5) };
        Assert.Null(preset.Validated()!.CropShares);
        Assert.Null(new PipPreset { Name = "bad", CropShares = new(double.NaN, 0, 0.5, 0.5) }.Validated()!.CropShares);
    }

    [Fact]
    public void A_saved_view_round_trips_with_its_setup()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-view-").FullName;
        try
        {
            string path = Path.Combine(folder, "settings.json");
            var settings = new AppSettings();
            ProgramSetup setup = Setup();
            setup.ViewKeepsUpdating = true;
            setup.View = new PipPreset
            {
                Name = "one", LongSide = 640, Device = @"\\.\DISPLAY9", Left = 0.25, Top = 0.5, OpacityPercent = 80,
                PositionLocked = true, CropShares = new(0.25, 0.25, 0.5, 0.5),
            };
            settings.SavedSetups.Add(setup);
            settings.Save(path);
            ProgramSetup loaded = Assert.Single(AppSettings.Load(path).SavedSetups);
            Assert.True(loaded.ChangesAnything);
            Assert.True(loaded.ViewKeepsUpdating);
            Assert.Equal(640, loaded.View!.LongSide);
            Assert.Equal(0.25, loaded.View.Left);
            Assert.Equal(80, loaded.View.OpacityPercent);
            Assert.True(loaded.View.PositionLocked);
            Assert.Equal(new PipCropShares(0.25, 0.25, 0.5, 0.5), loaded.View.CropShares);
        }
        finally { Directory.Delete(folder, true); }
    }
}
