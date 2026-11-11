using WinScreen.Localization;
using WinScreen.Native;
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
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
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
            using (var old = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), Save))
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

            using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), Save);
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
            using var window = new SettingsWindow(new AppSettings { Language = "en", ExperimentalFeatures = true }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
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
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []) { Quiet = true };
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
                using var window = new SettingsWindow(new AppSettings { Language = language }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
                using var scaledFont = new Font(window.Font.FontFamily, window.Font.Size * percent / 100f);
                window.Font = scaledFont;
                window.Scale(new SizeF(percent / 100f, percent / 100f));
                window.Show();
                Application.DoEvents();
                // Too big for the screen, Windows shows it maximized, which always fits.
                Assert.True(window.WindowState == FormWindowState.Maximized || Screen.FromControl(window).WorkingArea.Contains(window.Bounds));
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

    /// <summary>
    /// A wider window gives every row the same extra room, up to a width still easy to read; maximized, Settings
    /// stops staying above other windows and views, and stays above them again when made smaller.
    /// </summary>
    [Fact]
    public void A_wider_window_widens_every_row_and_maximized_it_stops_staying_on_top()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []) { TopMost = true };
            window.Show();
            Application.DoEvents();
            int[] Widths() => [.. Descendants(window).OfType<SwitchBox>().Where(option => !option.Compact && option.Visible)
                .Select(option => option.Width).Distinct()];
            int usual = Assert.Single(Widths());
            Assert.True(window.MinimumSize.Width >= window.Width);

            window.Width += 300;
            Application.DoEvents();
            int wider = Assert.Single(Widths());
            Assert.Equal(usual + 300, wider);

            window.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            Assert.True(window.FillsScreen);
            int widest = Assert.Single(Widths());
            Assert.True(widest <= SettingsStyle.Unit(window, ScrollPage.WidestCard), $"{widest}");
            Assert.Equal(0, User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST);

            window.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Assert.NotEqual(0, User32.GetWindowLongPtr(window.Handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST);
            window.Close();
        });
    }

    /// <summary>
    /// As in Windows 11's Settings, every page's title and cards share one column: the same left and right edges on
    /// every page, filling the room at the usual size, and at most a readable width, centred, in a maximized window.
    /// </summary>
    [Fact]
    public void Every_page_lines_up_in_one_column_centred_when_maximized()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var settings = new AppSettings { Language = "en", ExperimentalFeatures = true, ExcludedPrograms = ["notepad"] };
            settings.SavedSetups.Add(new ProgramSetup { Program = "notepad", Pinned = true });
            foreach (bool maximized in new[] { false, true })
            {
                using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
                window.Show();
                if (maximized) window.WindowState = FormWindowState.Maximized;
                void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); } }
                Pump();
                TabControl tabs = Descendants(window).OfType<TabControl>().Single();
                Control title = Descendants(window).OfType<Label>().Single(label => label.Font == SettingsStyle.PageTitleFont);
                Control side = Descendants(window).OfType<NavList>().Single().Parent!;
                int Unit(int value) => SettingsStyle.Unit(window, value);
                var edges = new List<(int Left, int Right, string What)>();
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    Pump();
                    // The cards on a page that scrolls; the search box and the table on the page of actions.
                    Control[] column = Descendants(page).OfType<SettingsCard>().Where(card => card.Visible).Cast<Control>().ToArray();
                    if (column.Length == 0)
                        column = Descendants(page).OfType<InputFrame>().Where(frame => frame.Dock is DockStyle.Top or DockStyle.Fill).ToArray();
                    Assert.NotEmpty(column);
                    Rectangle titleOnScreen = title.RectangleToScreen(title.ClientRectangle);
                    foreach (Control part in column)
                    {
                        Rectangle r = part.RectangleToScreen(part.ClientRectangle);
                        edges.Add((r.Left, r.Right, $"{(maximized ? "maximized" : "usual")} {page.Text}: {part.Text}"));
                        Assert.InRange(r.Left - titleOnScreen.Left, -Unit(4), Unit(4)); // The title's big letters stand a little in.
                    }
                }
                int left = edges[0].Left, right = edges[0].Right;
                foreach ((int l, int r, string what) in edges)
                {
                    Assert.True(Math.Abs(l - left) <= Unit(2), $"{what} starts at {l}, not {left}.");
                    Assert.True(Math.Abs(r - right) <= Unit(2), $"{what} ends at {r}, not {right}.");
                }
                if (!maximized) continue;
                Assert.True(right - left <= Unit(ScrollPage.WidestCard) + Unit(2), $"{right - left} wide.");
                Rectangle room = window.RectangleToScreen(window.ClientRectangle);
                int roomLeft = side.RectangleToScreen(side.ClientRectangle).Right;
                int before = left - roomLeft, after = room.Right - right;
                Assert.True(Math.Abs(before - after) <= Unit(24), $"Not centred: {before} before, {after} after.");
                window.Close();
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}

/// <summary>Where Settings may open again: anywhere its title bar is on a screen.</summary>
public sealed class SettingsPlacementTests
{
    private static readonly Rectangle[] Screens = [new(0, 0, 1920, 1040), new(1920, 0, 1280, 984)];

    [Theory]
    [InlineData(100, 100, 900, 650, true)]
    [InlineData(2000, 50, 900, 650, true)] // On the second screen.
    [InlineData(-2000, 100, 900, 650, false)] // A screen no longer there.
    [InlineData(100, -400, 900, 650, false)] // Title bar above every screen.
    [InlineData(100, 100, 50, 40, false)] // Too small to be a real size.
    public void Settings_opens_where_it_was_only_while_that_is_on_a_screen(int x, int y, int width, int height, bool kept) =>
        Assert.Equal(kept, new SettingsPlacement(x, y, width, height, false).OnScreen(Screens) is not null);
}
