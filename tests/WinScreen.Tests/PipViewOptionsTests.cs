using System.Text.Json;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipZoomTests
{
    [Fact]
    public void Zooming_keeps_the_spot_under_the_pointer()
    {
        var spot = new PointF(0.25f, 0.75f);
        RectangleF zoomed = PipGeometry.ZoomAround(PipGeometry.WholeView, spot, 2);
        Assert.Equal(0.5f, zoomed.Width, 3);
        Assert.Equal(0.25f, zoomed.X + spot.X * zoomed.Width, 3);
        Assert.Equal(0.75f, zoomed.Y + spot.Y * zoomed.Height, 3);
    }

    [Fact]
    public void Zoom_stops_at_the_whole_picture_and_at_eight_times()
    {
        Assert.Equal(PipGeometry.WholeView, PipGeometry.ZoomAround(PipGeometry.WholeView, new PointF(0.5f, 0.5f), 0.5));
        RectangleF view = PipGeometry.WholeView;
        for (int i = 0; i < 40; i++) view = PipGeometry.ZoomAround(view, new PointF(1, 1), 1.25);
        Assert.Equal(0.125f, view.Width, 3);
        Assert.Equal(1f, view.Right, 3);
    }

    [Fact]
    public void Panning_stops_at_the_edges()
    {
        var view = new RectangleF(0.25f, 0.25f, 0.5f, 0.5f);
        Assert.Equal(new RectangleF(0.5f, 0, 0.5f, 0.5f), PipGeometry.Pan(view, 1, -1));
    }

    [Fact]
    public void A_zoomed_part_stays_inside_the_cropped_part()
    {
        var part = new Rectangle(100, 50, 400, 300);
        Assert.Equal(part, PipGeometry.ZoomedPart(part, PipGeometry.WholeView));
        Assert.Equal(new Rectangle(300, 200, 200, 150),
            PipGeometry.ZoomedPart(part, new RectangleF(0.5f, 0.5f, 0.5f, 0.5f)));
    }

    [Fact]
    public void A_saved_position_survives_a_change_of_screen()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var bounds = new Rectangle(1420, 640, 480, 360);
        (double left, double top) = PipGeometry.ToShares(bounds, area);
        Assert.Equal(bounds.Location, PipGeometry.FromShares(left, top, bounds.Size, area));
        Point moved = PipGeometry.FromShares(left, top, bounds.Size, new Rectangle(-1280, 0, 1280, 680));
        Assert.True(new Rectangle(-1280, 0, 1280, 680).Contains(new Rectangle(moved, bounds.Size)));
    }
}

public sealed class PipPresetTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("winscreen-presets").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void A_damaged_preset_never_loses_other_settings()
    {
        string path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, """
            {"Hotkeys":{"TogglePin":"Ctrl+Alt+F1"},"PipPresets":[{"Name":"Good","LongSide":500,"FrameRate":30},
            {"Name":"Bad","FrameRate":"fast"},{"Name":""},7,null,{"Name":"Wide","LongSide":999999,"OpacityPercent":1}]}
            """);
        AppSettings loaded = AppSettings.Load(path);
        Assert.Equal("Ctrl+Alt+F1", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.Equal(["Good", "Wide"], loaded.PipPresets.Select(preset => preset.Name));
        Assert.Equal(16384, loaded.PipPresets[1].LongSide);
        Assert.Equal(10, loaded.PipPresets[1].OpacityPercent);
    }

    [Fact]
    public void Presets_survive_saving_and_cloning()
    {
        string path = Path.Combine(_folder, "settings.json");
        var settings = new AppSettings();
        settings.PipPresets.Add(new PipPreset { Name = "Corner", CropX = 5, CropY = 6, CropWidth = 70, CropHeight = 80, Scaling = PipScaling.Crisp });
        settings.Save(path);
        PipPreset loaded = Assert.Single(AppSettings.Load(path).PipPresets);
        Assert.Equal(new Rectangle(5, 6, 70, 80), loaded.Crop);
        Assert.Equal(PipScaling.Crisp, loaded.Scaling);
        Assert.Equal("Corner", Assert.Single(settings.Clone().PipPresets).Name);
    }

    [Fact]
    public void Older_settings_load_with_no_presets()
    {
        string path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "{}");
        Assert.Empty(AppSettings.Load(path).PipPresets);
    }

    [Fact]
    public void Overrides_leave_the_global_preferences_alone()
    {
        var global = new PipPreferences { FrameRate = 60, OpacityPercent = 90 };
        PipPreferences view = global.With(new PipOverrides(FrameRate: 30));
        Assert.Equal(30, view.FrameRate);
        Assert.Equal(90, view.OpacityPercent);
        Assert.Equal(60, global.FrameRate);
    }
}

