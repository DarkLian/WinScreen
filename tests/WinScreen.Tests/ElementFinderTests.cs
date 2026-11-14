using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class ElementFinderTests
{
    [Fact]
    public void A_window_with_no_controls_answers_with_its_own_rectangle()
    {
        var spot = new Rectangle(240, 200, 400, 300);
        using var target = new TargetProcess(spot);
        Rectangle? found = ElementFinder.Find(target.Handle, new Point(400, 300));
        Assert.NotNull(found);
        Assert.Equal(spot.Size, found.Value.Size);
    }

    [Fact]
    public void A_control_inside_a_window_is_found_by_its_own_rectangle()
    {
        var spot = new Rectangle(240, 200, 400, 300);
        using var target = new TargetProcess(spot);
        target.AddButton();
        Rectangle? found = null;
        Assert.True(Desktop.WaitFor(() =>
        {
            found = ElementFinder.Find(target.Handle, new Point(spot.Left + 60, spot.Top + 50));
            return found?.Size == new Size(140, 50);
        }, 3000), found?.ToString() ?? "no answer");
        Assert.Equal(new Point(spot.Left + 20, spot.Top + 30), found!.Value.Location);
    }

    /// <summary>A control found once is found again after the window is resized and it has moved (roadmap step 8.1).</summary>
    [Fact]
    public void A_control_is_found_again_where_it_moved_to()
    {
        var spot = new Rectangle(240, 200, 400, 300);
        using var target = new TargetProcess(spot);
        target.AddCornerControl();
        FoundElement? found = null;
        Assert.True(Desktop.WaitFor(() =>
        {
            Rectangle bounds = Desktop.BoundsOf(target.Handle);
            found = ElementFinder.FindElement(target.Handle, new Point(bounds.Right - 60, bounds.Bottom - 40));
            return found?.Bounds.Size == new Size(140, 50);
        }, 3000), found?.ToString() ?? "no answer");
        Rectangle before = found!.Bounds;

        target.Resize(600, 420);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(target.Handle).Width == 600, 3000), "The window wasn't resized.");
        Rectangle? now = null;
        Assert.True(Desktop.WaitFor(() => (now = ElementFinder.Locate(found)) == before with { X = before.X + 200, Y = before.Y + 120 }, 3000),
            $"Found at {now?.ToString() ?? "nothing"}, was {before}.");
    }

    [Fact]
    public void A_window_that_has_gone_is_no_answer_rather_than_a_failure()
    {
        var target = new TargetProcess(new(240, 200, 400, 300));
        nint window = target.Handle;
        target.Dispose();
        Assert.True(Desktop.WaitFor(() => !Native.User32.IsWindow(window), 3000));
        Assert.Null(ElementFinder.Find(window, new Point(400, 300)));
    }

    [Fact]
    public void A_finder_stops_when_the_selection_ends()
    {
        using var overlay = new Form { ShowInTaskbar = false, Size = new Size(200, 200) };
        overlay.Show();
        var finder = new ElementFinder(overlay.Handle);
        int threads = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        var answered = new ManualResetEventSlim();
        finder.Found += _ => answered.Set();
        finder.Ask(Cursor.Position);
        Assert.True(answered.Wait(5000));
        finder.Dispose();
        finder.Ask(Cursor.Position);
        Assert.True(Desktop.WaitFor(() => System.Diagnostics.Process.GetCurrentProcess().Threads.Count <= threads, 3000));
    }

    [Fact]
    public void Concurrent_questions_and_repeated_disposal_are_harmless()
    {
        using var finder = new ElementFinder(0);
        Parallel.Invoke(
            () => { for (int i = 0; i < 1000; i++) finder.Ask(new Point(i, i)); },
            () => { for (int i = 0; i < 100; i++) finder.Dispose(); });
        // Let the worker dispose its wait handle before asking again.
        Thread.Sleep(100);
        finder.Ask(Point.Empty);
        finder.Dispose();
    }
}
