using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class ListRowTests
{
    /// <summary>
    /// Every row of a list shows its words, not only the first. Rows are drawn off screen and Windows' text drawing did
    /// not follow the shift down to each row, so below the first the words fell outside and only the boxes showed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_row_shows_its_words(bool checkList)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var form = new Form { ClientSize = new Size(360, 260), StartPosition = FormStartPosition.Manual, Location = new Point(100, 100),
                TopMost = true, BackColor = SettingsStyle.Card };
            ListBox list = checkList ? new ThemedCheckList() : new ThemedList();
            list.Bounds = new Rectangle(10, 10, 320, 230);
            string[] names = ["Pin on top", "Hide while not in use", "Make see-through", "Follow the pointer"];
            foreach (string name in names)
                if (list is CheckedListBox ticks) ticks.Items.Add(name, true); else list.Items.Add(name);
            form.Controls.Add(list);
            form.Show();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
            int words = checkList ? SettingsStyle.Unit(list, 50) : SettingsStyle.Unit(list, 12);
            for (int index = 0; index < names.Length; index++)
            {
                Rectangle row = list.GetItemRectangle(index);
                Rectangle area = list.RectangleToScreen(Rectangle.FromLTRB(row.Left + words, row.Top, row.Right, row.Bottom));
                using var picture = new Bitmap(area.Width, area.Height);
                using (Graphics g = Graphics.FromImage(picture)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                int ink = 0;
                for (int y = 0; y < picture.Height; y++)
                    for (int x = 0; x < picture.Width; x++)
                        if (Math.Abs(picture.GetPixel(x, y).GetBrightness() - list.BackColor.GetBrightness()) > 0.2f) ink++;
                Assert.True(ink > 40, $"Row {index} ({names[index]}) showed {ink} pixels of words.");
            }
            form.Close();
        });
    }
}
