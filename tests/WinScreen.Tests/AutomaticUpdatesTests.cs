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
        internal string? Newer = "9.9.9";
        internal int Looks;

        internal Task<string?>? Answer;

        public Task<string?> NewerVersion(CancellationToken cancellation)
        {
            Looks++;
            return Answer ?? Task.FromResult(Newer);
        }

        internal UpdateProgress[] Steps = [];

        public Task<bool> Download(CancellationToken cancellation, Action<UpdateProgress>? progress = null)
        {
            Downloads++;
            foreach (UpdateProgress step in Steps) progress?.Invoke(step);
            return Fetch(cancellation);
        }

        internal bool? Restarted;

        public Task ApplyOnExit(CancellationToken cancellation, bool restart)
        {
            cancellation.ThrowIfCancellationRequested();
            Applications++;
            Restarted = restart;
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

    // The owner, 23 Nov: a check showed nothing until "close WinScreen to install"; now each step is told as it comes.
    [Fact]
    public async Task A_check_tells_each_step_and_names_the_version_that_is_ready()
    {
        var session = new Session
        {
            Steps = [new(UpdateStage.Checking), new(UpdateStage.Downloading, "9.9.9", 0), new(UpdateStage.Downloading, "9.9.9", 60),
                new(UpdateStage.Downloading, "9.9.9", 100), new(UpdateStage.Verifying, "9.9.9")],
        };
        var heard = new List<UpdateProgress>();
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.Progress += heard.Add;
        Assert.Null(updates.ReadyVersion);
        Assert.StartsWith("Update ready", await updates.CheckNow());
        Assert.Equal(UpdateStage.Checking, heard[0].Stage);
        Assert.Equal([0, 60, 100], heard.Where(step => step.Stage == UpdateStage.Downloading).Select(step => step.Percent));
        Assert.Equal(UpdateStage.Verifying, heard[^1].Stage);
        Assert.Equal("9.9.9", updates.ReadyVersion);
    }

    // A second click while a check runs waits for it and answers, rather than saying a check is already under way.
    [Fact]
    public async Task A_check_asked_for_while_one_runs_waits_for_it_and_answers()
    {
        var finish = new TaskCompletionSource<bool>();
        var session = new Session { Fetch = _ => finish.Task };
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetEnabled(true);
        Task timed = updates.Check();
        Task<string> asked = updates.CheckNow();
        Assert.False(asked.IsCompleted);
        finish.SetResult(true);
        await timed;
        Assert.StartsWith("Update ready", await asked);
        Assert.Equal(1, session.Downloads);
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

    [Fact]
    public async Task Telling_only_looks_and_says_each_new_version_once()
    {
        var session = new Session();
        var told = new List<string>();
        using var updates = new AutomaticUpdates(_ => session, _ => { }, told.Add);
        updates.SetTelling(true);
        await updates.Check();
        await updates.Check();
        Assert.Equal(["9.9.9"], told);
        Assert.Equal(0, session.Downloads);
        session.Newer = "10.0.0";
        await updates.Check();
        Assert.Equal(["9.9.9", "10.0.0"], told);
        await updates.ApplyOnExit();
        Assert.Equal(0, session.Applications);
    }

    [Fact]
    public async Task Off_means_no_session_at_all()
    {
        int sources = 0;
        using var updates = new AutomaticUpdates(_ => { sources++; return new Session(); }, _ => { }, _ => { });
        updates.SetTelling(true);
        updates.SetTelling(false);
        await updates.Check();
        Assert.Equal(0, sources);
    }

    [Fact]
    public async Task A_look_the_person_asks_for_works_whatever_the_setting_and_says_when_it_failed()
    {
        var session = new Session();
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        Assert.Equal(("9.9.9", false), await updates.LookNow());
        session.Newer = null;
        Assert.Equal(((string?)null, false), await updates.LookNow());
        using var failing = new AutomaticUpdates(_ => throw new HttpRequestException("offline"), _ => { });
        Assert.Equal(((string?)null, true), await failing.LookNow());
    }

    /// <summary>
    /// A look the person asks for while the timer's is under way waits for it, then looks itself: it must not be
    /// taken for "up to date", nor end with no answer when the timer's says nothing new.
    /// </summary>
    [Fact]
    public async Task A_look_asked_for_while_one_is_under_way_waits_and_answers()
    {
        var answer = new TaskCompletionSource<string?>();
        var session = new Session { Answer = answer.Task };
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetTelling(true);
        Task timed = updates.Check();
        Task<(string? Version, bool Failed)> asked = updates.LookNow();
        Assert.False(asked.IsCompleted);
        Assert.Equal(1, session.Looks);
        answer.SetResult("9.9.9");
        await timed;
        Assert.Equal(("9.9.9", false), await asked);
        Assert.Equal(2, session.Looks);
    }

    [Fact]
    public async Task A_look_after_closing_is_not_taken_for_up_to_date()
    {
        var updates = new AutomaticUpdates(_ => new Session(), _ => { });
        updates.Dispose();
        Assert.Equal(((string?)null, true), await updates.LookNow());
    }

    [Fact]
    public async Task Restart_to_update_is_offered_only_for_a_downloaded_update_and_asks_the_installer_to_start_it_again()
    {
        var session = new Session();
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        Assert.False(updates.Ready);
        updates.SetEnabled(true);
        await updates.Check();
        Assert.True(updates.Ready);
        await updates.ApplyOnExit(restart: true);
        Assert.True(session.Restarted);
    }

    [Fact]
    public async Task An_ordinary_exit_installs_without_starting_again()
    {
        var session = new Session();
        using var updates = new AutomaticUpdates(_ => session, _ => { });
        updates.SetEnabled(true);
        await updates.Check();
        await updates.ApplyOnExit();
        Assert.False(session.Restarted);
    }
}
