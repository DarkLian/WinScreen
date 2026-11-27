using WinScreen.Pip;
using Xunit;
using Press = WinScreen.Pip.DragArrival.Press;

namespace WinScreen.Tests;

/// <summary>When a drag from another program, resting on a view's picture, is handed to the original.</summary>
public sealed class DragArrivalTests
{
    private static readonly Press FromExplorer = new(FromElsewhere: true, Thread: 7, Process: 70, Class: "DirectUIHWND");
    private const long Start = 10_000;
    private static readonly Press FromOwnWindow = new(FromElsewhere: false, Thread: 8, Process: 80, Class: "WindowsForms");

    private sealed class Run(Press press, bool threadDrags = true, bool processDrags = false)
    {
        internal readonly DragArrival Arrival = new(thread => threadDrags && thread == press.Thread, process => processDrags && process == press.Process);
        internal bool Over = true;

        // Windows' tick count is never 0, which the arrival takes for "no press yet".
        internal bool At(long now, bool down = true) => Arrival.Step(Start + now, down, () => press, () => Over);
    }

    // The first tick only notes where the button went down; the drag must then rest DragArrival.Rests on the picture.
    [Fact]
    public void A_drag_from_elsewhere_resting_on_the_picture_is_handed_over_once()
    {
        var run = new Run(FromExplorer);
        Assert.False(run.At(0));
        Assert.False(run.At(50)); // Seen running: it starts resting now.
        Assert.False(run.At(50 + DragArrival.Rests - 1));
        Assert.True(run.At(50 + DragArrival.Rests));
        Assert.False(run.At(50 + DragArrival.Rests + 50));
        Assert.False(run.At(1000)); // Once per press.
    }

    [Fact]
    public void A_new_press_can_hand_over_again()
    {
        var run = new Run(FromExplorer);
        run.At(0); run.At(50);
        Assert.True(run.At(50 + DragArrival.Rests));
        run.At(400, down: false);
        run.At(500); run.At(550);
        Assert.True(run.At(550 + DragArrival.Rests));
    }

    [Fact]
    public void A_button_pressed_on_WinScreen_itself_is_never_a_drag_arriving()
    {
        var run = new Run(FromOwnWindow);
        for (long now = 0; now <= 2000; now += 50) Assert.False(run.At(now));
    }

    [Fact]
    public void Leaving_the_picture_starts_the_rest_again()
    {
        var run = new Run(FromExplorer);
        run.At(0); run.At(50);
        run.Over = false;
        Assert.False(run.At(100));
        run.Over = true;
        Assert.False(run.At(150)); // Resting from here.
        Assert.False(run.At(150 + DragArrival.Rests - 1));
        Assert.True(run.At(150 + DragArrival.Rests));
    }

    [Fact]
    public void A_button_held_with_no_drag_and_drop_running_is_not_a_drag()
    {
        var run = new Run(FromExplorer, threadDrags: false);
        for (long now = 0; now <= 2000; now += 50) Assert.False(run.At(now));
    }

    // Some programs run their drag on another thread than the pressed window's.
    [Fact]
    public void A_drag_running_elsewhere_in_the_program_counts_too()
    {
        var run = new Run(FromExplorer, threadDrags: false, processDrags: true);
        run.At(0);
        Assert.False(run.At(50));
        Assert.True(run.At(50 + DragArrival.Rests));
    }

    [Fact]
    public void The_program_is_asked_at_most_every_quarter_second()
    {
        int asked = 0;
        var arrival = new DragArrival(_ => false, _ => { asked++; return false; });
        for (long now = 0; now < 1000; now += 50) arrival.Step(Start + now, true, () => FromExplorer, () => true);
        Assert.Equal(4, asked); // At 50, 300, 550 and 800.
    }
}
