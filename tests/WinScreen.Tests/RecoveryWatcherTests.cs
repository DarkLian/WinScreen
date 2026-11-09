using System.Diagnostics;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class RecoveryWatcherTests
{
    /// <summary>
    /// A parent that is gone, or dying while it is looked up, counts as ended; the wait never throws. The dying case is
    /// a race, so it is tried many times against processes that exit at once.
    /// </summary>
    [Fact]
    public void A_parent_that_is_gone_or_dying_counts_as_ended_and_a_stop_wins()
    {
        using var never = new ManualResetEvent(false);
        Assert.True(RecoveryWatcher.WaitForParent(int.MaxValue - 1, never)); // No such process.
        using var stopped = new ManualResetEvent(true);
        Assert.False(RecoveryWatcher.WaitForParent(int.MaxValue - 1, stopped));
        for (int i = 0; i < 40; i++)
        {
            using var dying = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
            Assert.True(RecoveryWatcher.WaitForParent(dying.Id, never));
        }
    }

    [Fact]
    public void A_failed_launch_is_retried_after_a_short_backoff()
    {
        int attempts = 0;
        using var watcher = new RecoveryWatcher(_ => ++attempts == 1
            ? throw new System.ComponentModel.Win32Exception(5) : Process.GetCurrentProcess());
        watcher.Follow(true);
        watcher.Follow(true);
        Assert.Equal(1, attempts);
        Thread.Sleep(1100);
        watcher.Follow(true);
        Assert.Equal(2, attempts);
        Assert.True(watcher.IsRunning);
    }
    [Theory]
    [InlineData(new[] { "--watch", "123" }, 123)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--watch" }, null)]
    [InlineData(new[] { "--watch", "nonsense" }, null)]
    [InlineData(new[] { "--watch", "-4" }, null)]
    [InlineData(new[] { "--watch", "4", "extra" }, null)]
    public void Only_the_exact_form_starts_a_watcher(string[] arguments, int? expected) =>
        Assert.Equal(expected, RecoveryWatcher.ParentFrom(arguments));

    [Fact]
    public void A_copy_starts_only_while_needed_and_once()
    {
        int started = 0;
        using var watcher = new RecoveryWatcher(_ =>
        {
            started++;
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false });
        });
        Process[] before = Process.GetProcessesByName("WinScreen");
        watcher.Follow(false);
        Assert.Equal(0, started);
        watcher.Follow(true);
        watcher.Follow(true);
        Assert.Equal(1, started);
        Assert.True(watcher.IsRunning);
        watcher.Follow(false);
        Assert.False(watcher.IsRunning);
        Assert.Equal(before.Length, Process.GetProcessesByName("WinScreen").Length);
    }

    [Fact]
    public void A_failed_start_is_not_a_failure_of_the_app()
    {
        using var watcher = new RecoveryWatcher(_ => throw new System.ComponentModel.Win32Exception(5));
        watcher.Follow(true);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public void A_host_that_is_not_the_app_starts_no_copy()
    {
        using var watcher = new RecoveryWatcher();
        watcher.Follow(true);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public void Being_told_to_stop_skips_recovery_and_a_parent_gone_runs_it()
    {
        using Process parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        bool recovered = false;
        RecoveryWatcher.Watch(parent.Id, () => recovered = true, @"Local\WinScreen.Tests." + Guid.NewGuid().ToString("N"));
        Assert.True(parent.HasExited);
        Assert.True(recovered);
    }

    [Fact]
    public void A_controller_reports_only_the_first_hide_and_the_last_reveal()
    {
        using var first = new Support.TargetProcess(new Rectangle(200, 200, 200, 150));
        using var second = new Support.TargetProcess(new Rectangle(420, 200, 200, 150));
        var windows = new WindowController([]);
        var seen = new List<bool>();
        windows.ConcealmentChanged += seen.Add;
        windows.Hide(first.Handle);
        windows.Hide(second.Handle);
        windows.Show(first.Handle);
        Assert.Equal([true], seen);
        windows.Show(second.Handle);
        Assert.Equal([true, false], seen);
        windows.RestoreAll();
    }
}
