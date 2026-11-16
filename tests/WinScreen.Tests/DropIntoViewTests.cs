using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Something dragged onto a view lands in its hidden original, at the spot the pointer was on in the view: the original
/// comes up under the pointer for the drag and is hidden and parked again after. Real drags, with Windows' own drag and
/// drop, started in another process as File Explorer or a browser starts one.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class DropIntoViewTests
{
    private static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    /// <summary>Moves the real pointer in small steps, keeping this thread's windows answering the drag.</summary>
    private static void Glide(Point from, Point to, int steps = 12)
    {
        for (int i = 1; i <= steps; i++)
        {
            Desktop.MoveMouse(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Pump(40);
        }
    }

    private static string? ReportStarting(TargetProcess target, string start, int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            if (target.NextReport(50) is { } line && line.StartsWith(start, StringComparison.Ordinal)) return line;
        }
        return null;
    }

    private static Point Middle(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drag_onto_a_view_drops_into_the_original_at_the_spot_shown_and_it_hides_again(bool interactive)
    {
        using var source = new TargetProcess(new Rectangle(60, 80, 600, 400), "C81E1E", "WinScreen drop target");
        using var dragger = new TargetProcess(new Rectangle(760, 600, 300, 200), "1E64C8", "WinScreen drag source");
        source.TakeDrops();
        dragger.StartDragsWith("moved");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(760, 80);
            if (interactive) view.SetInteractive(true);
            else view.SetOriginalHidden(true);
            Pump(800);
            Assert.True(windows.IsParked(source.Handle));
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Rectangle shown = view.RectangleToScreen(view.ClientRectangle);
            Point aim = Middle(shown);
            Point expected = view.SourceSpotAt(aim);
            // A sanity check on the view's own answer: it shows all of the window, so its middle is near the middle.
            Assert.InRange(expected.X, 150, 450);
            Assert.InRange(expected.Y, 100, 300);

            Point start = Middle(Desktop.BoundsOf(dragger.Handle));
            Desktop.RequireWindowAt(start, dragger.Handle);
            Desktop.MoveMouse(start);
            Pump(100);
            Desktop.MouseButton(true);
            try
            {
                Glide(start, new Point(start.X, start.Y - 20), 4);
                // Straight to the spot, as a quick hand does: the drag comes into the view there.
                Desktop.MoveMouse(aim);
                Pump(40);
                Glide(aim, new Point(aim.X + 1, aim.Y), 2);
                Glide(new Point(aim.X + 1, aim.Y), aim, 2);
                Assert.True(Desktop.WaitFor(() => view.HandingOverDrop, 2000, () => Pump(20)), "The view didn't hand the drag over.");
                Assert.False(User32.IsWindowVisible(view.Handle), "The view still covers the original.");
                Rectangle out_ = Desktop.BoundsOf(source.Handle);
                Assert.True(out_.Contains(aim), $"The original isn't under the pointer: {out_}, pointer {aim}.");
                Assert.False(WindowInfo.IsConcealed(source.Handle), "The original is out but can't be seen.");
                Glide(aim, new Point(aim.X + 2, aim.Y + 2), 3);
                Glide(new Point(aim.X + 2, aim.Y + 2), aim, 3);
                Pump(150);
            }
            finally { Desktop.MouseButton(false); }

            // It lands on the spot the view showed under the pointer.
            string? drop = ReportStarting(source, "drop ", 3000);
            Assert.NotNull(drop);
            string[] parts = drop!.Split(' ');
            Assert.Equal("moved", parts[1]);
            var at = new Point(int.Parse(parts[2]), int.Parse(parts[3]));
            Assert.InRange(at.X, expected.X - 4, expected.X + 4);
            Assert.InRange(at.Y, expected.Y - 4, expected.Y + 4);

            Assert.True(Desktop.WaitFor(() => !view.HandingOverDrop, 3000, () => Pump(20)), "The original didn't hide again.");
            Assert.True(windows.IsHidden(source.Handle));
            Assert.True(windows.IsParked(source.Handle));
            Assert.True(WindowInfo.IsConcealed(source.Handle), "The original is back in its place but can be seen.");
            Assert.Equal(parked, Desktop.BoundsOf(source.Handle));
            Assert.True(User32.IsWindowVisible(view.Handle), "The view didn't come back.");

            // Its home is kept: Keep hidden off puts it back where it was before the view.
            if (interactive) view.SetInteractive(false);
            view.SetOriginalHidden(false);
            Assert.Equal(new Rectangle(60, 80, 600, 400), Desktop.BoundsOf(source.Handle));
        });
    }

    [Fact]
    public void A_drag_cancelled_with_Escape_over_the_original_drops_nothing_and_it_hides_again()
    {
        using var source = new TargetProcess(new Rectangle(60, 80, 600, 400), "C81E1E", "WinScreen drop target");
        using var dragger = new TargetProcess(new Rectangle(760, 600, 300, 200), "1E64C8", "WinScreen drag source");
        source.TakeDrops();
        dragger.StartDragsWith("cancelled");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows);
            view.ShowOnTop();
            view.Location = new Point(760, 80);
            view.SetOriginalHidden(true);
            Pump(800);
            Rectangle parked = Desktop.BoundsOf(source.Handle);
            Point aim = Middle(view.RectangleToScreen(view.ClientRectangle));
            Point start = Middle(Desktop.BoundsOf(dragger.Handle));

            Desktop.RequireWindowAt(start, dragger.Handle);
            Desktop.MoveMouse(start);
            Pump(100);
            Desktop.MouseButton(true);
            try
            {
                Glide(start, new Point(start.X, start.Y - 20), 4);
                Desktop.MoveMouse(aim);
                Pump(40);
                Glide(aim, new Point(aim.X + 1, aim.Y), 2);
                Assert.True(Desktop.WaitFor(() => view.HandingOverDrop, 2000, () => Pump(20)), "The view didn't hand the drag over.");
                Glide(new Point(aim.X + 1, aim.Y), aim, 2);
                Desktop.Press(Keys.Escape);
                Assert.NotNull(ReportStarting(dragger, "dragged None", 3000));
                // The button is still down: the original waits for it.
                Pump(300);
                Assert.True(view.HandingOverDrop);
            }
            finally { Desktop.MouseButton(false); }

            Assert.True(Desktop.WaitFor(() => !view.HandingOverDrop, 3000, () => Pump(20)), "The original didn't hide again.");
            Assert.Null(ReportStarting(source, "drop ", 300));
            Assert.True(WindowInfo.IsConcealed(source.Handle));
            Assert.Equal(parked, Desktop.BoundsOf(source.Handle));
            Assert.True(User32.IsWindowVisible(view.Handle));
        });
    }

    [Fact]
    public void A_drag_that_only_crosses_a_view_hides_the_original_again_and_drops_where_it_was_going()
    {
        using var source = new TargetProcess(new Rectangle(60, 80, 600, 400), "C81E1E", "WinScreen drop target");
        using var dragger = new TargetProcess(new Rectangle(760, 600, 300, 200), "1E64C8", "WinScreen drag source");
        using var other = new TargetProcess(new Rectangle(60, 600, 300, 200), "1EC864", "WinScreen other drop target");
        source.TakeDrops();
        other.TakeDrops();
        dragger.StartDragsWith("elsewhere");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            using var view = new PipWindow(source.Handle, windows);
            view.ShowOnTop();
            view.Location = new Point(760, 80);
            view.SetOriginalHidden(true);
            Pump(800);
            Point aim = Middle(view.RectangleToScreen(view.ClientRectangle));
            Point start = Middle(Desktop.BoundsOf(dragger.Handle));
            Point end = Middle(Desktop.BoundsOf(other.Handle));

            Desktop.RequireWindowAt(start, dragger.Handle);
            Desktop.MoveMouse(start);
            Pump(100);
            Desktop.MouseButton(true);
            try
            {
                Glide(start, new Point(start.X, start.Y - 20), 4);
                Glide(new Point(start.X, start.Y - 20), aim);
                Assert.True(Desktop.WaitFor(() => view.HandingOverDrop, 2000, () => Pump(20)), "The view didn't hand the drag over.");
                // On past the original to the other window, button still down.
                Glide(aim, end, 20);
                Assert.True(Desktop.WaitFor(() => !view.HandingOverDrop, 2000, () => Pump(20)),
                    "The original stayed out with the drag gone past it.");
                Glide(end, new Point(end.X + 3, end.Y + 3), 3);
                Pump(150);
            }
            finally { Desktop.MouseButton(false); }

            Assert.NotNull(ReportStarting(other, "drop elsewhere", 3000));
            Assert.Null(ReportStarting(source, "drop ", 300));
            Assert.True(WindowInfo.IsConcealed(source.Handle));
            Assert.True(windows.IsParked(source.Handle));
            Assert.True(User32.IsWindowVisible(view.Handle));
        });
    }
}
