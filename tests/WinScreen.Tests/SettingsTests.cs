using Microsoft.Win32;
using WinScreen.Localization;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

public class SettingsTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");

    [Fact]
    public void Missing_file_gives_defaults()
    {
        AppSettings settings = AppSettings.Load(NewPath());
        Assert.Equal("Ctrl+Alt+T", settings.Hotkeys[AppCommand.TogglePin]);
        Assert.True(settings.ShowToolbar);
        Assert.True(settings.ShowNotifications);
        Assert.False(settings.AutomaticUpdates);
        Assert.Equal("system", settings.Language);
    }

    [Fact]
    public void Damaged_file_gives_defaults()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.Equal("Ctrl+Alt+D", AppSettings.Load(path).Hotkeys[AppCommand.ToggleDark]);
    }

    [Theory]
    [InlineData("{\"Hotkeys\":null,\"ExcludedPrograms\":null,\"IncludedPrograms\":null,\"SavedSetups\":null}")]
    [InlineData("{\"Hotkeys\":{\"TogglePin\":null},\"SavedSetups\":[null,{\"Program\":null}]}")]
    public void Valid_JSON_with_null_collections_and_entries_cannot_prevent_startup(string json)
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        AppSettings loaded = AppSettings.Load(path);
        Assert.Equal("Ctrl+Alt+T", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.NotNull(loaded.ExcludedPrograms);
        Assert.NotNull(loaded.IncludedPrograms);
        Assert.Empty(loaded.SavedSetups);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.invalid-*"));
    }

    [Fact]
    public void Choices_survive_a_restart()
    {
        string path = NewPath();
        var settings = new AppSettings
        {
            OpaqueOnHover = true, ExcludedPrograms = ["exampleprogram"], ShowNotifications = false,
            Language = "zh-Hant", AutomaticUpdates = true
        };
        settings.Hotkeys[AppCommand.TogglePin] = "Ctrl+Shift+F9";
        settings.Save(path);

        AppSettings loaded = AppSettings.Load(path);

        Assert.True(loaded.OpaqueOnHover);
        Assert.False(loaded.ShowNotifications);
        Assert.True(loaded.AutomaticUpdates);
        Assert.Equal("zh-Hant", loaded.Language);
        Assert.Equal(["exampleprogram"], loaded.ExcludedPrograms);
        Assert.Equal("Ctrl+Shift+F9", loaded.Hotkeys[AppCommand.TogglePin]);
    }

    [Theory]
    [InlineData(80, 204)]
    [InlineData(50, 128)]
    [InlineData(1, 51)]
    [InlineData(100, 242)]
    public void The_default_level_stays_within_the_allowed_range(int percent, byte alpha)
    {
        Assert.Equal(alpha, new AppSettings { DefaultOpacityPercent = percent }.DefaultAlpha);
    }

    [Fact]
    public void A_saved_setup_survives_a_restart_and_is_found_in_any_case()
    {
        string path = NewPath();
        var settings = new AppSettings
        {
            SavedSetups = [new ProgramSetup { Program = "ExampleProgram", Pinned = true, Alpha = 128, Dark = true }]
        };
        settings.Save(path);

        ProgramSetup? loaded = AppSettings.Load(path).SetupFor("exampleprogram");

        Assert.NotNull(loaded);
        Assert.True(loaded.Pinned);
        Assert.True(loaded.Dark);
        Assert.Equal(128, loaded.Alpha);
        Assert.Equal("ExampleProgram: pinned, 50% solid, dark", loaded.ToString());
        Assert.Null(settings.SetupFor("anotherprogram"));
    }

    [Fact]
    public void A_setup_that_changes_nothing_is_not_worth_keeping()
    {
        Assert.False(new ProgramSetup { Program = "exampleprogram" }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "exampleprogram", Alpha = 200 }.ChangesAnything);
    }

    [Fact]
    public void Commands_added_in_a_later_version_get_their_default_hotkey()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Hotkeys\":{\"TogglePin\":\"Ctrl+Alt+F1\"}}");

        AppSettings loaded = AppSettings.Load(path);

        Assert.Equal("Ctrl+Alt+F1", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.Equal("Ctrl+Alt+D", loaded.Hotkeys[AppCommand.ToggleDark]);
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", User32.MOD_CONTROL | User32.MOD_ALT, Keys.T)]
    [InlineData("Ctrl+Alt+Up", User32.MOD_CONTROL | User32.MOD_ALT, Keys.Up)]
    [InlineData("Alt+F9", User32.MOD_ALT, Keys.F9)]
    public void Hotkeys_are_read_the_way_people_write_them(string text, uint modifiers, Keys key)
    {
        Assert.True(HotkeyText.TryParse(text, out uint readModifiers, out Keys readKey));
        Assert.Equal(modifiers, readModifiers);
        Assert.Equal(key, readKey);
    }

    [Theory]
    [InlineData(Keys.D1, "Ctrl+Alt+1")]
    [InlineData(Keys.OemQuestion, "Ctrl+Alt+/")]
    [InlineData(Keys.OemSemicolon, "Ctrl+Alt+;")]
    [InlineData(Keys.Oemplus, "Ctrl+Alt+Plus")]
    [InlineData(Keys.OemMinus, "Ctrl+Alt+-")]
    [InlineData(Keys.PageDown, "Ctrl+Alt+PageDown")]
    [InlineData(Keys.Capital, "Ctrl+Alt+CapsLock")]
    [InlineData(Keys.Enter, "Ctrl+Alt+Enter")]
    public void Keys_are_shown_by_the_names_people_use_and_read_back(Keys pressed, string shown)
    {
        Assert.Equal(shown, HotkeyText.Format(Keys.Control | Keys.Alt | pressed));
        Assert.True(HotkeyText.TryParse(shown, out _, out Keys key));
        Assert.Equal(pressed, key);
    }

    [Theory]
    [InlineData("Ctrl+Alt+D1", Keys.D1)]
    [InlineData("Ctrl+Alt+OemQuestion", Keys.OemQuestion)]
    [InlineData("Ctrl+Alt+Next", Keys.PageDown)]
    public void Hotkeys_saved_by_earlier_versions_still_load(string saved, Keys expected)
    {
        Assert.True(HotkeyText.TryParse(saved, out _, out Keys key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Win_combinations_and_lone_function_keys_are_accepted()
    {
        Assert.Equal("Ctrl+Win+K", HotkeyText.Format(Keys.Control | Keys.K, win: true));
        Assert.True(HotkeyText.TryParse("Ctrl+Win+K", out uint modifiers, out _));
        Assert.Equal(User32.MOD_CONTROL | 0x8u, modifiers);
        Assert.True(HotkeyText.TryParse("F9", out _, out Keys key));
        Assert.Equal(Keys.F9, key);
        Assert.False(HotkeyText.TryParse("65", out _, out _));
    }

    [Fact]
    public void New_installs_leave_the_common_ide_shortcut_free()
    {
        Assert.Equal("Ctrl+Alt+Shift+S", new AppSettings().Hotkeys[AppCommand.SaveWindowSetup]);
    }

    [Fact]
    public void Saved_setups_show_their_effects_before_a_long_path()
    {
        var setup = new ProgramSetup
            { Program = "Example", ExecutablePath = @"C:\\Long\\Path\\Example.exe", Pinned = true };
        Assert.Equal(@"Example: pinned (C:\\Long\\Path\\Example.exe)", setup.ToString());
    }

    [Fact]
    public void Every_command_has_one_place_and_a_name()
    {
        Assert.Equal(Enum.GetValues<AppCommand>().Length, CommandCatalog.All.Distinct().Count());
        Assert.All(CommandCatalog.All,
            command => Assert.NotEqual(command.ToString(), CommandCatalog.Name(command)));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("")]
    public void Invalid_hotkeys_are_refused(string text)
    {
        Assert.False(HotkeyText.TryParse(text, out _, out _));
    }

    [Fact]
    public void A_pressed_combination_is_written_back_the_same_way()
    {
        string text = HotkeyText.Format(Keys.Control | Keys.Alt | Keys.K);
        Assert.Equal("Ctrl+Alt+K", text);
        Assert.True(HotkeyText.TryParse(text, out _, out Keys key));
        Assert.Equal(Keys.K, key);
    }
}

public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _key = @"Software\WinScreenTests\" + Path.GetRandomFileName();

    public void Dispose() =>
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\WinScreenTests", throwOnMissingSubKey: false);

    private void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Saving_applies_every_choice()
    {
        OnUiThread(() =>
        {
            var settings = new AppSettings();
            AppSettings? applied = null;
            var startup = new StartWithWindows(_key);
            using var window = new SettingsWindow(settings, startup, chosen =>
            {
                applied = chosen;
                return [];
            });
            _ = window.Handle;

            window.CaptureHotkey(AppCommand.OpenPip, Keys.Control | Keys.Shift | Keys.F9);
            window.CaptureHotkey(AppCommand.ToggleDark, Keys.Delete);
            window.CaptureHotkey(AppCommand.TogglePin, Keys.ControlKey | Keys.Control);
            window.CaptureHotkey(AppCommand.RestoreAll, Keys.K);
            window.AddExcluded("ExampleProgram.exe");
            window.SetOption(nameof(AppSettings.OpaqueOnHover), true);
            window.SetOption("StartWithWindows", true);
            window.SetOption(nameof(AppSettings.HighlightPinned), true);
            window.SetOption(nameof(AppSettings.PipAutoMove), true);
            window.SetOption(nameof(AppSettings.ShowNotifications), false);
            window.SetOption(nameof(AppSettings.AutomaticUpdates), true);
            window.AddIncluded("AnotherProgram.EXE");
            window.SetDefaultOpacity(60);

            Assert.True(window.SaveChanges());

            Assert.NotNull(applied);
            Assert.Equal("Ctrl+Shift+F9", applied.Hotkeys[AppCommand.OpenPip]);
            Assert.Equal("", applied.Hotkeys[AppCommand.ToggleDark]);
            Assert.Equal("Ctrl+Alt+T", applied.Hotkeys[AppCommand.TogglePin]);
            Assert.Equal("Ctrl+Alt+R", applied.Hotkeys[AppCommand.RestoreAll]);
            Assert.Contains("ExampleProgram", applied.ExcludedPrograms);
            Assert.True(applied.OpaqueOnHover);
            Assert.False(startup.IsOn); // The apply callback owns the save transaction.
            Assert.True(applied.StartWithWindows);
            Assert.True(applied.HighlightPinned);
            Assert.True(applied.PipAutoMove);
            Assert.False(applied.ShowNotifications);
            Assert.True(applied.AutomaticUpdates);
            Assert.Equal(["AnotherProgram"], applied.IncludedPrograms);
            Assert.Equal(60, applied.DefaultOpacityPercent);
        });
    }

    [Fact]
    public void A_setup_can_be_forgotten_and_one_saved_meanwhile_is_kept()
    {
        OnUiThread(() =>
        {
            var forgotten = new ProgramSetup { Program = "exampleprogram", Pinned = true };
            var settings = new AppSettings { SavedSetups = [forgotten] };
            using var window = new SettingsWindow(settings, new StartWithWindows(_key), draft => { settings.CopyFrom(draft); return []; });
            _ = window.Handle;

            var savedByHotkey = new ProgramSetup { Program = "anotherprogram", Dark = true };
            settings.SavedSetups.Add(savedByHotkey);
            window.RemoveSetup(forgotten);
            Assert.True(window.SaveChanges());

            var retained = Assert.Single(settings.SavedSetups);
            Assert.Equal(savedByHotkey.Program, retained.Program);
            Assert.True(retained.Dark);
        });
    }

    [Theory]
    [InlineData("zh-Hans", "WinScreen 设置", "置顶", 2)]
    [InlineData("zh-Hant", "WinScreen 設定", "置頂", 3)]
    public void Chinese_settings_display_translations_and_preserve_the_language_choice(string language, string title,
        string pin, int index)
    {
        OnUiThread(() =>
        {
            try
            {
                UiText.Initialize(language);
                var settings = new AppSettings { Language = language, ShowNotifications = false };
                using var window = new SettingsWindow(settings, new StartWithWindows(_key), draft => { settings.CopyFrom(draft); return []; });
                _ = window.Handle;
                Control[] controls = Descendants(window).ToArray();
                Assert.Equal(title, window.Text);
                Assert.Contains(controls.OfType<ListView>().Single().Items.Cast<ListViewItem>(), row => row.Text == pin);
                var languages = controls.OfType<ComboBox>().First();
                Assert.Equal(index, languages.SelectedIndex);
                Assert.True(window.SaveChanges());
                Assert.Equal(language, settings.Language);
                Assert.False(settings.ShowNotifications);
                languages.SelectedIndex = 1;
                Assert.True(window.SaveChanges());
                Assert.Equal("en", settings.Language);
            }
            finally
            {
                UiText.Initialize("en");
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }

    [Fact]
    public void A_taken_hotkey_keeps_the_window_open_and_says_which()
    {
        OnUiThread(() =>
        {
            using var window = new SettingsWindow(new AppSettings(), new StartWithWindows(_key),
                _ => [new HotkeyProblem(AppCommand.TogglePin, "Ctrl+Alt+T")]);
            _ = window.Handle;
            Assert.False(window.SaveChanges());
        });
    }

    [Fact]
    public void Apply_is_offered_only_for_real_changes_and_keeps_the_window_open()
    {
        OnUiThread(() =>
        {
            var settings = new AppSettings { ShowToolbar = true };
            int applied = 0;
            using var window = new SettingsWindow(settings, new StartWithWindows(_key), draft =>
            {
                applied++;
                settings.CopyFrom(draft);
                return [];
            });
            _ = window.Handle;
            window.RefreshApply();
            Assert.False(window.HasChanges);

            window.SetOption(nameof(AppSettings.ShowToolbar), false);
            window.RefreshApply();
            Assert.True(window.HasChanges);
            window.SetOption(nameof(AppSettings.ShowToolbar), true);
            window.RefreshApply();
            Assert.False(window.HasChanges); // Changed and changed back: nothing to apply.

            window.SetDefaultOpacity(60);
            window.AddExcluded("notepad");
            window.RefreshApply();
            Assert.True(window.HasChanges);
            Assert.True(window.ApplyChanges());
            Assert.Equal(1, applied);
            Assert.False(window.HasChanges);
            Assert.False(window.IsDisposed);
            Assert.Equal(60, settings.DefaultOpacityPercent);
            Assert.Contains("notepad", settings.ExcludedPrograms);

            Descendants(window).OfType<ComboBox>().Skip(1).First().Text = "Typed.exe";
            window.RefreshApply();
            Assert.True(window.HasChanges); // A name typed but not added counts, as it does for Save.
        });
    }

    [Fact]
    public void Apply_stays_available_when_a_hotkey_is_taken()
    {
        OnUiThread(() =>
        {
            var settings = new AppSettings();
            using var window = new SettingsWindow(settings, new StartWithWindows(_key),
                _ => [new HotkeyProblem(AppCommand.TogglePin, "Ctrl+Alt+T")]);
            _ = window.Handle;
            window.SetDefaultOpacity(60);
            window.RefreshApply();
            Assert.False(window.ApplyChanges());
            window.RefreshApply();
            Assert.True(window.HasChanges);
        });
    }

    [Fact]
    public void A_name_typed_but_not_added_is_still_saved()
    {
        OnUiThread(() =>
        {
            AppSettings? applied = null;
            using var window = new SettingsWindow(new AppSettings(), new StartWithWindows(_key), chosen =>
            {
                applied = chosen;
                return [];
            });
            _ = window.Handle;
            Descendants(window).OfType<ComboBox>().Skip(1).First().Text = "Typed.exe";
            Assert.True(window.SaveChanges());
            Assert.Contains("Typed", applied!.ExcludedPrograms);
        });
    }

    [Fact]
    public void A_key_that_cant_be_a_hotkey_is_explained_and_keys_with_modifiers_are_not_clears()
    {
        OnUiThread(() =>
        {
            using var window = new SettingsWindow(new AppSettings(), new StartWithWindows(_key), _ => []);
            _ = window.Handle;
            window.CaptureHotkey(AppCommand.RestoreAll, Keys.K);
            Assert.Contains(Descendants(window),
                control => control.Text.StartsWith("Hold Ctrl", StringComparison.Ordinal));
            window.CaptureHotkey(AppCommand.RestoreAll, Keys.F9);
            window.CaptureHotkey(AppCommand.TogglePin, Keys.Control | Keys.Alt | Keys.Back);
            AppSettings? applied = null;
            using var second = new SettingsWindow(new AppSettings(), new StartWithWindows(_key), chosen =>
            {
                applied = chosen;
                return [];
            });
            _ = second.Handle;
            second.CaptureHotkey(AppCommand.TogglePin, Keys.Control | Keys.Alt | Keys.Back);
            second.CaptureHotkey(AppCommand.RestoreAll, Keys.F9);
            Assert.True(second.SaveChanges());
            Assert.Equal("Ctrl+Alt+Backspace", applied!.Hotkeys[AppCommand.TogglePin]);
            Assert.Equal("F9", applied.Hotkeys[AppCommand.RestoreAll]);
        });
    }

    [Fact]
    public void A_rejected_save_does_not_change_live_settings_or_startup_registration()
    {
        OnUiThread(() =>
        {
            var settings = new AppSettings();
            var startup = new StartWithWindows(_key);
            using var window = new SettingsWindow(settings, startup, _ =>
                [new HotkeyProblem(AppCommand.RestoreAll, "Ctrl+Alt+F21")]);
            window.CaptureHotkey(AppCommand.RestoreAll, Keys.Control | Keys.Alt | Keys.F21);
            window.SetOption("StartWithWindows", true);
            window.SetOption(nameof(AppSettings.ShowNotifications), false);
            window.AddExcluded("NewExclusion");
            Assert.False(window.SaveChanges());
            Assert.Equal("Ctrl+Alt+R", settings.Hotkeys[AppCommand.RestoreAll]);
            Assert.True(settings.ShowNotifications);
            Assert.DoesNotContain("NewExclusion", settings.ExcludedPrograms);
            Assert.False(startup.IsOn);
        });
    }

    [Fact]
    public void Start_with_windows_can_be_turned_off_again()
    {
        var startup = new StartWithWindows(_key);
        Assert.False(startup.IsOn);
        startup.IsOn = true;
        Assert.True(startup.IsOn);
        startup.IsOn = false;
        Assert.False(startup.IsOn);
    }
}
