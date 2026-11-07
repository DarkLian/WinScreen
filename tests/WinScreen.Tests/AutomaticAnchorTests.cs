using WinScreen.Anchors;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class CoverTrackerTests
{
    private static StackedWindow Window(nint handle, int left, int top, int width, int height, bool covers = true) =>
        new(handle, new Rectangle(left, top, width, height), covers, true);

    [Fact]
    public void A_window_behind_a_larger_one_is_covered_and_a_partly_visible_one_is_not()
    {
        Assert.Equal([(nint)2], CoverTracker.FullyCovered([Window(1, 0, 0, 800, 600), Window(2, 100, 100, 200, 200)]));
        Assert.Empty(CoverTracker.FullyCovered([Window(1, 0, 0, 800, 600), Window(2, 700, 100, 200, 200)]));
        Assert.Empty(CoverTracker.FullyCovered([Window(1, 100, 100, 200, 200), Window(2, 0, 0, 800, 600)]));
    }

    [Fact]
    public void Two_windows_side_by_side_can_cover_one_together_but_a_gap_shows_it()
    {
        Assert.Equal([(nint)3], CoverTracker.FullyCovered(
            [Window(1, 0, 0, 400, 600), Window(2, 400, 0, 400, 600), Window(3, 300, 100, 200, 200)]));
        Assert.Empty(CoverTracker.FullyCovered(
            [Window(1, 0, 0, 390, 600), Window(2, 400, 0, 400, 600), Window(3, 300, 100, 200, 200)]));
    }

    [Fact]
    public void See_through_windows_cover_nothing_but_can_be_covered()
    {
        Assert.Empty(CoverTracker.FullyCovered([Window(1, 0, 0, 800, 600, covers: false), Window(2, 100, 100, 200, 200)]));
        Assert.Equal([(nint)2], CoverTracker.FullyCovered([Window(1, 0, 0, 800, 600), Window(2, 100, 100, 200, 200, covers: false)]));
    }

    [Fact]
    public void Results_keep_the_stacking_order_and_a_pass_stays_quick()
    {
        var stack = new List<StackedWindow> { Window(1, 0, 0, 1920, 1080) };
        for (int i = 2; i < 300; i++)
            stack.Add(Window(i, i, i, 400, 300));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<nint> covered = CoverTracker.FullyCovered(stack);
        Assert.Equal(Enumerable.Range(2, 298).Select(value => (nint)value), covered);
        Assert.True(clock.ElapsedMilliseconds < 50, $"{clock.ElapsedMilliseconds} ms");
    }
}

public sealed class AnchorPreferenceTests
{
    [Fact]
    public void Older_settings_leave_automatic_Anchors_off()
    {
        AnchorPreferences loaded = AppSettings.Parse("{}").Anchors;
        Assert.False(loaded.Automatic);
        Assert.Empty(loaded.OnlyPrograms);
        Assert.Empty(loaded.ExcludedPrograms);
    }

    [Fact]
    public void Program_lists_match_with_or_without_the_extension()
    {
        var preferences = new AnchorPreferences { ExcludedPrograms = ["Secret.EXE"] };
        Assert.False(preferences.Allows("secret"));
        Assert.True(preferences.Allows("other"));
        var only = new AnchorPreferences { OnlyPrograms = ["browser"], ExcludedPrograms = ["secret"] };
        Assert.True(only.Allows("Browser.exe"));
        Assert.False(only.Allows("other"));
        Assert.Empty((new AnchorPreferences { OnlyPrograms = [" ", ""] }).Validated().OnlyPrograms);
    }
}

[Trait("Category", "RealWindows")]
public sealed class AutomaticAnchorTests
{
    private static readonly Rectangle Back = new(300, 240, 300, 200);
    private static readonly Rectangle Front = new(240, 180, 500, 400);

