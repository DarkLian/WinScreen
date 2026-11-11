using WinScreen.Anchors;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
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

public sealed class AnchorPolishTests
{
    [Fact]
    public void The_most_recent_Anchor_is_largest_and_most_solid_and_each_older_one_less_so()
    {
        Assert.Equal((1.0, 1.0), AnchorEmphasis.For(0, 1));
        var steps = Enumerable.Range(0, 5).Select(rank => AnchorEmphasis.For(rank, 5)).ToList();
        Assert.Equal((1.0, 1.0), steps[0]);
        for (int rank = 1; rank < 5; rank++)
        {
            Assert.True(steps[rank].Size < steps[rank - 1].Size);
            Assert.True(steps[rank].Opacity < steps[rank - 1].Opacity);
        }

        Assert.True(steps[4].Size >= 0.69 && steps[4].Opacity >= 0.59, "An old Anchor must stay usable.");
    }

    [Fact]
    public void Near_a_window_means_beside_it_or_inside_at_the_screen_edge_and_skips_taken_spots()
    {
        var area = new Rectangle(0, 0, 1920, 1032);
        Assert.Equal(new Point(508, 100), AnchorGeometry.NearWindow(new Rectangle(100, 100, 400, 300), 48, 8, area, _ => true));
        Assert.Equal(new Point(1844, 100), AnchorGeometry.NearWindow(new Rectangle(1500, 100, 400, 300), 48, 8, area, _ => true));
        Assert.Equal(new Point(508, 156), AnchorGeometry.NearWindow(new Rectangle(100, 100, 400, 300), 48, 8, area, spot => spot.Top != 100));
        Assert.Null(AnchorGeometry.NearWindow(new Rectangle(100, 100, 400, 300), 48, 8, area, _ => false));
    }

    [Fact]
    public void Older_settings_keep_todays_look_and_values_stay_in_range()
    {
        AnchorPreferences old = AppSettings.Parse("{}").Anchors;
        Assert.Equal(AnchorSide.Right, old.Side);
        Assert.Equal(8, old.MaxAutomatic);
        Assert.Equal(48, old.Size);
        Assert.Equal(AnchorLook.Dark, old.Style);
        Assert.False(old.AvoidContent);
        AnchorPreferences wild = AppSettings.Parse("""{"Anchors":{"MaxAutomatic":99,"Size":4,"Side":7,"Style":9}}""").Anchors;
        Assert.Equal(10, wild.MaxAutomatic);
        Assert.Equal(32, wild.Size);
        Assert.Equal(AnchorSide.Right, wild.Side);
        Assert.Equal(AnchorLook.Dark, wild.Style);
    }

    [Fact]
    public void The_Settings_page_shows_and_saves_every_Anchor_choice()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            AppSettings? saved = null;
            var settings = new AppSettings { Anchors = new()
            {
                Automatic = true, Side = AnchorSide.NearWindow, Style = AnchorLook.Light, MaxAutomatic = 3, Size = 60,
                RecencyEmphasis = false, AvoidContent = true, OnlyPrograms = ["one"], ExcludedPrograms = ["two"],
            } };
            using var window = new SettingsWindow(settings, new StartWithWindows(@"Software\WinScreenTests\Anchors", NoTasks.Instance),
                draft => { saved = draft; return []; });
            Assert.True(window.SaveChanges());
            AnchorPreferences kept = saved!.Anchors;
            Assert.True(kept.Automatic);
            Assert.Equal(AnchorSide.NearWindow, kept.Side);
            Assert.Equal(AnchorLook.Light, kept.Style);
            Assert.Equal(3, kept.MaxAutomatic);
            Assert.Equal(60, kept.Size);
            Assert.False(kept.RecencyEmphasis);
            Assert.True(kept.AvoidContent);
            Assert.Equal(["one"], kept.OnlyPrograms);
            Assert.Equal(["two"], kept.ExcludedPrograms);
        });
    }
}

[Trait("Category", "RealWindows")]
public sealed class AutomaticAnchorTests
{
    private static List<Form> Buttons() => Application.OpenForms.OfType<Form>().Where(form => form.GetType().Name == "AnchorButton").ToList();

    [Fact]
    public void Avoidance_moves_only_automatic_Anchors_and_leaves_them_out_of_capture()
    {
        using var automaticTarget = new TargetProcess(Back);
        using var manualTarget = new TargetProcess(Front);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var anchors = new AnchorSet();
            anchors.Configure(new AnchorPreferences { AvoidContent = true });
            anchors.Toggle(manualTarget.Handle);
            anchors.SyncAutomatic([automaticTarget.Handle]);
            Form manual = Buttons()[0], automatic = Buttons()[1];
            Assert.True(User32.GetWindowDisplayAffinity(automatic.Handle, out uint affinity) && affinity == User32.WDA_EXCLUDEFROMCAPTURE);
            Point manualAt = manual.Location, automaticAt = automatic.Location;

            bool[,] Busy(Rectangle area)
            {
                int rows = area.Height / 16, columns = area.Width / 16;
                var cells = new bool[rows, columns];
                for (int row = 0; row < rows; row++)
                    for (int column = columns / 2; column < columns; column++)
                        cells[row, column] = true; // Content on the right, a calm left half.
                return cells;
            }

            anchors.AvoidContent(Busy);
            Assert.Equal(manualAt, manual.Location);
            Assert.NotEqual(automaticAt, automatic.Location);
            Assert.True(automatic.Left < Screen.PrimaryScreen!.WorkingArea.Width / 2);

            anchors.Configure(new AnchorPreferences());
            Assert.True(User32.GetWindowDisplayAffinity(automatic.Handle, out affinity) && affinity == User32.WDA_NONE);
        });
    }

    [Fact]
    public void Size_style_side_and_limit_apply_and_recency_shrinks_older_Anchors()
    {
        var targets = Enumerable.Range(0, 4).Select(_ => new TargetProcess(new Rectangle(200, 200, 200, 150))).ToList();
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var anchors = new AnchorSet();
                anchors.Configure(new AnchorPreferences { Size = 64, Side = AnchorSide.Left, Style = AnchorLook.Light, MaxAutomatic = 3 });
                anchors.SyncAutomatic(targets.Select(target => target.Handle).ToList());
                List<Form> shown = Buttons();
                Assert.Equal(3, shown.Count);
                // Height, since these test windows share one icon and so each Anchor is widened for its title.
                Assert.Equal(64, shown[0].ClientSize.Height);
                Assert.True(shown[2].ClientSize.Height < shown[1].ClientSize.Height && shown[1].ClientSize.Height < 64);
                Assert.True(shown[2].Opacity < shown[0].Opacity);
                Assert.Equal(Color.FromArgb(240, 240, 240), shown[0].BackColor);
                Assert.True(shown[0].Left < Screen.PrimaryScreen!.WorkingArea.Width / 2);
            });
        }
        finally { targets.ForEach(target => target.Dispose()); }
    }

    [Fact]
    public void An_Anchor_marks_a_window_that_has_a_view()
    {
        using var target = new TargetProcess(Back);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            bool hasView = false;
            using var anchors = new AnchorSet { HasView = _ => hasView };
            anchors.Toggle(target.Handle);
            Form button = Buttons()[0];
            using var plain = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(plain, new Rectangle(Point.Empty, button.Size));
            hasView = true;
            anchors.Configure(new AnchorPreferences());
            using var marked = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(marked, new Rectangle(Point.Empty, button.Size));
            Assert.NotEqual(plain.GetPixel(button.Width - 2, button.Height - 2), marked.GetPixel(button.Width - 2, button.Height - 2));
        });
    }

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
