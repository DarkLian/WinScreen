using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class ProgramPickerTests
{
    /// <summary>
    /// A search box keeps saying what to type while it has the keyboard, as Windows 11's do, and the words go as soon
    /// as something is typed. WinForms' own waiting words went as soon as the box was clicked.
    /// </summary>
    [Fact]
    public void A_search_box_says_what_to_type_until_something_is_typed()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var form = new Form { ClientSize = new Size(400, 120), StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), TopMost = true };
            var box = new HintTextBox { Hint = "Search by title or program", Width = 300, Location = new Point(20, 40), BorderStyle = BorderStyle.None };
            form.Controls.Add(box);
            form.Show();
            box.Focus();
            void Pump() { for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); } }
            int Ink()
            {
                Rectangle area = box.RectangleToScreen(box.ClientRectangle);
                using var picture = new Bitmap(area.Width, area.Height);
                using (Graphics g = Graphics.FromImage(picture)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                int count = 0;
                for (int y = 0; y < picture.Height; y++)
                    for (int x = 0; x < picture.Width; x++)
                        if (Math.Abs(picture.GetPixel(x, y).GetBrightness() - box.BackColor.GetBrightness()) > 0.2f) count++;
                return count;
            }
            Pump();
            int hint = Ink();
            Native.User32.SendMessage(box.Handle, 0x0102 /* WM_CHAR */, 'x', 0);
            Pump();
            int typed = Ink();
            Native.User32.SendMessage(box.Handle, 0x0102, '\b', 0);
            Pump();
            int again = Ink();
            Assert.True(hint > 100, $"The hint drew {hint} pixels.");
            Assert.True(typed < hint / 4, $"With 'x' typed {typed} pixels showed, the hint {hint}.");
            Assert.True(again > hint * 3 / 4, $"Emptied again, {again} pixels showed, the hint {hint}.");
            form.Close();
        });
    }

    /// <summary>
    /// Typing a name offers matching programs under the box and takes the list away when nothing matches. Emptying the
    /// list as it closed left the box pointing at a row no longer there, and reading what was typed then stopped the app.
    /// </summary>
    [Theory]
    [InlineData("nox")]
    [InlineData("no\bx")]
    [InlineData("n\b")]
    [InlineData("chr\b\b\bz")]
    [InlineData("notep\b\b\b\b\bq")]
    public void Typing_past_every_suggestion_keeps_the_box_working(string keys)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Exception? thrown = null;
            Application.ThreadException += (_, e) => thrown ??= e.Exception;
            using var form = new Form { ClientSize = new Size(400, 200), StartPosition = FormStartPosition.Manual, Location = new Point(100, 100) };
            var picker = new ThemedPicker { Width = 300, Location = new Point(10, 10), Choices = () => ["notepad", "notepad++", "chrome", "code"] };
            form.Controls.Add(picker);
            form.Show();
            picker.Focus();
            nint typing = Native.User32.FindWindowEx(picker.Handle, 0, "Edit", null);
            string typed = "";
            foreach (char key in keys)
            {
                Native.User32.SendMessage(typing, 0x0102 /* WM_CHAR */, key, 0);
                typed = key == '\b' ? typed[..^1] : typed + key;
                for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
                if (thrown is not null) break;
            }
            Assert.True(thrown is null, thrown?.ToString());
            Assert.Equal(typed, picker.Text);
            form.Close();
        });
    }
}
