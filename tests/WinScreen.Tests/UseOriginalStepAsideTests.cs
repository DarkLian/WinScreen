using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Use it, then hide again: while the original is in use the view is out of sight, so it can't cover the window being
/// used, and it comes back as it was once the original hides again, or when Keep hidden is turned off meanwhile.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class UseOriginalStepAsideTests
{
    private static bool Pump(Func<bool> done, int milliseconds) =>
        Desktop.WaitFor(done, milliseconds, () => { Application.DoEvents(); Thread.Sleep(10); });

    [Fact]
    public void The_view_steps_aside_while_the_original_is_in_use_and_comes_back_after()
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 480, 360), "C81E1E");
        using var other = new TargetProcess(new Rectangle(760, 500, 300, 200), "1E64C8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            using var view = new PipWindow(source.Handle, new WindowController([]));
            view.ShowOnTop();
            view.Location = new Point(300, 220); // Over the original's place, as a view often is.
            view.SetOriginalHidden(true);
            Assert.True(Pump(() => view.KeepsUpdating, 3000));

            // Used, then left for another window: the view is away meanwhile, then back.
            view.UseOriginal();
            Pump(() => false, 300);
            Assert.True(view.UsingOriginal);
            Assert.True(view.SteppedAside);
            Assert.False(User32.IsWindowVisible(view.Handle), "The view still covers the original in use.");
            Desktop.Activate(source.Handle);
            Pump(() => false, 500);
            Desktop.Activate(other.Handle);
            Assert.True(Pump(() => !view.UsingOriginal, 8000), "The original didn't hide again.");
            Assert.True(User32.IsWindowVisible(view.Handle), "The view didn't come back.");
            Assert.False(view.SteppedAside);

            // Keep hidden turned off while the original is in use: the view comes back too.
            view.UseOriginal();
            Pump(() => false, 300);
            Assert.False(User32.IsWindowVisible(view.Handle));
            view.SetOriginalHidden(false);
            Pump(() => false, 300);
            Assert.True(User32.IsWindowVisible(view.Handle), "The view didn't come back when Keep hidden went off.");
            Assert.False(view.SteppedAside);
        });
    }
}
