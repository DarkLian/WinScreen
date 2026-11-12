using System.Reflection;
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

            window.Width += 200; // Still short of the widest a row gets.
            Application.DoEvents();
            int wider = Assert.Single(Widths());
            Assert.Equal(usual + 200, wider);

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
    /// As in Windows 11's Settings, every page's title and cards, and the bar for saving, share one column: the same
    /// left and right edges on every page, filling the room at the usual size, and at most a readable width, centred, in
    /// a maximized window, whether it was maximized before or after it showed.
    /// </summary>
    [Fact]
    public void Every_page_lines_up_in_one_column_centred_when_maximized()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var settings = new AppSettings { Language = "en", ExperimentalFeatures = true, ExcludedPrograms = ["notepad"] };
            settings.SavedSetups.Add(new ProgramSetup { Program = "notepad", Pinned = true });
            foreach ((string how, bool maximized, bool first) in new[] { ("usual", false, false), ("maximized", true, false),
                         ("maximized before showing", true, true) })
            {
                using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
                window.StartPosition = FormStartPosition.Manual; // As when it opens where it was last left.
                window.Bounds = new Rectangle(new Point(20, 20), window.Size);
                window.TopMost = true;
                if (first) window.WindowState = FormWindowState.Maximized;
                window.Show();
                // Windows may show the first window of a process maximized, as the program that started it asked.
                window.WindowState = maximized ? FormWindowState.Maximized : FormWindowState.Normal;
                void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); } }
                Pump();
                // A change not saved yet shows the bar for saving under every page.
                SwitchBox option = Descendants(window).OfType<SwitchBox>().First(box => !box.Compact && box.Enabled);
                option.Checked = !option.Checked;
                Pump();
                Control saveBar = Descendants(window).OfType<PlainButton>().Single(button => button.Text == UiText.T("settings.save"))
                    .Parent!.Parent!;
                Assert.True(saveBar.Visible);
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
                    foreach (Control part in column.Append(saveBar))
                    {
                        Rectangle r = part.RectangleToScreen(part.ClientRectangle);
                        edges.Add((r.Left, r.Right, $"{how} {page.Text}: {(part == saveBar ? "the bar for saving" : part.Text)}"));
                        Assert.InRange(r.Left - titleOnScreen.Left, -Unit(4), Unit(4)); // The title's big letters stand a little in.
                    }
                }
                int left = edges[0].Left, right = edges[0].Right;
                foreach ((int l, int r, string what) in edges)
                {
                    Assert.True(Math.Abs(l - left) <= Unit(2), $"{what} starts at {l}, not {left}.");
                    Assert.True(Math.Abs(r - right) <= Unit(2), $"{what} ends at {r}, not {right}.");
                }
                if (!maximized) { window.Close(); continue; }
                Assert.True(right - left <= Unit(ScrollPage.WidestCard) + Unit(2), $"{right - left} wide.");
                Rectangle room = window.RectangleToScreen(window.ClientRectangle);
                int roomLeft = side.RectangleToScreen(side.ClientRectangle).Right;
                int before = left - roomLeft, after = room.Right - right;
                Assert.True(Math.Abs(before - after) <= Unit(24), $"Not centred: {before} before, {after} after.");
                window.Close();
            }
        });
    }

    /// <summary>
    /// At the usual size every action in the table shows whole, in each language, with what it works on, its
    /// shortcuts and its state: nothing is cut short with an ellipsis. Options listed with them may be cut short.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Every_entry_in_the_table_of_actions_shows_whole(string language)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize(language);
            try
            {
                using var window = new SettingsWindow(new AppSettings { Language = language, ExperimentalFeatures = true },
                    new StartWithWindows(tasks: NoTasks.Instance), _ => []);
                window.Show();
                window.WindowState = FormWindowState.Normal; // Windows may show a process's first window maximized.
                TabControl tabs = Descendants(window).OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); }
                ListView table = Descendants(window).OfType<ListView>().Single();
                const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                foreach (ListViewItem row in table.Items)
                    for (int column = row.Tag is AppCommand ? 0 : 1; column < table.Columns.Count; column++)
                    {
                        int inset = SettingsStyle.Unit(table, (column == 0 ? 18 : 12) + 4);
                        if (column == 0 && CommandCatalog.IsExperimental((AppCommand)row.Tag!))
                            inset += SettingsStyle.Unit(table, 8) + MenuParts.TagSize(value => SettingsStyle.Unit(table, value)).Width;
                        int needs = Native.DirectText.MeasureText(row.SubItems[column].Text, table.Font, Size.Empty, measure).Width + inset; // As it is drawn.
                        Assert.True(needs <= table.Columns[column].Width,
                            $"{language}: \"{row.SubItems[column].Text}\" needs {needs}, has {table.Columns[column].Width}; columns " +
                            string.Join(", ", table.Columns.Cast<ColumnHeader>().Select(header => header.Width)) + $" in {table.ClientSize.Width}.");
                    }
                window.Close();
            }
            finally { UiText.Initialize("en"); }
        });
    }

    /// <summary>
    /// Every setting's icon, and the first letter of its name, sit at exactly the same place from the left on every
    /// page: at the usual size, wider, maximized, after the experimental settings are turned off and while a search
    /// filters the pages. And every setting in a card has an icon, so no name starts further left than the others.
    /// </summary>
    [Fact]
    public void Every_setting_lines_up_its_icon_and_name_to_the_pixel()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var settings = new AppSettings { Language = "en", ExperimentalFeatures = true };
            using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(20, 20);
            window.Show();
            window.WindowState = FormWindowState.Normal;
            void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); } }
            Pump();
            TabControl tabs = Descendants(window).OfType<TabControl>().Single();
            var places = new Dictionary<(int Icon, int Words), List<string>>();
            void Measure(string when)
            {
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    Pump();
                    foreach (Control row in Descendants(page).Where(row => row.Visible && row.Parent is { Visible: true } &&
                                 row is SwitchBox { Compact: false } or SettingRowPanel && Descendants(page).OfType<SettingsCard>().Any(card => card.Contains(row))))
                    {
                        char glyph = row is SwitchBox option ? option.Glyph : ((SettingRowPanel)row).Glyph;
                        Assert.True(glyph != '\0', $"{when}, {page.Text}: \"{row.Text}\" has no icon.");
                        int left = row.RectangleToScreen(row.ClientRectangle).Left - window.RectangleToScreen(window.ClientRectangle).Left
                            - (ScrollShift(row));
                        var place = (left + SettingsStyle.Unit(row, SettingText.IconLeft), left + SettingText.Left(row, glyph));
                        if (!places.TryGetValue(place, out List<string>? rows)) places[place] = rows = [];
                        rows.Add($"{when}, {page.Text}: {row.Text}");
                    }
                }
            }
            // The column moves in a wider window; what is measured is the place within the column.
            int ScrollShift(Control row)
            {
                for (Control? up = row; up is not null; up = up.Parent)
                    if (up is ScrollPage page) return page.Shift;
                return 0;
            }
            Measure("usual");
            window.Width += 260;
            Pump();
            Measure("wider");
            window.WindowState = FormWindowState.Maximized;
            Pump();
            Measure("maximized");
            window.WindowState = FormWindowState.Normal;
            window.SetOption(nameof(AppSettings.ExperimentalFeatures), false);
            Pump();
            Measure("experimental off");
            Descendants(window).OfType<TextBox>().First(box => box.AccessibleName == UiText.T("settings.search")).Text = "anchor";
            Pump();
            Measure("searching");
            Assert.True(places.Count == 1, "Icons and names start at more than one place: " +
                string.Join("; ", places.Select(pair => $"{pair.Key}: {pair.Value.Count} rows, such as {pair.Value[0]}")));
            window.Close();
        });
    }

    /// <summary>
    /// After the window is maximized and put back, no page keeps a dark sideways scroll bar for the width it had, and
    /// a page that fits shows no scroll bar at all.
    /// </summary>
    [Fact]
    public void Pages_never_scroll_sideways_and_show_a_scroll_bar_only_when_they_overflow()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = new Rectangle(0, 0, 1100, 800);
            window.Show();
            void Pump() { for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); } }
            TabControl tabs = Descendants(window).OfType<TabControl>().Single();
            var problems = new List<string>();
            for (int page = 0; page < tabs.TabCount; page++)
            {
                tabs.SelectedIndex = page;
                foreach (FormWindowState state in new[] { FormWindowState.Normal, FormWindowState.Maximized, FormWindowState.Normal })
                {
                    window.WindowState = state;
                    Pump();
                    foreach (ScrollHost host in Descendants(tabs.SelectedTab!).OfType<ScrollHost>())
                    {
                        ScrollPage scrolling = host.Page;
                        if ((User32.GetWindowLongPtr(scrolling.Handle, User32.GWL_STYLE) & 0x00100000) != 0)
                            problems.Add($"page {page} {state}: scrolls sideways");
                        int bottom = scrolling.Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max();
                        bool fits = bottom - scrolling.AutoScrollPosition.Y <= host.ClientSize.Height;
                        bool bar = host.Controls.OfType<ThinScrollBar>().Any(b => b.Visible);
                        if (fits && bar) problems.Add($"page {page} {state}: shows a scroll bar though it fits");
                    }
                }
            }
            Assert.True(problems.Count == 0, string.Join("; ", problems));
            window.Close();
        });
    }

    /// <summary>Numbers are set with a slider: the wheel and the arrow keys move it a step, never past its ends.</summary>
    [Fact]
    public void Number_sliders_move_by_steps_within_their_range()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.Show();
            Application.DoEvents();
            NumberSlider[] sliders = Descendants(window).OfType<NumberSlider>().ToArray();
            Assert.NotEmpty(sliders);
            Assert.DoesNotContain(Descendants(window).OfType<NumericUpDown>(), number => number.Visible);
            foreach (NumberSlider slider in sliders)
            {
                NumericUpDown number = slider.Number;
                decimal step = Math.Max(1, number.Increment);
                number.Value = number.Minimum;
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slider, [new KeyEventArgs(Keys.Right)]);
                Assert.Equal(number.Minimum + step, number.Value);
                typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slider,
                    [new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120)]);
                Assert.Equal(number.Minimum, number.Value);
                typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slider,
                    [new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120)]);
                Assert.Equal(number.Minimum, number.Value);
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slider, [new KeyEventArgs(Keys.End)]);
                Assert.Equal(number.Maximum, number.Value);
            }
            window.Close();
        });
    }

    /// <summary>A search that moves Settings to another page leaves the keyboard in the search box.</summary>
    [Fact]
    public void Searching_keeps_typing_in_the_search_box_when_the_page_changes()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.Show();
            window.PageIndex = 6;
            for (int i = 0; i < 5; i++) Application.DoEvents();
            TextBox search = Descendants(window).OfType<TextBox>().First(box => box.AccessibleName == UiText.T("settings.search"));
            search.Focus();
            Application.DoEvents();
            Assert.True(search.Focused);
            search.AppendText("transp"); // Only on General.
            for (int i = 0; i < 5; i++) Application.DoEvents();
            Assert.Equal(0, window.PageIndex);
            Assert.True(search.Focused, "The keyboard left the search box when the page changed.");
            Assert.Equal(search.TextLength, search.SelectionStart);
            window.Close();
        });
    }

    /// <summary>Menus can be kept plain even while Windows' Transparency effects is on, and the choice is saved.</summary>
    [Fact]
    public void Transparency_effects_in_menus_can_be_turned_off()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            AppSettings? saved = null;
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), draft => { saved = draft; return []; });
            window.Show();
            Application.DoEvents();
            CheckBox box = Descendants(window).OfType<CheckBox>().Single(b => b.Text == UiText.T("settings.menus.transparency"));
            Assert.True(box.Checked); // On by default: menus follow Windows.
            Assert.Equal(Environment.OSVersion.Version.Build >= 22000, box.Enabled);
            window.SetOption(nameof(AppSettings.MenuTransparency), false);
            Assert.True(window.ApplyChanges());
            Assert.False(saved!.MenuTransparency);
            window.Close();
        });
        try
        {
            Translucency.Allowed = false;
            Assert.False(MenuStyle.Translucent);
            Assert.Equal(MenuStyle.Back, MenuStyle.Ground);
        }
        finally { Translucency.Allowed = true; }
    }

    /// <summary>A value chosen on a slider is what gets saved.</summary>
    [Fact]
    public void A_value_chosen_on_a_slider_is_saved()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            var settings = new AppSettings { Language = "en" };
            int before = settings.Anchors.Size;
            AppSettings? saved = null;
            using var window = new SettingsWindow(settings, new StartWithWindows(tasks: NoTasks.Instance), draft => { saved = draft; return []; });
            window.Show();
            for (int i = 0; i < 5; i++) Application.DoEvents();
            NumberSlider slider = Descendants(window).OfType<NumberSlider>().Single(s => s.Number.Minimum == 32 && s.Number.Maximum == 72);
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slider,
                [new KeyEventArgs(before >= 72 ? Keys.Left : Keys.Right)]);
            for (int i = 0; i < 5; i++) Application.DoEvents();
            Assert.True(window.ApplyChanges());
            Assert.Equal(before >= 72 ? before - 4 : before + 4, saved!.Anchors.Size);
            window.Close();
        });
    }

    /// <summary>Each list that drops down is wide enough for its longest choice, in every language.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Every_list_is_wide_enough_for_its_longest_choice(string language)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize(language);
            using var window = new SettingsWindow(new AppSettings { Language = language, ExperimentalFeatures = true }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.Show();
            TabControl tabs = Descendants(window).OfType<TabControl>().Single();
            var short_ = new List<string>();
            for (int page = 0; page < tabs.TabCount; page++)
            {
                tabs.SelectedIndex = page;
                for (int i = 0; i < 5; i++) Application.DoEvents();
                foreach (SettingRowPanel row in Descendants(tabs.SelectedTab!).OfType<SettingRowPanel>())
                    if (row.Setting is ComboBox box)
                    {
                        int widest = box.Items.Cast<object>().Select(item => DirectText.MeasureText(box.GetItemText(item), box.Font, Size.Empty,
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width).DefaultIfEmpty(0).Max();
                        int room = box.Width - SettingsStyle.Unit(box, 11 + 44);
                        if (widest > room) short_.Add($"{row.Text}: {widest} > {room}");
                        if (box.Right > row.ClientSize.Width) short_.Add($"{row.Text}: past the row's edge");
                    }
            }
            Assert.True(short_.Count == 0, string.Join("; ", short_));
            window.Close();
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
