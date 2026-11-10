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
            Assert.False(Item(view, "pip.interactive").Checked);
            Activate(source.Handle);

            // It stays for as long as it is the window in use.
            Pump(1200);
            Assert.True(view.UsingOriginal);
            Assert.False(windows.IsHidden(source.Handle));

            Activate(other.Handle);
            Assert.True(Desktop.WaitFor(() => windows.IsHidden(source.Handle), 3000, Application.DoEvents), "It wasn't hidden again.");
            Assert.False(view.UsingOriginal);
            Assert.True(windows.IsParked(source.Handle));
            Assert.Equal(interactive, Item(view, "pip.interactive").Checked);
            Assert.True(Item(view, "pip.keepUpdating").Checked);

            // Going to the original afterwards is a plain choice again: it stays shown.
            view.UseOriginal();
            Activate(source.Handle);
            view.GoToSource();
            Assert.False(view.UsingOriginal);
            Activate(other.Handle);
            Pump(1200);
            Assert.False(windows.IsHidden(source.Handle));
            Assert.Equal(home, Desktop.BoundsOf(source.Handle));
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
                Assert.True(Item(view, "pip.interactive").Checked);
                view.Close();
            }
            finally { PipOriginalApps.Programs = before; }
        });
    }
}
