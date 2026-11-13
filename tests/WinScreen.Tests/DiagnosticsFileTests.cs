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
    public void A_log_still_being_written_goes_into_the_zip_with_nothing_personal()
    {
        string logs = Path.Combine(_folder, "logs");
        using var log = new DiagnosticLog(logs);
        log.Enqueue(new LogEntry("press", [("program", LogValue.Program(@"C:\Users\Someone\tool.exe")), ("outcome", PressOutcomeForTest.Refused)], null, null));
        log.Enqueue(new LogEntry("swallowed", [], new IOException(@"Notes - Budget C:\Users\Someone\notes.txt"), "Some.cs:3"));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        // The writer keeps going while the zip is made.
        string path = DiagnosticsFile.Save(_folder, Fixture("0.10.3-beta"), log.Files);
        log.Enqueue(new LogEntry("after", [], null, null));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        using ZipArchive zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(DiagnosticLog.FileName)!.Open());
        string text = reader.ReadToEnd();
        Assert.Contains("press program=tool.exe outcome=Refused", text);
        Assert.Contains("swallowed at=Some.cs:3 error=IOException", text);
        foreach (string personal in new[] { "Someone", "Notes", "Budget", "notes.txt", @":\" })
            Assert.DoesNotContain(personal, text);
    }

    private enum PressOutcomeForTest { Refused }

    [Fact]
    public void Every_list_of_pairs_in_the_settings_is_keyed_by_a_fixed_set()
    {
        // The shrunk settings keep property and key names. A dictionary keyed by a person's text would show it.
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([typeof(AppSettings)]);
        var keyedByText = new List<string>();
        while (pending.TryPop(out Type? type))
        {
            if (!seen.Add(type)) continue;
            if (type.IsGenericType)
            {
                if (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && !type.GetGenericArguments()[0].IsEnum)
                    keyedByText.Add(type.ToString());
                foreach (Type argument in type.GetGenericArguments()) pending.Push(argument);
            }
            if (type.IsArray) pending.Push(type.GetElementType()!);
            if (type.Assembly != typeof(AppSettings).Assembly) continue;
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                pending.Push(property.PropertyType);
        }
        Assert.Empty(keyedByText);
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
