using WinScreen.Localization;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The answer for an app that doesn't work through a view: its hidden original is shown to be used as it is, and is
/// hidden again once another window is in use.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class UseOriginalTests
{
    private static ToolStripMenuItem Item(PipWindow view, string key) =>
        view.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == UiText.T(key));

    private static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Activate(nint window)
    {
        User32.SetForegroundWindow(window);
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == window, 3000, Application.DoEvents),
            $"0x{window:X} didn't come to the front.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_original_is_shown_where_it_was_and_hidden_again_once_another_window_is_used(bool interactive)
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        using var other = new TargetProcess(new Rectangle(820, 120, 300, 300), "1E1EC8", "WinScreen other target");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Rectangle home = Desktop.BoundsOf(source.Handle);
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            if (interactive) view.SetInteractive(true);
            else view.SetOriginalHidden(true);
            Assert.True(windows.IsHidden(source.Handle));
            Pump(300);

            view.UseOriginal();
            Assert.True(view.UsingOriginal);
            Assert.False(windows.IsHidden(source.Handle));
            Assert.False(WindowInfo.IsConcealed(source.Handle));
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
            Assert.False(Item(view, "menu.view.interactive").Checked);
            Activate(source.Handle);

            // It stays for as long as it is the window in use.
            Pump(1200);
            Assert.True(view.UsingOriginal);
            Assert.False(windows.IsHidden(source.Handle));

            Activate(other.Handle);
            Assert.True(Desktop.WaitFor(() => windows.IsHidden(source.Handle), 3000, Application.DoEvents), "It wasn't hidden again.");
            Assert.False(view.UsingOriginal);
            Assert.True(windows.IsParked(source.Handle));
            Assert.Equal(interactive, Item(view, "menu.view.interactive").Checked);
            Assert.True(Item(view, "menu.original.keepHidden").Checked);

            // Going to the original afterwards is a plain choice again: it stays shown.
            view.UseOriginal();
            Activate(source.Handle);
            view.GoToSource();
            Assert.False(view.UsingOriginal);
            Activate(other.Handle);
            Pump(1200);
            Assert.False(windows.IsHidden(source.Handle));
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
            Assert.Equal(PipStateProblem.None, view.FirstStateProblem);
            view.Close();
        });
    }

    /// <summary>
    /// After a press that didn't get through, the view's chip offers the original, and a click on the chip, made during
    /// a visit, shows it without going to the app. From the third time for a program, it offers to list the program.
    /// </summary>
    [Fact]
    public void The_chip_offers_the_original_and_a_click_on_it_shows_the_original()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        using var other = new TargetProcess(new Rectangle(820, 120, 300, 300), "1E1EC8", "WinScreen other target");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            var listed = new List<string>();
            view.AlwaysShowOriginal += listed.Add;
            view.ShowOnTop();
            view.SetInteractive(true);
            Pump(300);
            Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
            Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null, 3000, Application.DoEvents), "No visit started.");

            view.Offer(PressOffer.UseOriginal);
            Assert.True(Desktop.WaitFor(() => view.ChipBounds is not null, 2000, Application.DoEvents), "The chip didn't show.");
            Assert.Equal(PipChipState.OriginalOffered, view.ChipShown);
            Rectangle chip = view.ChipBounds!.Value;
            Desktop.SlideMouse(new Point(chip.Left + chip.Width / 2, chip.Top + chip.Height / 2));
            Pump(200);
            Desktop.MouseButton(true);
            Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => view.UsingOriginal, 3000, Application.DoEvents), "A click on the chip didn't show the original.");
            Assert.False(windows.IsHidden(source.Handle));
            Assert.DoesNotContain(PressOutcome.Delivered, view.PressOutcomes); // The click was the chip's, not the app's.
            Assert.Equal(PipChipState.None, view.ChipShown);
            Assert.Empty(listed);

            Activate(other.Handle);
            Assert.True(Desktop.WaitFor(() => windows.IsHidden(source.Handle), 3000, Application.DoEvents), "It wasn't hidden again.");
            Assert.True(Item(view, "menu.view.interactive").Checked);

            view.Offer(PressOffer.AlwaysShowOriginal);
            Assert.True(Desktop.WaitFor(() => view.ChipShown == PipChipState.AlwaysOriginalOffered, 2000, Application.DoEvents),
                "The chip didn't offer to always show the original.");
            view.OnChipChosen(PipChipState.AlwaysOriginalOffered);
            Assert.Equal([WindowInfo.ProgramOf(source.Handle)], listed);
            Assert.True(view.UsingOriginal);
            Assert.Equal(PipStateProblem.None, view.FirstStateProblem);
            view.Close();
        });
    }

    [Fact]
    public void Nothing_is_offered_while_the_original_is_on_show()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            Pump(300);
            view.Offer(PressOffer.UseOriginal);
            Pump(300);
            Assert.Equal(PipChipState.None, view.ChipShown);
            view.Close();
        });
    }

    [Fact]
    public void A_click_on_a_view_of_a_listed_program_shows_its_original_and_sends_it_nothing()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        using var other = new TargetProcess(new Rectangle(820, 120, 300, 300), "1E1EC8", "WinScreen other target");
        IReadOnlyList<string> before = PipOriginalApps.Programs;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            try
            {
                Desktop.GainForegroundRights();
                PipOriginalApps.Programs = [WindowInfo.ProgramOf(source.Handle)];
                var windows = new WindowController([]);
                using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
                view.ShowOnTop();
                Assert.False(view.OpensOriginalOnClick); // Only a hidden original needs bringing back.
                view.SetInteractive(true);
                Assert.True(view.OpensOriginalOnClick);
                Pump(300);
                while (source.NextReport(200) is not null) { }

                var inside = new Point(view.Left + view.Width / 2, view.Top + view.Height / 2);
                Desktop.SlideMouse(inside);
                Pump(400);
                Assert.Null(view.LiveInteraction); // No visit starts: nothing is sent to this program.
                Desktop.ClickOn(inside, view.Handle);
                Assert.True(Desktop.WaitFor(() => view.UsingOriginal, 3000, Application.DoEvents), "The click didn't show the original.");
                Assert.False(windows.IsHidden(source.Handle));
                Assert.Empty(view.PressOutcomes);

                Activate(other.Handle);
                Assert.True(Desktop.WaitFor(() => windows.IsHidden(source.Handle), 3000, Application.DoEvents), "It wasn't hidden again.");
                Assert.True(Item(view, "menu.view.interactive").Checked);
                view.Close();
            }
            finally { PipOriginalApps.Programs = before; }
        });
    }
}
