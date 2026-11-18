using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

/// <summary>The picture that grows from a view into its original's place for a drag (12.6): the arithmetic.</summary>
public sealed class GrowingPictureTests
{
    private static readonly Rectangle View = new(1000, 600, 320, 200), Window = new(700, 300, 960, 600);

    [Fact]
    public void It_starts_on_the_view_ends_on_the_window_and_stays_there()
    {
        Assert.Equal(View, GrowingPicture.Frame(View, Window, 0));
        Assert.Equal(Window, GrowingPicture.Frame(View, Window, 1));
        Assert.Equal(Window, GrowingPicture.Frame(View, Window, 1.7));
        Assert.Equal(View, GrowingPicture.Frame(View, Window, -0.2));
    }

    [Fact]
    public void It_goes_quickly_at_first_and_settles_at_the_end()
    {
        Rectangle half = GrowingPicture.Frame(View, Window, 0.5);
        Assert.True(half.Width - View.Width > (Window.Width - View.Width) * 0.8, $"Halfway it is only {half}.");
        int previous = View.Width;
        for (int step = 1; step <= 10; step++)
        {
            int width = GrowingPicture.Frame(View, Window, step / 10.0).Width;
            Assert.True(width >= previous, $"It shrank at step {step}.");
            previous = width;
        }
    }

    /// <summary>Takes a real picture of the screen: the first version passed a screen-copy value .NET refuses, and crashed a drop.</summary>
    [Fact]
    [Trait("Category", "RealWindows")]
    public void Taking_the_picture_works_and_with_no_window_out_nothing_is_revealed()
    {
        bool revealed = false;
        using GrowingPicture? growing = GrowingPicture.Start(new Rectangle(0, 0, 96, 64), () => null, () => revealed = true);
        Assert.Equal(growing is null, revealed); // Animations off: revealed at once; on: it waits for a window that never comes.
        Thread.Sleep(300);
        Assert.Equal(growing is null, revealed);
    }

    [Fact]
    public void The_pressed_spot_stays_under_the_pointer_all_the_way()
    {
        // The window is placed so the spot the view showed under the pointer is under it again: in every frame between,
        // that spot (the same fraction of the picture) stays there.
        Point pointer = new(1100, 680);
        double u = (pointer.X - View.Left) / (double)View.Width, v = (pointer.Y - View.Top) / (double)View.Height;
        Rectangle window = new((int)Math.Round(pointer.X - u * 960), (int)Math.Round(pointer.Y - v * 600), 960, 600);
        for (int step = 0; step <= 20; step++)
        {
            Rectangle frame = GrowingPicture.Frame(View, window, step / 20.0);
            double x = frame.Left + u * frame.Width, y = frame.Top + v * frame.Height;
            Assert.True(Math.Abs(x - pointer.X) <= 1.5 && Math.Abs(y - pointer.Y) <= 1.5, $"Step {step}: the spot is at {x:0.#},{y:0.#} in {frame}.");
        }
    }
}
