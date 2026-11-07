using System.Reflection;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class WindowManagementTests
{
    private static readonly int[] Placement = [0, 1, 0, 0, 0, 0, 100, 100, 500, 400];
    private static string JournalPath() => Path.Combine(Directory.CreateTempSubdirectory("winscreen-appearance").FullName, "recovery.json");

    [Fact]
    public void New_fields_round_trip_without_widening_legacy_owned_styles()
    {
        var record = new WindowRecord(123, 456, 0, false, 0, 255, 0, null)
        {
            IdentityToken = 7, StyleBits = WindowController.BorderlessMask, SwitcherBits = 0,
            BorderlessPlacement = Placement, BorderlessOrigin = [110, 131], BorderlessFilled = true, OriginalDarkTitleBar = 0,
        };
        var journal = new ChangeJournal(JournalPath());
        journal.Save([record]);
        var loaded = Assert.Single(journal.Load());
        Assert.Equal(0, loaded.OwnedStyles);
        Assert.Equal(record.StyleBits, loaded.StyleBits);
        Assert.Equal(0L, loaded.SwitcherBits);
        Assert.Equal(record.BorderlessPlacement, loaded.BorderlessPlacement);
        Assert.Equal(record.BorderlessOrigin, loaded.BorderlessOrigin);
        Assert.True(loaded.BorderlessFilled);
        Assert.Equal(0, loaded.OriginalDarkTitleBar);
        Assert.Null(journal.DamagedBackup);
        journal.Save([]);
    }

    [Theory]
    [InlineData("\"StyleBits\":1")]
    [InlineData("\"StyleBits\":12582912")]
    [InlineData("\"SwitcherBits\":8")]
    [InlineData("\"OriginalDarkTitleBar\":2")]
    [InlineData("\"BorderlessPlacement\":[0,1]")]
    [InlineData("\"BorderlessOrigin\":[1]")]
    [InlineData("\"BorderlessFilled\":true")]
    [InlineData("\"OwnedStyles\":128")]
    public void Damaged_appearance_fields_are_rejected_and_preserved(string field)
    {
        string path = JournalPath();
        File.WriteAllText(path, "[{\"Window\":123,\"ProcessId\":456,\"IdentityToken\":7," + field + "}]");
        var journal = new ChangeJournal(path);
        Assert.Empty(journal.Load());
        Assert.NotNull(journal.DamagedBackup);
        Assert.Contains(field, File.ReadAllText(journal.DamagedBackup!));
    }

    [Fact]
    public void Old_records_have_no_new_changes_to_recover()
    {
        string path = JournalPath();
        File.WriteAllText(path, "[{\"Window\":123,\"ProcessId\":456,\"IdentityToken\":7}]");
        WindowRecord record = Assert.Single(new ChangeJournal(path).Load());
        Assert.Equal(0, record.StyleBits);
        Assert.Null(record.SwitcherBits);
        Assert.Null(record.BorderlessPlacement);
        Assert.Null(record.OriginalDarkTitleBar);
    }

    [Fact]
    public void Recovery_and_borderless_undo_always_run_but_exclusive_mode_refuses_new_borderless_changes()
    {
        foreach (FullScreenState state in Enum.GetValues<FullScreenState>())
        foreach (bool pause in new[] { false, true })
        {
            foreach (var command in new[] { AppCommand.RestoreAll, AppCommand.RestoreWindow, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill })
                Assert.Equal(FullScreenAction.Run, FullScreenPolicy.Decide(command, state, pause, false, false, false, true));
            Assert.Equal(FullScreenAction.Refuse, FullScreenPolicy.Decide(AppCommand.ToggleBorderless,
                FullScreenState.Exclusive, pause, false, false, false));
        }
    }

    [Theory]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public void Either_enabled_gesture_can_keep_the_single_hook_alive(bool move, bool menu, bool moveExcluded, bool menuExcluded, bool expected)
    {
        Assert.Equal(expected, ModifierHookPolicy.ShouldRun(move, menu, FullScreenState.None, false, moveExcluded, menuExcluded, false));
        Assert.False(ModifierHookPolicy.ShouldRun(move, menu, FullScreenState.Busy, false, moveExcluded, menuExcluded, false));
        Assert.False(ModifierHookPolicy.ShouldRun(move, menu, FullScreenState.None, true, moveExcluded, menuExcluded, false));
        Assert.False(ModifierHookPolicy.ShouldRun(move, menu, FullScreenState.None, false, moveExcluded, menuExcluded, true));
    }

    [Theory]
    [InlineData(WindowMenuGesture.CtrlRightClick, false, false)]
    [InlineData(WindowMenuGesture.CtrlShiftRightClick, true, false)]
    [InlineData(WindowMenuGesture.CtrlMiddleClick, false, true)]
    public void Menu_modifiers_match_exactly(WindowMenuGesture gesture, bool shift, bool middle)
    {
        Assert.True(MouseGestures.MatchesMenu(gesture, true, shift, false, false, middle));
        Assert.False(MouseGestures.MatchesMenu(gesture, false, shift, false, false, middle));
        Assert.False(MouseGestures.MatchesMenu(gesture, true, !shift, false, false, middle));
        Assert.False(MouseGestures.MatchesMenu(gesture, true, shift, true, false, middle));
        Assert.False(MouseGestures.MatchesMenu(gesture, true, shift, false, true, middle));
        Assert.False(MouseGestures.MatchesMenu(gesture, true, shift, false, false, !middle));
    }

    [Fact]
    public void New_gestures_are_off_and_new_commands_have_no_default_hotkeys()
    {
        var settings = new AppSettings();
        Assert.False(settings.WindowMenu);
        Assert.Empty(settings.WindowMenuExclusions);
        foreach (var command in new[] { AppCommand.ShowWindowMenu, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar })
        {
            Assert.DoesNotContain(command, settings.Hotkeys.Keys);
            Assert.Contains(command, CommandCatalog.Extra);
            Assert.DoesNotContain(command, CommandCatalog.Ordered);
            Assert.False(CommandCatalog.IsExperimental(command));
        }
        string path = Path.Combine(Path.GetDirectoryName(JournalPath())!, "settings.json");
        File.WriteAllText(path, "{\"WindowMenuGesture\":999,\"WindowMenuExclusions\":null}");
        var loaded = AppSettings.Load(path);
        Assert.Equal(WindowMenuGesture.CtrlRightClick, loaded.WindowMenuGesture);
        Assert.Empty(loaded.WindowMenuExclusions);
        settings.WindowMenu = true;
        settings.WindowMenuGesture = WindowMenuGesture.CtrlMiddleClick;
        settings.WindowMenuExclusions = ["Example"];
        settings.Save(path);
        loaded = AppSettings.Load(path);
        Assert.True(loaded.WindowMenu);
        Assert.Equal(settings.WindowMenuGesture, loaded.WindowMenuGesture);
        Assert.Equal(settings.WindowMenuExclusions, loaded.WindowMenuExclusions);
    }

    [Theory]
    [InlineData(0, 23, false)]
    [InlineData(8, 23, false)]
    [InlineData(31, 23, true)]
    [InlineData(46, 35, true)]
    public void Standard_title_bar_detection_uses_the_window_dpi(int offset, int captionHeight, bool expected) =>
        Assert.Equal(expected, WindowController.NativeCaptionGeometry(offset, captionHeight));

    [Fact]
    public void Restoring_borderless_preserves_a_deliberate_move()
    {
        Rect result = WindowController.OffsetBorderlessPlacement(new Rect(100, 100, 500, 400), new Point(108, 131), new Point(408, 231));
        Assert.Equal((400, 200, 800, 500), (result.Left, result.Top, result.Right, result.Bottom));
    }

    [Fact]
    public async Task Hook_state_read_never_waits_on_the_controller_lock()
    {
        var windows = new WindowController([]);
        var gate = (Lock)typeof(WindowController).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(windows)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        holder.Start();
        Assert.True(held.Wait(1000));
        try
        {
            Task<bool> read = Task.Run(() => windows.GestureBlocked(123));
            Assert.False(await read.WaitAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally { release.Set(); holder.Join(); }
    }
}
