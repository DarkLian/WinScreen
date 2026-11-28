using WinScreen.Settings;
using WinScreen.Tests.Support;
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

    /// <summary>
    /// The owner opened Notifications (Off), passed the pointer over Show all and the box above the list said Show all,
    /// though nothing was chosen: while a list is open the box keeps showing the choice made before it opened.
    /// </summary>
    [Fact]
    public void An_open_list_box_keeps_showing_the_choice_while_the_pointer_passes_other_rows()
    {
        Desktop.GainForegroundRights();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var form = new Form { ClientSize = new Size(360, 260), StartPosition = FormStartPosition.Manual, Location = new Point(100, 100),
                TopMost = true, BackColor = SettingsStyle.Card };
            var box = new ThemedCombo { Bounds = new Rectangle(20, 20, 220, 32) };
            box.Items.AddRange(["Off", "Only important", "Show all"]);
            box.SelectedIndex = 0;
            form.Controls.Add(box);
            form.Show();
            form.Activate();
            Pump(200);
            Point home = Desktop.CursorPosition();
            try
            {
                Rectangle shown = box.RectangleToScreen(box.ClientRectangle);
                Desktop.MoveMouse(new Point(shown.Left + shown.Width / 2, shown.Top + shown.Height / 2));
                Pump(50);
                Desktop.MouseButton(true);
                Thread.Sleep(50);
                Desktop.MouseButton(false);
                Pump(400);
                Assert.True(box.DroppedDown, "The click didn't open the list.");
                using Bitmap before = Picture(shown);

                Rectangle list = Desktop.BoundsOf(Native.User32.FindWindow("ComboLBox", null));
                Desktop.MoveMouse(new Point(list.Left + list.Width / 2, list.Bottom - box.ItemHeight / 2 - 2));
                Pump(300);
                Assert.True(box.DroppedDown, "The list closed.");
                Assert.Equal(2, box.SelectedIndex); // What Windows reports for the row under the pointer.
                // The box painted late, as when the pointer moved on before the paint asked for on opening came.
                box.Invalidate();
                box.Update();
                Pump(100);
                using Bitmap after = Picture(shown);
                int changed = 0;
                for (int y = 0; y < before.Height; y++)
                    for (int x = 0; x < before.Width; x++)
                        if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;
                Assert.True(changed < 10, $"The box changed in {changed} pixels as the pointer passed a row.");

                Desktop.Key(Keys.Escape, true);
                Desktop.Key(Keys.Escape, false);
                Pump(200);
                Assert.False(box.DroppedDown);
                Assert.Equal(0, box.SelectedIndex);
            }
            finally
            {
                Desktop.MoveMouse(home);
                form.Close();
            }
        });
    }

    private static Bitmap Picture(Rectangle area)
    {
        var picture = new Bitmap(area.Width, area.Height);
        using (Graphics g = Graphics.FromImage(picture)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return picture;
    }

    private static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
