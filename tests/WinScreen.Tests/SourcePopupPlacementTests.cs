using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class SourcePopupPlacementTests
{
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
            var popups = new SourcePopups(owner.Handle) { RaisePopup = _ => { } };
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
}
