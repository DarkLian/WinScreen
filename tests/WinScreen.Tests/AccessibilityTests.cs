using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>What a screen reader can say about WinScreen's own windows (roadmap P2-4).</summary>
[Trait("Category", "RealWindows")]
public sealed class AccessibilityTests
{
    private static IEnumerable<Control> All(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));

    /// <summary>
    /// The name a screen reader reads for a control: its accessible name, or its own text, or the label just before it
    /// (Windows Forms gives a box the text of the label that comes before it in tab order).
    /// </summary>
    internal static string? SpokenName(Control control)
    {
        if (!string.IsNullOrWhiteSpace(control.AccessibilityObject.Name)) return control.AccessibilityObject.Name;
        return null;
    }

    private static List<string> Unnamed(Control root) =>
        All(root).Where(control => control.CanSelect && control.TabStop && control.Visible)
            .Where(control => SpokenName(control) is null)
            .Select(control => $"{control.GetType().Name} in {control.Parent?.GetType().Name} ({control.Name})").ToList();

    [Fact]
    public void Every_control_in_settings_that_takes_the_keyboard_has_a_name_to_read()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var window = new SettingsWindow(new AppSettings { Language = "en" }, new StartWithWindows(tasks: NoTasks.Instance), _ => []);
            window.Show();
            Application.DoEvents();
            var problems = new List<string>();
            // Each page in turn: a page that isn't open counts its controls as not shown.
            foreach (TabControl tabs in All(window).OfType<TabControl>().ToArray())
                foreach (TabPage page in tabs.TabPages.Cast<TabPage>().ToArray())
                {
                    tabs.SelectedTab = page;
                    Application.DoEvents();
                    problems.AddRange(Unnamed(page).Select(problem => $"{page.Text}: {problem}"));
                }
            problems.AddRange(Unnamed(window).Where(problem => !problems.Any(p => p.EndsWith(problem, StringComparison.Ordinal))));
            window.Close();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
        });
    }

    [Fact]
    public void Every_control_in_the_dialogs_that_takes_the_keyboard_has_a_name_to_read()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Form[] dialogs = [new PlacementDialog("Exact", new Rectangle(10, 20, 300, 200)), new RuleDialog(new ProgramSetup { Program = "app" })];
            var problems = new List<string>();
            foreach (Form dialog in dialogs)
                using (dialog)
                {
                    dialog.Show();
                    Application.DoEvents();
                    problems.AddRange(Unnamed(dialog).Select(problem => $"{dialog.GetType().Name}: {problem}"));
                    dialog.Close();
                }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }
}
