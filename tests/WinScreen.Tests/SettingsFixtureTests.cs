using System.Text.Json;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A settings.json saved by each published version, with made-up choices (tools\settings-fixtures), loads into
/// this version with every choice kept (roadmap P0-2). A new release adds its file with
/// <c>Make-SettingsFixtures.ps1 -Tag v…</c>; old files are never edited.
/// </summary>
public class SettingsFixtureTests
{
    private const string Prefix = "SettingsFixture.";

    internal static string[] VersionNames() =>
        typeof(SettingsFixtureTests).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(name => name[Prefix.Length..^".json".Length]).ToArray();

    public static TheoryData<string> Versions() => new(VersionNames());

    internal static string Fixture(string version)
    {
        using var reader = new StreamReader(typeof(SettingsFixtureTests).Assembly.GetManifestResourceStream($"{Prefix}{version}.json")!);
        return reader.ReadToEnd();
    }

    /// <summary>Loads a fixture the way WinScreen starts: from a file, in a folder of its own.</summary>
    private static (AppSettings Settings, string Folder) LoadFromDisk(string version)
    {
        string folder = Path.Combine(Path.GetTempPath(), "WinScreenSettingsFixture-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), Fixture(version));
        return (AppSettings.Load(Path.Combine(folder, "settings.json")), folder);
    }

    [Fact]
    public void There_is_a_fixture_for_every_published_version_since_0_7_1()
    {
        string[] versions = VersionNames();
        foreach (string expected in new[] { "0.7.1-beta", "0.8.0-beta", "0.8.1-beta", "0.8.2-beta", "0.9.0-beta", "0.9.1-beta",
                     "0.10.1-beta", "0.10.2-beta", "0.10.3-beta", "1.0.0" })
            Assert.Contains(expected, versions);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Settings_saved_by_an_earlier_version_keep_every_choice(string version)
    {
        (AppSettings settings, string folder) = LoadFromDisk(version);
        try
        {
            // Read as valid: nothing was set aside as damaged.
            Assert.Equal(["settings.json"], Directory.GetFiles(folder).Select(Path.GetFileName));

            Assert.Equal("Ctrl+Shift+F9", settings.Hotkeys[AppCommand.TogglePin]);
            Assert.Equal("", settings.Hotkeys[AppCommand.OpacityUp]);
            Assert.Equal("Ctrl+Alt+Shift+R", settings.Hotkeys[AppCommand.ToggleRollUp]);
            Assert.Equal("Ctrl+Alt+Down", settings.Hotkeys[AppCommand.OpacityDown]);

            Assert.Equal(["csgo", "cs2", "valorant", "eldenring", "fixture-game"], settings.ExcludedPrograms);
            Assert.Equal(["fixture-included"], settings.IncludedPrograms);
            Assert.Contains("fixture-paint", settings.ModifierExclusions);
            Assert.Equal(["fixture-menu"], settings.WindowMenuExclusions);
            using (JsonDocument saved = JsonDocument.Parse(Fixture(version)))
                if (saved.RootElement.TryGetProperty(nameof(AppSettings.PipOriginalPrograms), out _))
                    Assert.Equal(["fixture-java"], settings.PipOriginalPrograms);

            Assert.Equal(2, settings.SavedSetups.Count);
            ProgramSetup editor = settings.SavedSetups[0];
            Assert.Equal(("fixture-editor", @"C:\Fixture\editor.exe", true, (byte)200, true, false),
                (editor.Program, editor.ExecutablePath, editor.Pinned, editor.Alpha, editor.Dark, editor.View is not null));
            Assert.Equal(("fixture-chat", true), (settings.SavedSetups[1].Program, settings.SavedSetups[1].Pinned));

            Assert.False(settings.ShowToolbar);
            Assert.True(settings.ModifierDrag);
            Assert.Equal(ModifierKey.Win, settings.ModifierKey);
            Assert.True(settings.WindowMenu);
            Assert.Equal(WindowMenuGesture.CtrlMiddleClick, settings.WindowMenuGesture);
            Assert.Equal(60, settings.DefaultOpacityPercent);
            Assert.Equal("zh-Hans", settings.Language);
            Assert.True(settings.ExperimentalFeatures);
            Assert.Equal(AnchorSide.Left, settings.Anchors.Side);
            Assert.True(settings.Anchors.Automatic);

            Assert.Equal(ToolbarPlacement.Right, settings.Toolbar.Placement);
            Assert.Equal(ToolbarTrigger.MinimizeButton, settings.Toolbar.Trigger);
            // Versions before the Window menu button get it once, added at the end.
            bool hadMenuButton = Fixture(version).Contains("\"WindowMenuOffered\"", StringComparison.Ordinal);
            AppCommand[] buttons = [AppCommand.TogglePin, AppCommand.OpenPip, AppCommand.RestoreWindow];
            Assert.Equal(hadMenuButton ? buttons : [.. buttons, AppCommand.ShowWindowMenu], settings.Toolbar.Buttons);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Settings_from_an_earlier_version_load_the_same_after_they_are_saved_again(string version)
    {
        (AppSettings first, string folder) = LoadFromDisk(version);
        try
        {
            string path = Path.Combine(folder, "settings.json");
            first.Save(path);
            AppSettings again = AppSettings.Load(path);

            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again));
            Assert.Equal(first.Toolbar.Buttons, again.Toolbar.Buttons); // the Window menu button isn't offered twice
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
