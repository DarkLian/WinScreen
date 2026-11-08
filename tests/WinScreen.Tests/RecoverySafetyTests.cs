using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class RecoverySafetyTests
{
    private static readonly Rectangle Spot = new(260, 220, 300, 200);
    private static string JournalPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "recovery.json");

    [Theory]
    [InlineData("pin")]
    [InlineData("opacity")]
    [InlineData("click-through")]
    [InlineData("crop")]
    public void Recovery_intent_is_durable_before_each_first_mutation(string operation)
    {
        using var target = new TargetProcess(Spot);
        string path = JournalPath();
        var journal = new ChangeJournal(path);
        long before = Desktop.ExStyle(target.Handle);
        var windows = new WindowController([], journal);
        windows.Checkpoint = (step, _) =>
        {
            if (step != "before-mutation") return;
            var record = Assert.Single(journal.Load());
            Assert.Equal(before, record.ExStyle);
            Assert.NotEqual(0, record.IdentityToken);
            Assert.True(record.OwnedStyles != 0 || record.Cropped);
            throw new WindowOperationException("Injected failure before native mutation");
        };
        Assert.Throws<WindowOperationException>(() =>
        {
            switch (operation)
            {
                case "pin": windows.TogglePin(target.Handle); break;
                case "opacity": windows.SetOpacity(target.Handle, 128); break;
                case "click-through": windows.ToggleClickThrough(target.Handle); break;
                case "crop": windows.Crop(target.Handle, new Rectangle(0, 0, 60, 60)); break;
            }
        });
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
        windows.Checkpoint = null;
        Assert.Equal(0, windows.RestoreAll());
    }

    [Fact]
    public void Damaged_recovery_data_is_set_aside_once_and_nothing_stays_blocked()
    {
        string path = JournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var first = new WindowController([], new ChangeJournal(path));
        first.RecoverFromJournal();
        Assert.False(first.RecoveryBlocked);
        Assert.Contains(".invalid-", first.RecoveryWarning);
        Assert.Equal(0, first.RestoreAll());

        var second = new WindowController([], new ChangeJournal(path));
        second.RecoverFromJournal();
        Assert.Null(second.RecoveryWarning);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.invalid-*"));

        using var target = new TargetProcess(Spot);
        Assert.Equal((byte)128, second.SetOpacity(target.Handle, 128));
        Assert.Equal(0, second.RestoreAll());
    }

    [Fact]
    public void Unreadable_recovery_data_pauses_changes_but_never_exit_and_can_be_retried()
    {
        string path = JournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[]");
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([], new ChangeJournal(path));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            windows.RecoverFromJournal();
            Assert.True(windows.RecoveryBlocked);
            Assert.Throws<WindowOperationException>(() => windows.SetOpacity(target.Handle, 128));
            Assert.Equal(0, windows.RestoreAll());
            Assert.True(windows.RecoveryBlocked);
        }

        Assert.Equal(0, windows.RestoreAll());
        Assert.False(windows.RecoveryBlocked);
        Assert.Equal((byte)128, windows.SetOpacity(target.Handle, 128));
        windows.RestoreAll();
    }

    [Fact]
    public void A_refused_crop_leaves_no_false_state()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([], new ChangeJournal(JournalPath()));
        windows.Checkpoint = (step, _) =>
        {
            if (step == "before-mutation") throw new WindowOperationException("Injected failure");
        };
        Assert.Throws<WindowOperationException>(() => windows.Crop(target.Handle, new Rectangle(0, 0, 60, 60)));
        Assert.False(windows.IsCropped(target.Handle));
    }

    [Fact]
    public void Turning_click_through_off_removes_the_transparency_it_added_but_not_a_chosen_level()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        Assert.True(windows.ToggleClickThrough(target.Handle));
        Assert.True(windows.OpacityOf(target.Handle) < 255);
        Assert.False(windows.ToggleClickThrough(target.Handle));
        Assert.Equal(255, windows.OpacityOf(target.Handle));

        windows.SetOpacity(target.Handle, 120);
        windows.ToggleClickThrough(target.Handle);
        windows.ToggleClickThrough(target.Handle);
        Assert.Equal(120, windows.OpacityOf(target.Handle));
        windows.RestoreAll();
    }

    [Fact]
    public void Hovering_a_click_through_window_keeps_it_see_through()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        windows.ToggleClickThrough(target.Handle);
        byte? before = Desktop.Alpha(target.Handle);
        windows.Refresh(target.Center, opaqueOnHover: true, pausePinWhenMaximized: false);
        Assert.Equal(before, Desktop.Alpha(target.Handle));
        windows.RestoreAll();
    }

    [Fact]
    public void Watching_a_window_writes_nothing_to_it()
    {
        using var target = new TargetProcess(Spot);
        WindowIdentity observed = WindowIdentity.Observe(target.Handle);
        Assert.True(observed.IsSameWindow());
        Assert.Equal(0, User32.GetProp(target.Handle, WindowIdentity.PropertyName));
    }

    [Fact]
    public void Journal_write_failure_prevents_native_mutation()
    {
        using var target = new TargetProcess(Spot);
        string path = JournalPath();
        Directory.CreateDirectory(path); // A directory cannot be replaced by the journal file.
        long before = Desktop.ExStyle(target.Handle);
        var windows = new WindowController([], new ChangeJournal(path));
        Assert.Throws<WindowOperationException>(() => windows.SetOpacity(target.Handle, 128));
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
    }

    [Fact]
    public void An_already_hung_target_is_rejected_within_a_bounded_probe()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([], new ChangeJournal(JournalPath()));
        target.Hang();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<WindowOperationException>(() => windows.SetOpacity(target.Handle, 128));
        Assert.InRange(elapsed.ElapsedMilliseconds, 0, 1500);
    }

    [Fact]
    public void Failed_crop_restore_keeps_its_record_while_restoring_other_effects_and_survives_restart()
    {
        using var target = new TargetProcess(Spot);
        Desktop.GainForegroundRights();
        string path = JournalPath();
        var journal = new ChangeJournal(path);
        long before = Desktop.ExStyle(target.Handle);
        var windows = new WindowController([], journal);
        windows.TogglePin(target.Handle);
        windows.SetOpacity(target.Handle, 128);
        windows.ToggleClickThrough(target.Handle);
        windows.Crop(target.Handle, new Rectangle(0, 0, 80, 80));
        windows.Checkpoint = (step, _) =>
        {
            if (step == "restore-crop") throw new WindowOperationException("Injected crop failure");
        };
        Assert.Equal(1, windows.RestoreAll());
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
        Assert.True(Assert.Single(journal.Load()).Cropped);
        var restarted = new WindowController([], journal);
        restarted.RecoverFromJournal();
        Assert.Null(WindowInfo.ShapeOf(target.Handle));
        Assert.Empty(journal.Load());
    }

    [Fact]
    public void Restore_preserves_style_bits_added_independently_and_ignores_new_filters()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        windows.SetOpacity(target.Handle, 128);
        const long independent = 0x08000000; // WS_EX_NOACTIVATE, never owned by this controller.
        long style = Desktop.ExStyle(target.Handle);
        User32.SetWindowLongPtr(target.Handle, User32.GWL_EXSTYLE, (nint)(style | independent));
        windows.SetExcluded(["WinScreen.TestTarget"]);
        Assert.True(windows.RestoreWindow(target.Handle));
        Assert.Equal(independent, Desktop.ExStyle(target.Handle) & independent);
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_LAYERED);
    }

    [Fact]
    public void Replaced_window_identity_cannot_receive_the_previous_snapshots()
    {
        using var target = new TargetProcess(Spot);
        var windows = new WindowController([]);
        windows.SetOpacity(target.Handle, 128);
        var identity = WindowIdentity.Capture(target.Handle);
        User32.SetProp(target.Handle, WindowIdentity.PropertyName, (nint)(identity.Token ^ 2));
        Assert.Equal(255, windows.OpacityOf(target.Handle));
        Assert.Equal(0, windows.RestoreAll());
        Assert.Equal((byte?)128, Desktop.Alpha(target.Handle)); // Stale restore deliberately did not write.
        Assert.Equal(0, windows.PendingRestores);
    }

    [Fact]
    public void Hover_stays_solid_when_opacity_is_changed_and_unsafe_layered_renderers_are_refused()
    {
        using var source = new TargetProcess(Spot);
        var windows = new WindowController([]);
        windows.SetOpacity(source.Handle, 128);
        windows.Refresh(source.Center, opaqueOnHover: true, pausePinWhenMaximized: false);
        windows.SetOpacity(source.Handle, 100);
        Assert.Equal((byte?)255, Desktop.Alpha(source.Handle));
        windows.Refresh(Point.Empty, opaqueOnHover: true, pausePinWhenMaximized: false);
        Assert.Equal((byte?)100, Desktop.Alpha(source.Handle));
        windows.RestoreAll();
        using var renderer = new TargetProcess(new Rectangle(650, 220, 300, 200));
        nint layered = renderer.CreateUnattributedLayeredWindow();
        Assert.NotEqual(0, layered);
        Assert.False(User32.GetLayeredWindowAttributes(layered, out _, out _, out _));
        var unsafeRenderer = new WindowController([]);
        Assert.Throws<WindowOperationException>(() => unsafeRenderer.SetOpacity(layered, 128));
        Assert.Throws<WindowOperationException>(() => unsafeRenderer.Hide(layered));
        Assert.False(User32.GetLayeredWindowAttributes(layered, out _, out _, out _));
    }
}
