using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class ProgramPickerTests
{
    /// <summary>
    /// Typing a name offers matching programs under the box and takes the list away when nothing matches. Emptying the
    /// list as it closed left the box pointing at a row no longer there, and reading what was typed then stopped the app.
    /// </summary>
    [Theory]
    [InlineData("nox")]
    [InlineData("nox")]
    [InlineData("n")]
    [InlineData("chrz")]
    [InlineData("notepq")]
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
                typed = key == '' ? typed[..^1] : typed + key;
                for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
                if (thrown is not null) break;
            }
            Assert.True(thrown is null, thrown?.ToString());
            Assert.Equal(typed, picker.Text);
            form.Close();
        });
    }
}
