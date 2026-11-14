using System.Reflection;
using System.Runtime.InteropServices;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// WinScreen's own windows used with the keyboard alone (roadmap P2-4, step 7.3): every Settings page is reached from
/// its list of pages, every control on it by Tab, and every item of the window menu and its submenus by the arrow
/// keys, with real key presses.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class KeyboardWalkTests
{
    [DllImport("user32.dll")] private static extern nint GetFocus();

    private static void Pump(int milliseconds = 40)
    {
        for (int waited = 0; waited < milliseconds; waited += 10) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Press(Keys key, params Keys[] modifiers)
    {
        Desktop.Press(key, modifiers);
        Pump();
    }

    private static IEnumerable<Control> All(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));

    private static bool TakesTheKeyboard(Control control) =>
        control.CanSelect && control.TabStop && control.Visible && control.Enabled;

    [Fact]
    public void Every_settings_page_and_every_control_on_it_is_reached_with_the_keyboard()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            // Before any window of this thread: it empties the thread's messages, and a window waiting to paint keeps
            // one there for ever.
            Desktop.GainForegroundRights();
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.Show();
            Desktop.Activate(window.Handle);
            Pump(300);
            var nav = (Control)typeof(SettingsWindow).GetField("_nav", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            TabControl tabs = All(window).OfType<TabControl>().First();
            int pages = tabs.TabPages.Count;
            var problems = new List<string>();
            var seenPages = new HashSet<TabPage>();
            nav.Focus();
            Pump();
            for (int page = 0; page < pages; page++)
            {
                if (page > 0)
                {
                    // Back to the list of pages with Shift+Tab, as far as it takes, then the next page.
                    for (int i = 0; i < 400 && Control.FromChildHandle(GetFocus()) != nav; i++) Press(Keys.Tab, Keys.ShiftKey);
                    if (Control.FromChildHandle(GetFocus()) != nav) { problems.Add($"The list of pages can't be reached from {tabs.SelectedTab?.Text}."); break; }
                    Press(Keys.Down);
                }
                TabPage shown = tabs.SelectedTab!;
                if (!seenPages.Add(shown)) { problems.Add($"Down in the list of pages stayed on {shown.Text}."); break; }
                Pump(200);
                HashSet<Control> wanted = All(shown).Where(TakesTheKeyboard).ToHashSet();
                var reached = new HashSet<Control>();
                for (int i = 0; i < 400 && !wanted.IsSubsetOf(reached); i++)
                {
                    Press(Keys.Tab);
                    for (Control? focused = Control.FromChildHandle(GetFocus()); focused is not null; focused = focused.Parent)
                        reached.Add(focused);
                }
                problems.AddRange(wanted.Except(reached).Select(control =>
                    $"{shown.Text}: {control.GetType().Name} '{control.AccessibilityObject.Name ?? control.Name}' isn't reached by Tab."));
            }
            if (seenPages.Count < pages) problems.Add($"Only {seenPages.Count} of {pages} pages were reached.");
            window.Close();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void Every_item_of_the_window_menu_is_reached_with_the_arrow_keys()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            // A handler that throws would show Windows Forms' own error box and hold the test: say it instead.
            var thrown = new List<string>();
            ThreadExceptionEventHandler said = (_, e) => thrown.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
            Application.ThreadException += said; // With a handler, Windows Forms shows no error box of its own.
            Desktop.GainForegroundRights(); // Before any window of this thread, as above.
            using var target = new Form { Text = "Keyboard walk target", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 200, 400, 300) };
            target.Show();
            using var menu = new ContextMenuStrip();
            int opacity = 100;
            var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
                { OpacityPercent = _ => opacity, SetOpacityPercent = (_, value) => opacity = value };
            builder.Populate(menu.Items, false);
            MenuRenderer.Apply(menu);
            Desktop.Activate(target.Handle);
            Pump(200);
            menu.Show(new Point(260, 260));
            Pump(300);

            // The keys are pressed from another thread while this one only handles messages, and the menu is read
            // through this thread with a time limit: a menu that stops answering fails the test and says where.
            var problems = new List<string>();
            string last = "start";
            bool sliderMoved = false, stalled = false;
            T Ask<T>(Func<T> question)
            {
                if (stalled) throw new TimeoutException();
                IAsyncResult asked = target.BeginInvoke(question);
                if (!asked.AsyncWaitHandle.WaitOne(5000)) { stalled = true; throw new TimeoutException(); }
                return (T)target.EndInvoke(asked)!;
            }
            void Key(Keys key, string what)
            {
                last = what;
                Desktop.Press(key);
                Thread.Sleep(60);
                Ask(() => true); // Every message so far handled.
            }

            void Walk(ToolStripDropDown dropDown, string path)
            {
                ToolStripItem[] wanted = Ask(() => dropDown.Items.Cast<ToolStripItem>()
                    .Where(item => item.Available && item.Enabled && item is not ToolStripSeparator && item is not ToolStripLabel &&
                                   !Equals(item.Tag, MenuParts.HeaderTag))
                    .ToArray());
                string Name(ToolStripItem item) => Ask(() => item.Text is { Length: > 0 } text ? text : item.GetType().Name);
                ToolStripItem? Selected() => Ask(() => dropDown.Items.Cast<ToolStripItem>().FirstOrDefault(item => item.Selected));
                var reached = new List<ToolStripItem>();
                for (int i = 0; i < wanted.Length * 2 + 4 && reached.Count < wanted.Length; i++)
                {
                    Key(Keys.Down, $"Down in {path}");
                    if (Selected() is { } selected && !reached.Contains(selected)) reached.Add(selected);
                }
                problems.AddRange(wanted.Except(reached).Select(item => $"{path}{Name(item)} isn't reached by the arrow keys."));
                foreach (ToolStripItem item in reached)
                {
                    string name = Name(item);
                    for (int i = 0; i < wanted.Length * 2 + 4 && Selected() != item; i++) Key(Keys.Down, $"Down to {path}{name}");
                    if (item is OpacityRow row)
                    {
                        int before = Ask(() => row.Value);
                        Key(Keys.Left, "Left on the opacity slider");
                        sliderMoved |= Ask(() => row.Value) != before;
                        Key(Keys.Right, "Right on the opacity slider");
                    }
                    if (item is ToolStripMenuItem parent && Ask(() => parent.HasDropDownItems))
                    {
                        Key(Keys.Right, $"Right into {path}{name}");
                        Thread.Sleep(200);
                        if (!Ask(() => parent.DropDown.Visible)) { problems.Add($"{path}{name} doesn't open with the right arrow."); continue; }
                        Walk(parent.DropDown, path + name + " › ");
                        Key(Keys.Left, $"Left out of {path}{name}");
                        Thread.Sleep(150);
                    }
                }
            }

            Exception? failure = null;
            var driver = new Thread(() =>
            {
                try
                {
                    Walk(menu, "");
                    if (Ask(() => menu.Items.OfType<OpacityRow>().Any()) && !sliderMoved) problems.Add("The opacity slider doesn't move with the arrow keys.");
                }
                catch (TimeoutException) { problems.Add($"The menu stopped answering after: {last}."); }
                catch (Exception e) { failure = e; }
                finally
                {
                    // Out of whatever holds the menu, so the test can end.
                    for (int i = 0; i < 6; i++) { Desktop.Press(Keys.Escape); Thread.Sleep(100); }
                }
            });
            driver.Start();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (driver.IsAlive && clock.Elapsed < TimeSpan.FromMinutes(4)) { Application.DoEvents(); Thread.Sleep(5); }
            menu.Close();
            Application.ThreadException -= said;
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            problems.AddRange(thrown.Select(error => "A menu handler threw " + error));
            if (driver.IsAlive) problems.Add("The walk ran out of time.");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }
}
