using System.Text.RegularExpressions;
using WinScreen.Settings;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>WinScreen's dialogs have one look, Settings' own, in light and dark (roadmap P2-5).</summary>
public sealed class ThemedDialogTests
{
    private static IEnumerable<Control> All(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_dialogs_use_the_settings_colours_buttons_and_boxes(string theme)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            MenuStyle.UseTheme(theme);
            try
            {
                Form[] dialogs =
                [
                    new PlacementDialog("Exact", new Rectangle(10, 20, 300, 200)),
                    new RuleDialog(new ProgramSetup { Program = "app", Pinned = true }),
                ];
                foreach (Form dialog in dialogs)
                    using (dialog)
                    {
                        Assert.Equal(SettingsStyle.Page, dialog.BackColor);
                        Assert.Equal(SettingsStyle.Text, dialog.ForeColor);
                        Control[] controls = All(dialog).ToArray();
                        Assert.All(controls.OfType<Button>(), button => Assert.IsType<PlainButton>(button));
                        Assert.All(controls.OfType<ComboBox>(), box => Assert.IsType<ThemedCombo>(box));
                        // A typed-into box is inside Settings' frame, not drawn by Windows with its own border.
                        Assert.All(controls.Where(control => control is TextBox or NumericUpDown && control.Parent is not NumericUpDown),
                            box => Assert.True(box.Parent is InputFrame || box.Parent?.Parent is InputFrame, $"{box.GetType().Name} has no frame."));
                        Assert.NotNull(dialog.AcceptButton);
                        Assert.NotNull(dialog.CancelButton);
                    }
            }
            finally { MenuStyle.UseTheme("system"); }
        });
    }

    /// <summary>In a high-contrast theme, Settings, dialogs and menus take Windows' own colours (roadmap P2-4).</summary>
    [Fact]
    public void A_high_contrast_theme_gives_settings_and_menus_windows_own_colours()
    {
        using (MenuStyle.AsIfHighContrast())
        {
            Assert.Equal(SystemColors.Window, SettingsStyle.Page);
            Assert.Equal(SystemColors.Window, SettingsStyle.Card);
            Assert.Equal(SystemColors.WindowText, SettingsStyle.Text);
            Assert.Equal(SystemColors.WindowText, SettingsStyle.Secondary);
            Assert.Equal(SystemColors.GrayText, SettingsStyle.Disabled);
            Assert.Equal(SystemColors.Highlight, SettingsStyle.Accent);
            Assert.Equal(SystemColors.HighlightText, SettingsStyle.OnAccent);
            Assert.Equal(SystemColors.Menu, MenuStyle.Back);
            Assert.Equal(SystemColors.MenuText, MenuStyle.Text);
            Assert.False(MenuStyle.Translucent);
        }
        if (!SystemInformation.HighContrast) Assert.NotEqual(SystemColors.Highlight, SettingsStyle.Accent); // and back to the theme
    }

    [Fact]
    public void Windows_own_message_box_is_left_only_where_WinScreen_has_stopped()
    {
        var found = new List<string>();
        foreach (string name in typeof(AppSettings).Assembly.GetManifestResourceNames().Length > 0
                     ? typeof(ThemedDialogTests).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("LocalizationSource.", StringComparison.Ordinal))
                     : [])
        {
            using var reader = new StreamReader(typeof(ThemedDialogTests).Assembly.GetManifestResourceStream(name)!);
            string code = reader.ReadToEnd();
            foreach (Match match in Regex.Matches(code, @"MessageBox\.Show\("))
                found.Add($"{name}:{code[..match.Index].Count(c => c == '\n') + 1}");
        }
        // The one left: the message after an unexpected stop, when a window of WinScreen's own may not be safe to show.
        Assert.Single(found);
        Assert.Contains("Program.cs", found[0], StringComparison.Ordinal);
    }
}
