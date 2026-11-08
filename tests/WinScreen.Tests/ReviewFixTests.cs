using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

public sealed class BackupFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WinScreen.Tests." + Guid.NewGuid().ToString("N"));

    public BackupFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Only_the_newest_backups_of_that_file_are_kept()
    {
        string settings = Path.Combine(_folder, "settings.json");
        File.WriteAllText(settings, "{}");
        string other = Path.Combine(_folder, "changed-windows.json.invalid-other");
        File.WriteAllText(other, "[]");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < 8; index++)
        {
            string backup = settings + ".invalid-" + index;
            File.WriteAllText(backup, "{}");
            File.SetLastWriteTimeUtc(backup, start.AddMinutes(index));
        }

        BackupFiles.Prune(settings, ".invalid-");

        string[] left = Directory.GetFiles(_folder, "settings.json.invalid-*").Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["settings.json.invalid-3", "settings.json.invalid-4", "settings.json.invalid-5",
            "settings.json.invalid-6", "settings.json.invalid-7"], left);
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_missing_folder_is_not_a_failure() =>
        BackupFiles.Prune(Path.Combine(_folder, "gone", "settings.json"), ".invalid-");
}

public sealed class ProgramRuleTests
{
    [Fact]
    public void Excluded_programs_win_and_an_inclusion_list_limits_the_rest()
    {
        var settings = new AppSettings { ExcludedPrograms = ["notepad"], IncludedPrograms = [] };
        Assert.False(settings.Allows("Notepad"));
        Assert.True(settings.Allows("mspaint"));

        settings.IncludedPrograms = ["mspaint", "notepad"];
        Assert.True(settings.Allows("MSPaint"));
        Assert.False(settings.Allows("notepad"));
        Assert.False(settings.Allows("calc"));
    }
}
