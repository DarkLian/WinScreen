using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public class ChangeJournalTests
{
    private static string MissingFolder() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "journal.json");

    [Fact]
    public void A_first_start_with_no_data_folder_is_fine()
    {
        var journal = new ChangeJournal(MissingFolder());
        Assert.Empty(journal.Load());
        journal.Save([]);
    }

    [Fact]
    public void Records_survive_a_restart()
    {
        string path = MissingFolder();
        var record = new WindowRecord(123, 456, 0x100, true, 1, 200, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        new ChangeJournal(path).Save([record]);

        WindowRecord loaded = Assert.Single(new ChangeJournal(path).Load());

        Assert.Equal(record.Window, loaded.Window);
        Assert.Equal(record.ProcessId, loaded.ProcessId);
        Assert.Equal(record.Placement, loaded.Placement);
        Assert.False(loaded.Cropped);
        new ChangeJournal(path).Save([]);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_crop_survives_a_restart()
    {
        string path = MissingFolder();
        new ChangeJournal(path).Save([new WindowRecord(123, 456, 0x100, false, 0, 255, 0, null, Cropped: true)]);

        Assert.True(Assert.Single(new ChangeJournal(path).Load()).Cropped);
        new ChangeJournal(path).Save([]);
    }

    [Fact]
    public void A_file_from_before_cropping_still_loads()
    {
        string path = MissingFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[{\"Window\":1,\"ProcessId\":2,\"ExStyle\":3,\"HadLayeredAttributes\":false,\"ColorKey\":0,\"Alpha\":255,\"LayeredFlags\":0,\"Placement\":null}]");

        Assert.False(Assert.Single(new ChangeJournal(path).Load()).Cropped);
        new ChangeJournal(path).Save([]);
    }

    [Fact]
    public void A_damaged_file_is_ignored()
    {
        string path = MissingFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not json");

        Assert.Empty(new ChangeJournal(path).Load());
    }

    [Fact]
    public void Malformed_records_are_skipped_individually_and_the_original_is_preserved()
    {
        string path = MissingFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[null,{\"Window\":\"bad\",\"ProcessId\":2},{\"Window\":1,\"ProcessId\":2,\"Placement\":[1,2]}," +
            "{\"Window\":3,\"ProcessId\":4,\"Placement\":null}]");
        var valid = Assert.Single(new ChangeJournal(path).Load());
        Assert.Equal(3, valid.Window);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "journal.json.invalid-*"));
    }
}
