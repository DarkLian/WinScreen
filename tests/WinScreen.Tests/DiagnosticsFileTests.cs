using System.IO.Compression;
using WinScreen.Diagnostics;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Save diagnostics: a zip safe to attach to a public report (roadmap P0-4).</summary>
public sealed class DiagnosticsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WinScreenDiagnostics-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static AppSettings Fixture(string version)
    {
        using var reader = new StreamReader(typeof(DiagnosticsFileTests).Assembly.GetManifestResourceStream($"SettingsFixture.{version}.json")!);
        return AppSettings.Parse(reader.ReadToEnd());
    }

    [Theory]
    [MemberData(nameof(SettingsFixtureTests.Versions), MemberType = typeof(SettingsFixtureTests))]
    public void Settings_from_any_version_keep_no_program_path_or_text_of_the_person(string version)
    {
        AppSettings settings = Fixture(version);
        settings.SavedSetups.Add(new ProgramSetup { Program = "Spending tracker", ExecutablePath = @"C:\Users\Someone\spending.exe" });
        settings.PipOriginalPrograms.Add("someones-tool");
        settings.SettingsWindowPlacement = new SettingsPlacement(10, 20, 900, 700, false);

        string shrunk = DiagnosticsFile.Shrunk(settings);

        foreach (string personal in new[] { "fixture", "Fixture", "Spending", "Someone", "someones", @":\\", "editor.exe" })
            Assert.DoesNotContain(personal, shrunk);
        // What helps: the choices themselves, list sizes, hotkeys and toolbar buttons.
        Assert.Contains("\"Language\": \"zh-Hans\"", shrunk);
        Assert.Contains("\"TogglePin\": \"Ctrl\\u002BShift\\u002BF9\"", shrunk);
        Assert.Contains("\"OpenPip\"", shrunk);
        Assert.Contains("\"ExcludedPrograms\": {\n    \"count\": 5\n  }".Replace("\n", Environment.NewLine), shrunk);
        Assert.Contains("\"ModifierDrag\": true", shrunk);
    }

    [Fact]
    public void The_zip_sits_alone_in_its_own_folder_and_replaces_the_last_one()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "{\"SavedSetups\":[{\"Program\":\"secret\"}]}");

        string first = DiagnosticsFile.Save(_folder, Fixture("0.10.3-beta"));
        string second = DiagnosticsFile.Save(_folder, Fixture("0.10.3-beta"));

        Assert.Equal(first, second);
        Assert.Equal(Path.Combine(_folder, DiagnosticsFile.FolderName), Path.GetDirectoryName(second));
        Assert.Equal([DiagnosticsFile.FileName], Directory.GetFiles(Path.GetDirectoryName(second)!).Select(Path.GetFileName));
        using ZipArchive zip = ZipFile.OpenRead(second);
        Assert.Contains("settings.json", zip.Entries.Select(entry => entry.FullName));
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        Assert.DoesNotContain("fixture", reader.ReadToEnd());
    }
}
