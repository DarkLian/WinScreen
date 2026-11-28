using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Whether the input worker may hand a left press in a view to the real window for a drag, whatever order the hook's
/// release and the worker's move come in. A quick click on a link with a twitch was picked up after its release (TD-77).
/// </summary>
public sealed class LeftPressTests
{
    [Fact]
    public void A_press_already_let_go_is_not_handed_over()
    {
        var left = new LeftPress();
        long press = left.Pressed();
        Assert.False(left.Released());
        Assert.False(left.HandOver(press));
    }

    [Fact]
    public void A_held_press_is_handed_over_and_its_release_is_the_real_windows()
    {
        var left = new LeftPress();
        long press = left.Pressed();
        Assert.True(left.HandOver(press));
        Assert.True(left.Released());
    }

    [Fact]
    public void A_move_left_from_an_earlier_press_does_not_hand_over_a_newer_one()
    {
        var left = new LeftPress();
        long first = left.Pressed();
        Assert.False(left.Released());
        long second = left.Pressed();
        Assert.False(left.HandOver(first));
        Assert.True(left.HandOver(second));
        Assert.True(left.Released());
    }

    [Fact]
    public void A_press_is_handed_over_once_and_the_next_one_starts_held()
    {
        var left = new LeftPress();
        long first = left.Pressed();
        Assert.True(left.HandOver(first));
        Assert.False(left.HandOver(first));
        Assert.True(left.Released());
        // The press the view got itself (PipInteraction.TakePress) counts the same.
        long second = left.Pressed();
        Assert.False(left.Released());
        Assert.False(left.HandOver(second));
    }

    [Fact]
    public void A_release_racing_the_hand_over_is_either_a_click_or_the_real_windows_never_lost()
    {
        for (int round = 0; round < 2000; round++)
        {
            var left = new LeftPress();
            long press = left.Pressed();
            bool handedOver = false, releasedHandedOver = false;
            using var start = new Barrier(2);
            var worker = new Thread(() => { start.SignalAndWait(); handedOver = left.HandOver(press); });
            worker.Start();
            start.SignalAndWait();
            releasedHandedOver = left.Released();
            worker.Join();
            // Handed over: the hook lets the release through to the real window. Not: the queued release makes the click.
            Assert.Equal(handedOver, releasedHandedOver);
        }
    }
}