[Trait("Category", "RealWindows")]
public sealed class PipViewOptionTests
{
    private static void OnUiThread(Action<Action<int>> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test(milliseconds =>
                {
                    DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
                    while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
                });
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static PipWindow Open(nint source, Action<int> pump)
    {
        var pip = new PipWindow(source, new WindowController([]));
        pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
        pip.ShowOnTop();
        pip.Location = new Point(800, 120);
        pump(150);
        return pip;
    }

    [Fact]
    public void Saving_settings_does_not_clear_a_views_overrides()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            pip.Overrides = pip.Overrides with { OpacityPercent = 50, PositionLocked = true };
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm, OpacityPercent = 80, FrameRate = 24 });
            Assert.Equal(50, pip.EffectivePreferences.OpacityPercent);
            Assert.Equal(24, pip.EffectivePreferences.FrameRate);
            Assert.True(pip.Overrides.PositionLocked);
            pip.Close();
        });
    }

    [Fact]
    public void A_click_through_view_lets_the_pointer_reach_what_is_behind_it()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            var spot = new NativePoint { X = pip.Left + pip.Width / 2, Y = pip.Top + pip.Height / 2 };
            Assert.Equal(pip.Handle, User32.WindowFromPoint(spot));
            pip.Overrides = pip.Overrides with { ClickThrough = true };
            pump(100);
            Assert.NotEqual(pip.Handle, User32.WindowFromPoint(spot));
            pip.Overrides = pip.Overrides with { ClickThrough = false };
            pump(100);
            Assert.Equal(pip.Handle, User32.WindowFromPoint(spot));
            pip.Close();
        });
    }

    [Fact]
    public void A_preset_brings_back_crop_size_and_position()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            pip.SetCrop(new Rectangle(10, 20, 200, 100));
            pip.Location = new Point(300, 300);
            pump(100);
            PipPreset saved = pip.CurrentPreset("Test");
            Rectangle expected = pip.Bounds;
            pip.SetCrop(Rectangle.Empty);
            pip.Location = new Point(900, 200);
            pump(100);
            pip.ApplyPreset(saved);
            pump(100);
            Assert.Equal(new Rectangle(10, 20, 200, 100), pip.Crop);
            Assert.Equal(expected, pip.Bounds);
            pip.Close();
        });
    }

    [Fact]
    public void A_crop_survives_zoom_and_the_view_keeps_its_size()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            var crop = new Rectangle(20, 20, 300, 200);
            pip.SetCrop(crop);
            pump(100);
            Size size = pip.Size;
            pip.ZoomAt(new PointF(0.5f, 0.5f), 2);
            pump(100);
            Assert.Equal(size, pip.Size);
            Assert.Equal(crop, pip.Crop);
            Assert.True(pip.ResetZoom());
            Assert.False(pip.ResetZoom());
            pump(100);
            Assert.Equal(crop, pip.Crop);
            Assert.Equal(size, pip.Size);
            pip.Close();
        });
    }

    [Fact]
    public void A_share_crop_fits_a_differently_sized_source_and_a_missing_display_stays_on_screen()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            pip.ApplyPreset(new PipPreset
            {
                Name = "Shares", CropX = 1, CropY = 1, CropWidth = 2, CropHeight = 2,
                CropShares = new(0.25, 0.5, 0.5, 0.25), Device = @"\\.\NoSuchDisplay", Left = 0.5, Top = 0.5,
            });
            pump(100);
            Assert.Equal(new Rectangle(100, 150, 200, 75), pip.Crop);
            Assert.Contains(Screen.AllScreens, screen => screen.WorkingArea.Contains(pip.Bounds));
            pip.Close();
        });
    }

    [Fact]
    public void A_crop_larger_than_the_source_still_shows_something()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using PipWindow pip = Open(source.Handle, pump);
            pip.ApplyPreset(new PipPreset { Name = "Big", CropX = 5000, CropY = 5000, CropWidth = 100, CropHeight = 100 });
            pump(100);
            Assert.False(pip.IsDisposed);
            Assert.True(pip.Width > 50);
            pip.Close();
        });
    }
}
