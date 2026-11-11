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

    /// <summary>
    /// Settings is made again when the look changes between light and dark; changes not saved yet came along instead of
    /// being dropped with the old window, and are still waiting to be saved.
    /// </summary>
    [Fact]
    public void Changes_not_saved_yet_survive_Settings_being_made_again()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var kept = new ProgramSetup { Program = "notepad" };
            var removed = new ProgramSetup { Program = "mspaint" };
            var settings = new AppSettings { Language = "en", ShowNotifications = true, ExcludedPrograms = ["calc"], DefaultOpacityPercent = 80 };
            settings.SavedSetups.AddRange([kept, removed]);
            AppSettings? saved = null;
            IReadOnlyList<HotkeyProblem> Save(AppSettings draft) { saved = draft; return []; }

            SettingsWindow.UnsavedEdits? unsaved;
            using (var old = new SettingsWindow(settings, new StartWithWindows(), Save))
            {
                Assert.Null(old.Unsaved());
                old.SetOption(nameof(AppSettings.ShowNotifications), false);
                old.AddExcluded("notepad");
                old.RemoveSetup(removed);
                old.SetDefaultOpacity(55);
                unsaved = old.Unsaved();
                old.Close();
            }
            Assert.NotNull(unsaved);

            using var window = new SettingsWindow(settings, new StartWithWindows(), Save);
            window.TakeUnsaved(unsaved);
            Assert.True(window.HasChanges);
            Assert.True(window.SaveChanges());
            Assert.NotNull(saved);
            Assert.False(saved.ShowNotifications);
            Assert.Equal(["calc", "notepad"], saved.ExcludedPrograms);
            Assert.Equal(["notepad"], saved.SavedSetups.Select(setup => setup.Program));
            Assert.Equal(55, saved.DefaultOpacityPercent);
            window.Close();
        });
    }

    /// <summary>
    /// Every box people type in or choose from, and every button, is as tall as the others, as in Windows 11: lists that
    /// drop down were taller than the number boxes beside them, and the search boxes taller again.
    /// </summary>
    [Fact]
    public void Every_box_and_button_is_the_same_height()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var window = new SettingsWindow(new AppSettings { Language = "en", ExperimentalFeatures = true }, new StartWithWindows(), _ => []);
            window.Show();
            Application.DoEvents();
            int standard = SettingsStyle.Unit(window, BoxHeight.Standard);
            var boxes = Descendants(window).Where(control => control is ComboBox { Parent: not InputFrame } or PlainButton { AutoSize: true }
                or InputFrame && !Descendants(control).Any(inner => inner is ListBox or ListView)).ToList();
            Assert.True(boxes.Count > 40, $"Only {boxes.Count} found.");
            Assert.All(boxes, box => Assert.True(box.Height == standard, $"{box.GetType().Name} '{box.Text}' is {box.Height}, not {standard}."));
            window.Close();
        });
    }

    /// <summary>Made again while something else is being used, Settings comes back without taking the keyboard from it.</summary>
    [Fact]
    public void Settings_made_again_quietly_leaves_the_keyboard_where_it_is()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            Support.Desktop.GainForegroundRights();
            using var inUse = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(50, 50, 300, 200) };
            inUse.Show();
            Support.Desktop.Activate(inUse.Handle);
            Application.DoEvents();
            Assert.Equal(inUse.Handle, Native.User32.GetForegroundWindow());
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(), _ => []) { Quiet = true };
            window.Show();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
            Assert.True(window.Visible);
            Assert.True((Native.User32.GetWindowLongPtr(window.Handle, Native.User32.GWL_EXSTYLE) & 0x8) != 0, "Not on top."); // WS_EX_TOPMOST
            Assert.Equal(inUse.Handle, Native.User32.GetForegroundWindow());
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
