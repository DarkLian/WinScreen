using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class SourcePopupPlacementTests
{
    private static Form Popup(Form owner, Rectangle bounds, Color color)
    {
        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Bounds = bounds, BackColor = color,
        };
        popup.Show(owner);
        return popup;
    }

    private static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static Point Middle(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    [Fact]
    public void Relayout_keeps_a_popups_anchor_and_first_restoration_position()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var owner = new Form { Location = new(100, 100), StartPosition = FormStartPosition.Manual };
            using var popup = new Form { FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false, Location = new(150, 150), StartPosition = FormStartPosition.Manual, Size = new(180, 100) };
            using var unrelated = new Form { Location = new(500, 100), StartPosition = FormStartPosition.Manual };
            owner.Show(); popup.Show(owner); unrelated.Show();
            Point original = popup.Location, stranger = unrelated.Location;
            var popups = new SourcePopups(owner.Handle) { RaisePopups = _ => { } };
            Rectangle view = new(800, 200, 480, 300);
            popups.SweepMapped(view, new(900, 250), original, 0);
            Point placed = popup.Location;
            Assert.NotEqual(original, placed);
            for (int i = 0; i < 6; i++)
            {
                // The app lays its menu out again; input is now over a different point in the menu.
                popup.Location = original + new Size(i, i);
                popups.SweepMapped(view, new(950 + i * 20, 300 + i * 10), original, 0);
                Assert.Equal(placed, popup.Location);
            }
            popups.ShowAll();
            Assert.Equal(original, popup.Location);
            Assert.Equal(stranger, unrelated.Location);
        });
    }

    /// <summary>
    /// Windows lays a submenu out beside where its parent menu really is, which is next to the view once the parent
    /// was moved. The pointer is over the menu, not the view, so the last pointer seen in the view is where the
    /// parent went: moving the submenu there would put it right on top of its parent.
    /// </summary>
    [Fact]
    public void A_submenu_beside_its_moved_parent_stays_there_and_ends_above_it_over_the_view()
    {
        Desktop.GainForegroundRights(); // Before any window: on a thread with windows it would wait on their painting.
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new(screen.Left + 40, screen.Top + 40) };
            owner.Show();
            // The test's own window is the active one, so nothing the person has open is over the menus.
            Desktop.Activate(owner.Handle);
            // A topmost window standing in for the view, under where the menus go.
            Rectangle viewArea = new(screen.Left + 300, screen.Top + 200, 700, 500);
            using var view = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, Bounds = viewArea, BackColor = Color.Magenta, TopMost = true };
            view.Show();
            using var parent = Popup(owner, new(screen.Left + 60, screen.Top + 60, 220, 300), Color.Blue);
            Point pointer = new(viewArea.Left + 40, viewArea.Top + 40);
            var raised = new List<nint[]>();
            var popups = new SourcePopups(owner.Handle) { RaisePopups = group => { raised.Add(group.ToArray()); WinScreen.Windows.TopLayer.RaiseGroup(group); } };
            Point mapped = new(parent.Left - 5, parent.Top - 5);
            popups.SweepMapped(viewArea, pointer, mapped, view.Handle);
            Pump(50);
            Rectangle placedParent = parent.Bounds;
            Assert.True(viewArea.IntersectsWith(placedParent), $"The parent menu went to {placedParent}, not by the view {viewArea}.");
            Assert.Equal([parent.Handle], raised.Last());

            // The submenu opens where Windows puts it: beside the parent, overlapping it a little.
            using var child = Popup(owner, new(placedParent.Right - 6, placedParent.Top + 40, 200, 220), Color.Lime);
            Rectangle native = child.Bounds;
            popups.SweepMapped(viewArea, pointer, mapped, view.Handle); // The same stale pointer.
            Pump(50);
            Assert.Equal(native, child.Bounds);
            Assert.Equal(placedParent, parent.Bounds);
            Assert.Equal([parent.Handle, child.Handle], raised.Last());
            Rectangle overlap = Rectangle.Intersect(parent.Bounds, child.Bounds);
            Assert.False(overlap.IsEmpty);
            Assert.Equal(child.Handle, Desktop.TopWindowAt(Middle(overlap)));
            Assert.Equal(child.Handle, Desktop.TopWindowAt(Middle(child.Bounds)));
            Assert.Equal(parent.Handle, Desktop.TopWindowAt(new Point(parent.Left + 20, parent.Bottom - 20)));

            // Something lifts the view over both, as activating it does: the next sweep lifts the chain back in
            // order, the submenu still over its parent.
            const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
            User32.SetWindowPos(view.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
            Pump(50);
            Assert.Equal(view.Handle, Desktop.TopWindowAt(Middle(overlap)));
            popups.SweepMapped(viewArea, pointer, mapped, view.Handle);
            Pump(50);
            Assert.Equal([parent.Handle, child.Handle], raised.Last());
            Assert.Equal(child.Handle, Desktop.TopWindowAt(Middle(overlap)));
            Assert.Equal(parent.Handle, Desktop.TopWindowAt(new Point(parent.Left + 20, parent.Bottom - 20)));

            // A correct order is left alone.
            int before = raised.Count;
            for (int i = 0; i < 5; i++) popups.SweepMapped(viewArea, pointer, mapped, view.Handle);
            Assert.Equal(before, raised.Count);

            // The submenu closes: the parent keeps its place and the input.
            child.Hide();
            popups.SweepMapped(viewArea, pointer, mapped, view.Handle);
            Pump(50);
            Assert.Equal(placedParent, parent.Bounds);
            Assert.Equal(parent.Handle, Desktop.TopWindowAt(Middle(overlap)));
            popups.ShowAll();
            view.Close();
        });
    }

    /// <summary>A submenu its app lays out from the parent's first place moves with the parent, keeping its side.</summary>
    [Fact]
    public void A_submenu_laid_out_from_its_parents_first_place_moves_with_the_parent()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new(screen.Left + 40, screen.Top + 40) };
            owner.Show();
            using var parent = Popup(owner, new(screen.Left + 60, screen.Top + 60, 220, 300), Color.Blue);
            Rectangle original = parent.Bounds;
            Rectangle viewArea = new(screen.Left + 500, screen.Top + 200, 700, 500);
            var popups = new SourcePopups(owner.Handle) { RaisePopups = _ => { } };
            Point pointer = new(viewArea.Left + 40, viewArea.Top + 40), mapped = new(parent.Left - 5, parent.Top - 5);
            popups.SweepMapped(viewArea, pointer, mapped, 0);
            Size moved = (Size)parent.Location - (Size)original.Location;
            Assert.NotEqual(Size.Empty, moved);

            using var child = Popup(owner, new(original.Right - 4, original.Top + 30, 200, 220), Color.Lime);
            Rectangle native = child.Bounds;
            popups.SweepMapped(viewArea, pointer, mapped, 0);
            Assert.Equal(native.Location + moved, child.Location);
            Assert.Equal(parent.Right - 4, child.Left);

            // Everything goes back where its app put it.
            popups.ShowAll();
            Assert.Equal(original.Location, parent.Location);
            Assert.Equal(native.Location, child.Location);
        });
    }
}
