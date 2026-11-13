using System.Reflection;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Preset sizes and Minimize to an Anchor (roadmap P2-14): the arithmetic.</summary>
public sealed class PresetSizeTests
{
    private static readonly Rectangle Area = new(0, 0, 1920, 1040);

    [Fact]
    public void A_new_size_keeps_the_top_left_corner_where_it_fits()
    {
        Assert.Equal(new Rectangle(100, 50, 1280, 720), PlacementGeometry.Resized(new Rectangle(100, 50, 400, 300), new Size(1280, 720), Area));
    }

    [Fact]
    public void A_window_that_would_go_off_the_display_moves_back_onto_it()
    {
        Assert.Equal(new Rectangle(640, 320, 1280, 720), PlacementGeometry.Resized(new Rectangle(1500, 900, 400, 300), new Size(1280, 720), Area));
        Assert.Equal(new Rectangle(0, 0, 800, 600), PlacementGeometry.Resized(new Rectangle(-300, -200, 400, 300), new Size(800, 600), Area));
    }

    [Fact]
    public void A_size_bigger_than_the_display_is_cut_to_it()
    {
        Assert.Equal(new Rectangle(0, 0, 1366, 768), PlacementGeometry.Resized(new Rectangle(10, 10, 400, 300), new Size(1920, 1080), new Rectangle(0, 0, 1366, 768)));
    }

    [Fact]
    public void Every_preset_is_a_real_size() =>
        Assert.All(WindowMenuBuilder.PresetSizes, size => Assert.True(size.Width >= 640 && size.Height >= 480));
}

/// <summary>Preset sizes and Minimize to an Anchor on real windows.</summary>
[Trait("Category", "RealWindows")]
public sealed class SmallCommandTests
{
    [Fact]
    public void Minimize_to_an_Anchor_leaves_an_Anchor_that_brings_the_window_back_and_Put_back_takes_it_away()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-small-").FullName;
        using var target = new TargetProcess(new Rectangle(300, 300, 420, 300));
        target.UseTitleBar();
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    run.Invoke(app, [AppCommand.MinimizeToAnchor, target.Handle]);
                    Assert.True(Native.User32.IsIconic(target.Handle), "The window wasn't minimized.");
                    Assert.True((bool)isOn.Invoke(app, [AppCommand.ToggleAnchor, target.Handle])!, "No Anchor was left for it.");

                    run.Invoke(app, [AppCommand.RestoreWindow, target.Handle]);
                    Assert.False((bool)isOn.Invoke(app, [AppCommand.ToggleAnchor, target.Handle])!, "Put back left the Anchor.");
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
