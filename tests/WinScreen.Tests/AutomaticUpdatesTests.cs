using WinScreen.Updates;
using Xunit;

namespace WinScreen.Tests;

public sealed class AutomaticUpdatesTests
{
    private sealed class Session : IUpdateSession
    {
        internal int Downloads;
        internal int Applications;
        internal bool Disposed;
        internal Func<CancellationToken, Task<bool>> Fetch = _ => Task.FromResult(true);

        public Task<bool> Download(CancellationToken cancellation)
        {
            Downloads++;
            return Fetch(cancellation);
        }

        public Task ApplyOnExit(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Applications++;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Disabled_means_no_source_creation_or_network_and_enabled_updates_only_apply_on_exit()
    {
        int sources = 0;
        var session = new Session();
        using var updates = new AutomaticUpdates(_ =>
        {
            sources++;
            return session;
        }, _ => { });
        await updates.Check();
        Assert.Equal(0, sources);
        updates.SetEnabled(true);
        await updates.Check();
        Assert.Equal(1, session.Downloads);
        Assert.Equal(0, session.Applications);
        await updates.Check();
        Assert.Equal(1, sources);
        await updates.ApplyOnExit();
        Assert.Equal(1, session.Applications);
    }

    [Fact]
    public async Task Disabling_after_download_prevents_installation()
    {
        var session = new Session();
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetEnabled(true);
        await updates.Check();
        updates.SetEnabled(false);
        await updates.ApplyOnExit();
        Assert.Equal(0, session.Applications);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Disabling_during_download_cancels_and_discards_the_old_session_even_if_reenabled()
    {
        var completed = new TaskCompletionSource<bool>();
        CancellationToken observed = default;
        var session = new Session
        {
            Fetch = cancellation =>
            {
                observed = cancellation;
                return completed.Task;
            }
        };
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetEnabled(true);
        Task checking = updates.Check();
        await updates.Check();
        Assert.Equal(1, session.Downloads);
        updates.SetEnabled(false);
        Assert.True(observed.IsCancellationRequested);
        updates.SetEnabled(true);
        completed.SetResult(true);
        await checking;
        await updates.ApplyOnExit();
        Assert.Equal(0, session.Applications);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task A_requested_check_says_how_it_went()
    {
        var session = new Session { Fetch = _ => Task.FromResult(false) };
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetEnabled(true);
        Assert.Equal("WinScreen is up to date.", await updates.CheckNow());
        session.Fetch = _ => throw new InvalidDataException("bad signature");
        Assert.StartsWith("Update check failed", await updates.CheckNow());
        session.Fetch = _ => Task.FromResult(true);
        Assert.StartsWith("Update ready", await updates.CheckNow());
    }

    [Fact]
    public async Task Network_or_verification_failure_keeps_the_app_running_and_allows_retry()
    {
        var session = new Session { Fetch = _ => throw new InvalidDataException("bad signature") };
        var messages = new List<string>();
        using var updates = new AutomaticUpdates(_ => session, messages.Add);
        updates.SetEnabled(true);
        await updates.Check();
        await updates.ApplyOnExit();
        Assert.Single(messages);
        Assert.Equal(0, session.Applications);
        Assert.True(session.Disposed);
        session.Fetch = _ => Task.FromResult(false);
        await updates.Check();
        Assert.Equal(2, session.Downloads);
    }

    [Fact]
    public async Task A_requested_check_works_with_automatic_updates_off_and_the_timer_stays_quiet()
    {
        int sources = 0;
        var session = new Session();
        var messages = new List<string>();
        using var updates = new AutomaticUpdates(_ =>
        {
            sources++;
            return session;
        }, messages.Add);
        await updates.Check();
        Assert.Equal(0, sources);
        Assert.StartsWith("Update ready", await updates.CheckNow());
        Assert.Empty(messages);
        await updates.ApplyOnExit();
        Assert.Equal(1, session.Applications);
    }
}
