using System.Reflection;
using System.Text.Json;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class CustomizationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WinScreen-transfer-" + Guid.NewGuid().ToString("N"));
    public CustomizationTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void Portable_settings_round_trip_and_keep_this_machines_startup_state()
    {
        string file = Path.Combine(_folder, "export.json");
        var source = new AppSettings
        {
            OpacityStepPercent = 3, OpacityWheel = true, DetectAlreadyDark = false, StartWithWindows = true,
            WelcomeShown = false, PinBorder = new() { ColorHex = "#2368AB", OpacityPercent = 45, Thickness = 6 },
            SavedSetups = [new() { Program = "example", ExecutablePath = @"C:\Apps\example.exe", Pinned = true }],
        };
        SettingsTransfer.Export(source, file);
        string json = File.ReadAllText(file);
        Assert.DoesNotContain("StartWithWindows", json);
        Assert.DoesNotContain("WelcomeShown", json);
        var current = new AppSettings { StartWithWindows = false, WelcomeShown = true };
        AppSettings imported = SettingsTransfer.Import(file, current);
        Assert.False(imported.StartWithWindows);
        Assert.True(imported.WelcomeShown);
        Assert.True(imported.Imported);
        Assert.Equal(3, imported.OpacityStepPercent);
        Assert.True(imported.OpacityWheel);
        Assert.False(imported.DetectAlreadyDark);
        Assert.Equal(source.PinBorder, imported.PinBorder);
        Assert.Equal(source.SavedSetups[0].ExecutablePath, Assert.Single(imported.SavedSetups).ExecutablePath);
        Assert.False(current.OpacityWheel);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"FormatVersion\":2,\"Settings\":{}}")]
    [InlineData("{\"FormatVersion\":\"1\",\"Settings\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"Settings\":null}")]
    [InlineData("{\"FormatVersion\":1,\"Settings\":{\"Hotkeys\":null}}")]
    public void Invalid_transfer_files_are_rejected_without_changing_current_settings(string json)
    {
        string file = Path.Combine(_folder, "import.json");
        File.WriteAllText(file, json);
        var current = new AppSettings { OpacityStepPercent = 4 };
        Assert.Throws<JsonException>(() => SettingsTransfer.Import(file, current));
        Assert.Equal(4, current.OpacityStepPercent);
    }

    [Fact]
    public void Import_and_startup_use_the_same_range_and_enum_validation()
    {
        const string json = """{"OpacityStepPercent":200,"PinBorder":{"ColorHex":"bad","Thickness":0,"OpacityPercent":400},"ModifierKey":300,"DefaultOpacityPercent":0}""";
        string file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, json);
        AppSettings loaded = AppSettings.Load(file);
        File.WriteAllText(file, "{\"FormatVersion\":1,\"Settings\":" + json + "}");
        AppSettings imported = SettingsTransfer.Import(file, new());
        Assert.Equal(25, imported.OpacityStepPercent);
        Assert.Equal("#E82020", imported.PinBorder.ColorHex);
        Assert.Equal(1, imported.PinBorder.Thickness);
        Assert.Equal(100, imported.PinBorder.OpacityPercent);
        Assert.Equal(20, imported.DefaultOpacityPercent);
        Assert.Equal(loaded.PinBorder, imported.PinBorder);
        Assert.Equal(loaded.ModifierKey, imported.ModifierKey);
    }

    [Fact]
    public void Wheel_accumulates_partial_notches_and_keeps_the_direction()
    {
        int remainder = 0;
        Assert.Equal(0, MouseGestures.TakeWheelSteps(ref remainder, 40));
        Assert.Equal(1, MouseGestures.TakeWheelSteps(ref remainder, 100));
        Assert.Equal(20, remainder);
        Assert.Equal(-1, MouseGestures.TakeWheelSteps(ref remainder, -140));
        Assert.Equal(0, remainder);
    }

    [Fact]
    public void Opacity_wheel_can_share_a_hook_without_starting_a_mover()
    {
        using var gestures = new MouseGestures(_ => false, _ => { }) { MoveEnabled = false, OpacityWheelEnabled = true };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        gestures.SetActive(true);
        Assert.Null(typeof(MouseGestures).GetField("_mover", flags)!.GetValue(gestures));
        Assert.NotNull(typeof(MouseGestures).GetField("_hookThread", flags)!.GetValue(gestures));
        gestures.SetActive(false);
        Assert.Null(typeof(MouseGestures).GetField("_hookThread", flags)!.GetValue(gestures));
    }

    [Fact]
    public void Border_settings_apply_to_real_pixels_and_leave_the_centre_open()
    {
        using var target = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var border = new PinBorder(target.Handle);
            border.Configure(new() { ColorHex = "#23AB45", Thickness = 8, OpacityPercent = 50 });
            Assert.Equal(Color.FromArgb(0x23, 0xAB, 0x45), border.BackColor);
            Assert.Equal((byte)128, Desktop.Alpha(border.Handle));
            Assert.True(border.Region!.IsVisible(3, 3));
            Assert.False(border.Region.IsVisible(border.Width / 2, border.Height / 2));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_uses_the_apps_hotkey_transaction_and_backs_up_only_an_applied_file(bool duplicateHotkeys)
    {
        string settingsFile = Path.Combine(_folder, "settings.json");
        string exportFile = Path.Combine(_folder, "export.json");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var settings = new AppSettings
            {
                WelcomeShown = true, ShowNotifications = false, ShowToolbar = false,
                Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
            };
            using var app = new TrayApplication(new WindowController([]), settings, settingsFile);
            try
            {
            string before = File.ReadAllText(settingsFile);
            var changed = settings.Clone();
            changed.OpacityStepPercent = 4;
            if (duplicateHotkeys)
            {
                changed.Hotkeys[AppCommand.TogglePin] = "Ctrl+Shift+F19";
                changed.Hotkeys[AppCommand.ToggleDark] = "Ctrl+Shift+F19";
            }
            SettingsTransfer.Export(changed, exportFile);
            var imported = SettingsTransfer.Import(exportFile, settings);
            var apply = typeof(TrayApplication).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var problems = (IReadOnlyList<HotkeyProblem>)apply.Invoke(app, [imported])!;
            if (duplicateHotkeys)
            {
                Assert.NotEmpty(problems);
                Assert.Equal(before, File.ReadAllText(settingsFile));
                Assert.Empty(Directory.GetFiles(_folder, "settings.json.before-import-*"));
                Assert.Equal(10, settings.OpacityStepPercent);
            }
            else
            {
                Assert.Empty(problems);
                string backup = Assert.Single(Directory.GetFiles(_folder, "settings.json.before-import-*"));
                Assert.Equal(before, File.ReadAllText(backup));
                Assert.Equal(4, AppSettings.Load(settingsFile).OpacityStepPercent);
                Assert.Equal(4, settings.OpacityStepPercent);
            }
            }
            finally { app.ExitThread(); }
        });
    }
}
