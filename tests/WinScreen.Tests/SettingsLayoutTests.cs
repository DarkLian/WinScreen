using WinScreen.Localization;
using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class SettingsLayoutTests
{
    /// <summary>
    /// The actions table never keeps a sideways scroll bar once its columns fit: after the window narrows, the bar it
    /// showed while the old columns were too wide went only when something else redrew the table.
    /// </summary>
    [Fact]
    public void The_actions_table_drops_its_sideways_scroll_bar_once_the_window_narrows()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(), _ => []);
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = new Rectangle(0, 0, 1100, Screen.PrimaryScreen!.WorkingArea.Height);
            window.Show();
            TabControl tabs = Descendants(window).OfType<TabControl>().Single();
            tabs.SelectedIndex = 1;
            void Pump() { for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); } }
            Pump();
            window.Bounds = new Rectangle(0, 0, 855, 670);
            Pump();
            ListView table = Descendants(window).OfType<ListView>().Single();
            int columns = table.Columns.Cast<ColumnHeader>().Sum(column => column.Width);
            Assert.True(columns <= table.ClientSize.Width, $"Columns {columns} wider than the table {table.ClientSize.Width}.");
            Assert.True((Native.User32.GetWindowLongPtr(table.Handle, Native.User32.GWL_STYLE) & 0x00100000) == 0,
                "The table kept its sideways scroll bar.");
            window.Close();
        });
    }

    [Theory]
    [InlineData("en", 100)] [InlineData("en", 150)] [InlineData("en", 200)]
    [InlineData("zh-Hans", 100)] [InlineData("zh-Hans", 150)] [InlineData("zh-Hans", 200)]
    [InlineData("zh-Hant", 100)] [InlineData("zh-Hant", 150)] [InlineData("zh-Hant", 200)]
    public void Scaled_layout_keeps_save_and_discard_inside_the_window(string language, int percent)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            try
            {
                UiText.Initialize(language);
                using var window = new SettingsWindow(new AppSettings { Language = language }, new StartWithWindows(), _ => []);
                using var scaledFont = new Font(window.Font.FontFamily, window.Font.Size * percent / 100f);
                window.Font = scaledFont;
                window.Scale(new SizeF(percent / 100f, percent / 100f));
                window.Show();
                Application.DoEvents();
                Assert.True(Screen.FromControl(window).WorkingArea.Contains(window.Bounds));
                // Save and Discard sit in a bar that is only there while something waits to be saved.
                window.SetOption(nameof(AppSettings.ShowNotifications), false);
                window.RefreshApply();
                Assert.True(window.HasChanges);
                Application.DoEvents();
                TabControl tabs = Descendants(window).OfType<TabControl>().Single();
                foreach (int page in Enumerable.Range(0, tabs.TabPages.Count))
                {
                    tabs.SelectedIndex = page;
                    Application.DoEvents();
                    foreach (Button button in Descendants(window).OfType<Button>().Where(button =>
                        button.Text == UiText.T("settings.save") || button.Text == UiText.T("settings.discard")))
                    {
                        Assert.True(button.Visible, button.Text);
                        Assert.True(window.ClientRectangle.Contains(window.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
                    }
                    if (Environment.GetEnvironmentVariable("WINSCREEN_LAYOUT_EVIDENCE") is { } folder)
                    {
                        Directory.CreateDirectory(folder);
                        using var image = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(image, new Rectangle(Point.Empty, window.Size));
                        image.Save(Path.Combine(folder, $"{language}-{percent}-page{page}.png"));
                    }
                }
                window.Close();
            }
            finally { UiText.Initialize("en"); }
        });
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
