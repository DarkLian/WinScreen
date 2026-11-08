using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class PipParkingGeometryTests
{
    [Fact]
    public void Parking_uses_an_external_edge_across_negative_and_stacked_monitors()
    {
        Rectangle[] monitors = [new(-1920, 0, 1920, 1080), new(0, 0, 1920, 1080), new(0, 1080, 1920, 1080)];
        Rectangle parked = WindowController.ParkingPlace(new Size(900, 700), monitors);
        foreach (Rectangle monitor in monitors)
        {
            Rectangle intersection = Rectangle.Intersect(parked, monitor);
            Assert.True(intersection.IsEmpty || intersection.Height <= 2);
        }
        Assert.Equal(900, parked.Width);
        Assert.Equal(700, parked.Height);
    }

    [Fact]
    public void Compatible_input_paths_survive_cloning_and_loading_and_null_entries_are_ignored()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-input-settings").FullName;
        try
        {
            var settings = new AppSettings { PipCompatibleInputPrograms = [@"C:\Apps\One.exe", @"C:\Apps\Two.exe"] };
            string file = Path.Combine(folder, "settings.json");
            settings.Save(file);
            Assert.Equal(settings.PipCompatibleInputPrograms, settings.Clone().PipCompatibleInputPrograms);
            Assert.Equal(settings.PipCompatibleInputPrograms, AppSettings.Load(file).PipCompatibleInputPrograms);
            File.WriteAllText(file, """{"PipCompatibleInputPrograms":[null,"","C:/Apps/One.exe","c:/apps/one.EXE"]}""");
            Assert.Single(AppSettings.Load(file).PipCompatibleInputPrograms);
        }
        finally { Directory.Delete(folder, true); }
    }
}

[Trait("Category", "RealWindows")]
public sealed class PipParkedRecoveryTests
{
    [Fact]
    public void Parked_intent_is_durable_before_movement_and_a_new_controller_recovers_it()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        Rectangle before = Desktop.BoundsOf(source.Handle);
        long originalStyle = Desktop.ExStyle(source.Handle);
        string folder = Directory.CreateTempSubdirectory("winscreen-parked-recovery").FullName;
        var journal = new ChangeJournal(Path.Combine(folder, "journal.json"));
        var windows = new WindowController([], journal);
        try
        {
            windows.Hide(source.Handle);
            windows.SetOutOfSwitcher(source.Handle, true);
            bool checkedIntent = false;
            windows.Checkpoint = (step, _) =>
            {
                if (step != "before-mutation" || checkedIntent) return;
                WindowRecord record = Assert.Single(journal.Load());
                Assert.True(record.Parked);
                Assert.NotNull(record.Placement);
                Assert.Equal(before, Desktop.BoundsOf(source.Handle));
                checkedIntent = true;
            };
            windows.Park(source.Handle);
            Assert.True(checkedIntent);
            Assert.NotEqual(before, Desktop.BoundsOf(source.Handle));
            var recovery = new WindowController([], journal);
            recovery.RecoverFromJournal();
            Assert.Equal(before, Desktop.BoundsOf(source.Handle));
            Assert.Equal(originalStyle, Desktop.ExStyle(source.Handle));
            Assert.True(Desktop.Alpha(source.Handle) is null or 255);
            Assert.Empty(journal.Load());
        }
        finally { windows.Checkpoint = null; windows.RestoreAll(); Directory.Delete(folder, true); }
    }
}