    private static void Pump(int milliseconds, Func<bool>? until = null)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end && until?.Invoke() != true)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static AutomaticAnchors Automatic(AnchorSet anchors, params nint[] only) =>
        new(anchors, only.Contains, () => false);

    [Fact]
    public void There_is_no_hook_while_off_and_the_hook_goes_when_turned_off()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            int before = WindowEvents.Active;
            using var anchors = new AnchorSet();
            using var automatic = Automatic(anchors);
            automatic.Configure(false);
            Assert.Equal(before, WindowEvents.Active);
            Assert.Null(automatic.Events);
            automatic.Configure(true);
            Assert.Equal(before + 1, WindowEvents.Active);
            automatic.Configure(true);
            Assert.Equal(before + 1, WindowEvents.Active);
            automatic.Configure(false);
            Assert.Equal(before, WindowEvents.Active);
        });
    }

    [Fact]
    public void An_Anchor_follows_a_window_being_covered_and_uncovered_within_the_time_allowed()
    {
        using var hidden = new TargetProcess(Back);
        using var cover = new TargetProcess(Front, "1E64C8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var anchors = new AnchorSet();
            using var automatic = Automatic(anchors, hidden.Handle);
            User32.SetWindowPos(cover.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            User32.SetWindowPos(hidden.Handle, cover.Handle, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            automatic.Configure(true);
            Assert.Equal([hidden.Handle], anchors.Automatic);
            Assert.False(anchors.Has(hidden.Handle));

            User32.SetWindowPos(hidden.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Pump(2000, () => anchors.Automatic.Count == 0);
            Assert.Empty(anchors.Automatic);
            Assert.True(clock.ElapsedMilliseconds < 300, $"Took {clock.ElapsedMilliseconds} ms to go.");

            User32.SetWindowPos(hidden.Handle, cover.Handle, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            clock.Restart();
            Pump(2000, () => anchors.Automatic.Count == 1);
            Assert.Single(anchors.Automatic);
            Assert.True(clock.ElapsedMilliseconds < 300, $"Took {clock.ElapsedMilliseconds} ms to come.");
        });
    }

    [Fact]
    public void A_kept_Anchor_stays_when_uncovered_and_a_removed_one_stays_away_until_then()
    {
        using var hidden = new TargetProcess(Back);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var anchors = new AnchorSet();
            anchors.SyncAutomatic([hidden.Handle]);
            Assert.True(anchors.Toggle(hidden.Handle));
            Assert.True(anchors.Has(hidden.Handle));
            anchors.SyncAutomatic([]);
            Assert.True(anchors.Has(hidden.Handle));
            Assert.False(anchors.Toggle(hidden.Handle));

            anchors.SyncAutomatic([hidden.Handle]);
            Application.OpenForms.OfType<Form>().Single(form => form.GetType().Name == "AnchorButton").Close();
            Assert.Empty(anchors.Automatic);
            anchors.SyncAutomatic([hidden.Handle]);
            Assert.Empty(anchors.Automatic);
            anchors.SyncAutomatic([]);
            anchors.SyncAutomatic([hidden.Handle]);
            Assert.Single(anchors.Automatic);
        });
    }

    [Fact]
    public void A_paused_pass_shows_nothing()
    {
        using var hidden = new TargetProcess(Back);
        using var cover = new TargetProcess(Front, "1E64C8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var anchors = new AnchorSet();
            anchors.SyncAutomatic([hidden.Handle]);
            using var automatic = new AutomaticAnchors(anchors, _ => true, () => true);
            automatic.Pass();
            Assert.Empty(anchors.Automatic);
        });
    }

    [Fact]
    public void Only_the_limit_of_covered_windows_get_an_Anchor()
    {
        var targets = Enumerable.Range(0, 9).Select(_ => new TargetProcess(new Rectangle(200, 200, 200, 150))).ToList();
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var anchors = new AnchorSet();
                List<nint> handles = targets.Select(target => target.Handle).ToList();
                anchors.SyncAutomatic(handles);
                Assert.Equal(handles.Take(8).Order(), anchors.Automatic.Order());
            });
        }
        finally { targets.ForEach(target => target.Dispose()); }
    }
}
