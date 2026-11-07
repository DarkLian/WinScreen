using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class RollUpTests
{
    private static readonly Rectangle Spot = new(240, 200, 500, 400);

    private static Rectangle RectOf(nint window)
    {
        Assert.True(User32.GetWindowRect(window, out Rect r));
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static string JournalPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "recovery.json");

    [Fact]
    public void Rolls_up_to_a_title_bar_and_back()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        var windows = new WindowController([]);
        Rectangle before = RectOf(target.Handle);

        Assert.True(windows.ToggleRollUp(target.Handle));
        Assert.True(windows.IsRolledUp(target.Handle));
        Assert.InRange(RectOf(target.Handle).Height, 1, 80);
        Assert.Equal(before.Width, RectOf(target.Handle).Width);

        Assert.False(windows.ToggleRollUp(target.Handle));
        Assert.False(windows.IsRolledUp(target.Handle));
        Assert.Equal(before.Size, RectOf(target.Handle).Size);
    }

    [Fact]
    public void Rolling_down_keeps_a_move_made_while_rolled_up()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        var windows = new WindowController([]);
        int height = RectOf(target.Handle).Height;
        windows.ToggleRollUp(target.Handle);
        User32.SetWindowPos(target.Handle, 0, 600, 300, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

        windows.ToggleRollUp(target.Handle);

        Rectangle after = RectOf(target.Handle);
        Assert.Equal(new Point(600, 300), after.Location);
        Assert.Equal(height, after.Height);
    }

    [Fact]
    public void A_killed_copy_is_recovered_on_the_next_start()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        int height = RectOf(target.Handle).Height;
        var killed = new WindowController([], new ChangeJournal(path));
        killed.ToggleRollUp(target.Handle);
        Assert.True(RectOf(target.Handle).Height < height);

        var next = new WindowController([], new ChangeJournal(path));
        next.RecoverFromJournal();

        Assert.Equal(height, RectOf(target.Handle).Height);
        Assert.False(next.IsRolledUp(target.Handle));
        Assert.Empty(new ChangeJournal(path).Load());
    }

    [Fact]
    public void The_journal_knows_where_it_came_from_before_the_window_changes()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        string path = JournalPath();
        var journal = new ChangeJournal(path);
        var windows = new WindowController([], journal);
        int height = RectOf(target.Handle).Height;
        windows.Checkpoint = (step, _) =>
        {
            if (step != "before-mutation") return;
            Assert.NotNull(Assert.Single(journal.Load()).RollUpPlacement);
            throw new WindowOperationException("Injected failure before native mutation");
        };
        Assert.Throws<WindowOperationException>(() => windows.ToggleRollUp(target.Handle));
        Assert.Equal(height, RectOf(target.Handle).Height);
    }

    [Fact]
    public void Restore_all_brings_it_back()
    {
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        var windows = new WindowController([]);
        int height = RectOf(target.Handle).Height;
        windows.ToggleRollUp(target.Handle);
        Assert.Equal(0, windows.RestoreAll());
        Assert.Equal(height, RectOf(target.Handle).Height);
    }

    [Fact]
    public void A_window_without_a_title_bar_is_refused_and_left_alone()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        Rectangle before = RectOf(target.Handle);
        Assert.Throws<WindowOperationException>(() => windows.ToggleRollUp(target.Handle));
        Assert.Equal(before, RectOf(target.Handle));
        Assert.False(windows.IsRolledUp(target.Handle));
    }

    [Fact]
    public void A_journal_with_a_bad_roll_up_record_is_treated_as_damaged()
    {
        string path = JournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[{\"Window\":1,\"ProcessId\":1,\"ExStyle\":0,\"HadLayeredAttributes\":false,\"ColorKey\":0,\"Alpha\":255,\"LayeredFlags\":0,\"IdentityToken\":3,\"RollUpPlacement\":[1,2]}]");
        var journal = new ChangeJournal(path);
        Assert.Empty(journal.Load());
        Assert.NotNull(journal.DamagedBackup);
    }
}
