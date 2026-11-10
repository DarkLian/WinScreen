using WinScreen.Localization;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipInteractionStateTests
{
    private static ToolStripMenuItem Item(PipWindow view, string key) =>
        view.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == UiText.T(key));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interactive_mode_locks_hiding_and_restores_the_previous_preference(bool previouslyHidden)
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.SetOriginalHidden(previouslyHidden);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                view.SetInteractive(true);
                view.SetInteractive(true); // Idempotent: must not replace the saved preference.
                Assert.True(Item(view, "menu.view.interactive").Checked);
                Assert.True(Item(view, "menu.original.keepHidden").Checked);
                Assert.False(Item(view, "menu.original.keepHidden").Enabled);
                view.SetOriginalHidden(false);
                Assert.True(windows.IsHidden(source.Handle));
                view.SetInteractive(false);
                Assert.False(Item(view, "menu.view.interactive").Checked);
                Assert.True(Item(view, "menu.original.keepHidden").Enabled);
                Assert.Equal(previouslyHidden, Item(view, "menu.original.keepHidden").Checked);
                Assert.Equal(previouslyHidden, windows.IsHidden(source.Handle));
            }
            view.SetInteractive(true);
            view.GoToSource();
            Assert.False(windows.IsHidden(source.Handle));
            Assert.False(Item(view, "menu.view.interactive").Checked);
            Assert.False(Item(view, "menu.original.keepHidden").Checked);
            Assert.True(Item(view, "menu.original.keepHidden").Enabled);
            view.Close();
        });
    }

    [Fact]
    public void Recovery_updates_mode_state_while_a_view_is_suspended()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.SetInteractive(true);
            view.SuspendView();
            windows.RestoreAll();
            Assert.True(Desktop.WaitFor(() => !Item(view, "menu.view.interactive").Checked, 2000,
                () => { Application.DoEvents(); Thread.Sleep(10); }));
            Assert.False(Item(view, "menu.original.keepHidden").Checked);
            Assert.True(Item(view, "menu.original.keepHidden").Enabled);
        });
    }

    [Fact]
    public void A_failed_enable_before_capture_is_ready_leaves_both_modes_off()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.SetInteractive(true);
            Assert.False(Item(view, "menu.view.interactive").Checked);
            Assert.False(Item(view, "menu.original.keepHidden").Checked);
            Assert.True(Item(view, "menu.original.keepHidden").Enabled);
            Assert.False(windows.IsHidden(source.Handle));
        });
    }

    [Fact]
    public void Recovery_turns_interactive_mode_off_and_closure_restores_the_source()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        Rectangle original = Desktop.BoundsOf(source.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.SetInteractive(true);
            windows.RestoreAll();
            Assert.True(Desktop.WaitFor(() => !Item(view, "menu.view.interactive").Checked, 2000,
                () => { Application.DoEvents(); Thread.Sleep(10); }));
            Assert.False(Item(view, "menu.original.keepHidden").Checked);
            Assert.True(Item(view, "menu.original.keepHidden").Enabled);
            view.SetInteractive(true);
            view.Close();
            Assert.False(windows.IsHidden(source.Handle));
            Assert.Equal(original, Desktop.BoundsOf(source.Handle));
        });
    }
}
