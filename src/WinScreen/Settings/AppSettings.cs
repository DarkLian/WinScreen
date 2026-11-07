using static WinScreen.Settings.UiText;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinScreen.Native;

namespace WinScreen.Settings;

public enum AppCommand
{
    TogglePin = 1,
    OpacityUp,
    OpacityDown,
    ToggleClickThrough,
    OpenPip,
    RestoreAll,
    ToggleDark,
    ToggleGlass,
    ToggleAnchor,
    ToggleDefaultOpacity,
    SaveWindowSetup,
    ToggleShrink,
    ToggleCrop,
    RestoreWindow,
}

/// <summary>What a program's windows get automatically. Click-through and Glass are left out on purpose:
/// a window that opens unreachable, or with its colours removed, is never what someone wants.</summary>
public sealed class ProgramSetup
{
    public string Program { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public bool Pinned { get; set; }
    public byte Alpha { get; set; } = 255;
    public bool Dark { get; set; }

    /// <summary>False when the setup leaves a window exactly as it opens, so it isn't worth keeping.</summary>
    public bool ChangesAnything => Pinned || Alpha < 255 || Dark;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Pinned)
            parts.Add(T("pinned"));
        if (Alpha < 255)
            parts.Add(string.Format(T("{0}% solid"), (int)Math.Round(Alpha * 100.0 / 255)));
        if (Dark)
            parts.Add(T("dark"));
        string location = string.IsNullOrWhiteSpace(ExecutablePath) ? "" : $" ({ExecutablePath})";
        return $"{Program}: {string.Join(", ", parts)}{location}";
    }
}

/// <summary>What is wrong with one command's hotkey, in words for the person.</summary>
public sealed record HotkeyProblem(AppCommand Command, string Message);

/// <summary>Every command's name and place, so the tray, toolbar and Settings all agree.</summary>
public static class CommandCatalog
{
    /// <summary>The order commands appear in everywhere.</summary>
    public static readonly AppCommand[] Ordered =
    [
        AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.ToggleDefaultOpacity, AppCommand.OpacityUp,
        AppCommand.ToggleClickThrough, AppCommand.OpenPip, AppCommand.ToggleAnchor, AppCommand.ToggleDark,
        AppCommand.ToggleGlass, AppCommand.ToggleShrink, AppCommand.ToggleCrop, AppCommand.SaveWindowSetup,
        AppCommand.RestoreWindow, AppCommand.RestoreAll,
    ];

    /// <summary>The last command of each group, where a divider follows.</summary>
    public static bool EndsGroup(AppCommand command) =>
        command is AppCommand.ToggleClickThrough or AppCommand.ToggleCrop or AppCommand.SaveWindowSetup;

    public static bool IsExperimental(AppCommand command) =>
        command is AppCommand.ToggleDark or AppCommand.ToggleGlass or AppCommand.ToggleShrink or AppCommand.ToggleCrop;

    public static string Name(AppCommand command) => T(command switch
    {
        AppCommand.TogglePin => "Pin on top",
        AppCommand.OpacityDown => "More see-through",
        AppCommand.ToggleDefaultOpacity => "Use default opacity",
        AppCommand.OpacityUp => "More opaque",
        AppCommand.ToggleClickThrough => "Click-through",
        AppCommand.OpenPip => "Picture-in-Picture",
        AppCommand.ToggleAnchor => "Anchor",
        AppCommand.ToggleDark => "Dark mode",
        AppCommand.ToggleGlass => "Glass mode",
        AppCommand.ToggleShrink => "Shrink",
        AppCommand.ToggleCrop => "Crop",
        AppCommand.SaveWindowSetup => "Remember setup",
        AppCommand.RestoreWindow => "Restore this window",
        AppCommand.RestoreAll => "Restore all windows",
        _ => command.ToString(),
    });
}

/// <summary>Everything a person can choose, stored as JSON in their app data folder.</summary>
public sealed class AppSettings
{
    public const int LowestOpacityPercent = 20;
    public const int HighestOpacityPercent = 95;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public Dictionary<AppCommand, string> Hotkeys { get; set; } = new()
    {
        [AppCommand.TogglePin] = "Ctrl+Alt+T",
        [AppCommand.OpacityUp] = "Ctrl+Alt+Up",
        [AppCommand.OpacityDown] = "Ctrl+Alt+Down",
        [AppCommand.ToggleClickThrough] = "Ctrl+Alt+C",
        [AppCommand.OpenPip] = "Ctrl+Alt+P",
        [AppCommand.RestoreAll] = "Ctrl+Alt+R",
        [AppCommand.ToggleDark] = "Ctrl+Alt+D",
        [AppCommand.ToggleGlass] = "Ctrl+Alt+G",
        [AppCommand.ToggleAnchor] = "Ctrl+Alt+A",
        [AppCommand.ToggleDefaultOpacity] = "Ctrl+Alt+O",
        [AppCommand.SaveWindowSetup] = "Ctrl+Alt+Shift+S",
        [AppCommand.ToggleShrink] = "Ctrl+Alt+W",
        [AppCommand.ToggleCrop] = "Ctrl+Alt+X",
        [AppCommand.RestoreWindow] = "Ctrl+Alt+U",
    };

    /// <summary>Programs WinScreen never touches, for example games that dislike overlays.</summary>
    public List<string> ExcludedPrograms { get; set; } = ["csgo", "cs2", "valorant", "eldenring"];

    /// <summary>When any are listed, WinScreen works on these programs and no others.</summary>
    public List<string> IncludedPrograms { get; set; } = [];

    /// <summary>What each program's windows get when they first come forward.</summary>
    public List<ProgramSetup> SavedSetups { get; set; } = [];

    public bool ShowToolbar { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool AutomaticUpdates { get; set; }
    public string Language { get; set; } = "system";
    public bool PauseInFullScreen { get; set; } = true;
    public bool AllowPipInFullScreen { get; set; }
    public bool PausePinWhenMaximized { get; set; }
    public bool OpaqueOnHover { get; set; }
    public bool HighlightPinned { get; set; }
    public bool PipAutoMove { get; set; }
    public bool ExperimentalFeatures { get; set; }
    public bool PipClickForwarding { get; set; }
    [JsonConverter(typeof(PipPreferencesConverter))]
    public PipPreferences Pip { get; set; } = new();
    public bool? StartWithWindows { get; set; }

    /// <summary>True once the first-run hint has been shown, so it doesn't return at every sign-in.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>How solid a window is after the default see-through command, and after click-through.</summary>
    public int DefaultOpacityPercent { get; set; } = 80;

    /// <summary>The default level as Windows wants it, whatever the settings file says.</summary>
    [JsonIgnore]
    public byte DefaultAlpha =>
        (byte)Math.Round(Math.Clamp(DefaultOpacityPercent, LowestOpacityPercent, HighestOpacityPercent) * 255 / 100.0);

    public ProgramSetup? SetupFor(string program)
    {
        return SavedSetups.Find(setup => string.Equals(setup.Program, program, StringComparison.OrdinalIgnoreCase));
    }

    public ProgramSetup? SetupForWindow(nint window)
    {
        string path = Windows.WindowInfo.ExecutablePath(window);
        return SavedSetups.Find(setup => !string.IsNullOrEmpty(setup.ExecutablePath) &&
                                         string.Equals(setup.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))
               ?? SavedSetups.Find(setup => string.IsNullOrEmpty(setup.ExecutablePath) &&
                                            string.Equals(setup.Program, Windows.WindowInfo.ProgramOf(window),
                                                StringComparison.OrdinalIgnoreCase));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public void CopyFrom(AppSettings settings)
    {
        var copy = settings.Clone();
        foreach (var property in typeof(AppSettings).GetProperties().Where(property => property.CanWrite))
            property.SetValue(this, property.GetValue(copy));
    }

    /// <summary>Reads the settings. A missing or damaged file gives the defaults.</summary>
    public static AppSettings Load(string path)
    {
        var defaults = new AppSettings();
        if (!File.Exists(path))
            return defaults;
        defaults.Pip.Backend = PipBackendPreference.Dwm;

        try
        {
            if (new FileInfo(path).Length > 1024 * 1024)
                throw new JsonException("Settings file is too large.");
            string json = File.ReadAllText(path);
            AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? defaults;
            using var document = JsonDocument.Parse(json);
            // Existing installations keep compositor-managed PiP until the person selects capture.
            loaded.Pip = document.RootElement.ValueKind == JsonValueKind.Object &&
                         document.RootElement.TryGetProperty(nameof(Pip), out var pip) && pip.ValueKind == JsonValueKind.Object
                ? (loaded.Pip ?? new PipPreferences()).Validated()
                : new PipPreferences { Backend = PipBackendPreference.Dwm };
            if (loaded.Hotkeys is null || loaded.ExcludedPrograms is null || loaded.IncludedPrograms is null ||
                loaded.SavedSetups is null || loaded.SavedSetups.Any(value => value is null) ||
                loaded.Hotkeys.Any(pair => pair.Value is null))
            {
                try
                {
                    File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            loaded.Hotkeys ??= [];
            loaded.ExcludedPrograms = (loaded.ExcludedPrograms ?? defaults.ExcludedPrograms)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            loaded.IncludedPrograms = (loaded.IncludedPrograms ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            loaded.SavedSetups = (loaded.SavedSetups ?? [])
                .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.Program)).ToList();
            loaded.Language = loaded.Language is "en" or "zh-Hans" or "zh-Hant" ? loaded.Language : "system";
            loaded.DefaultOpacityPercent =
                Math.Clamp(loaded.DefaultOpacityPercent, LowestOpacityPercent, HighestOpacityPercent);
            foreach ((AppCommand command, string hotkey) in defaults.Hotkeys)
            {
                loaded.Hotkeys.TryAdd(command, hotkey);
                if (loaded.Hotkeys[command] is null)
                    loaded.Hotkeys[command] = hotkey;
            }

            foreach (AppCommand command in loaded.Hotkeys.Keys.Where(command => !Enum.IsDefined(command)).ToArray())
                loaded.Hotkeys.Remove(command);
            return loaded;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }

            return defaults;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Format));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Reads and writes hotkeys written the way people say them, such as "Ctrl+Alt+T".</summary>
public static class HotkeyText
{
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;

    /// <summary>The names people use for keys whose WinForms names are internal ones like Oem2 or D1.</summary>
    private static readonly Dictionary<Keys, string> Names = new()
    {
        [Keys.D0] = "0", [Keys.D1] = "1", [Keys.D2] = "2", [Keys.D3] = "3", [Keys.D4] = "4",
        [Keys.D5] = "5", [Keys.D6] = "6", [Keys.D7] = "7", [Keys.D8] = "8", [Keys.D9] = "9",
        [Keys.OemMinus] = "-", [Keys.Oemplus] = "Plus", [Keys.Oemcomma] = ",", [Keys.OemPeriod] = ".",
        [Keys.OemQuestion] = "/", [Keys.OemSemicolon] = ";", [Keys.OemQuotes] = "'", [Keys.Oemtilde] = "`",
        [Keys.OemOpenBrackets] = "[", [Keys.OemCloseBrackets] = "]", [Keys.OemPipe] = "\\",
        [Keys.PageUp] = "PageUp", [Keys.PageDown] = "PageDown", [Keys.Capital] = "CapsLock", [Keys.Return] = "Enter",
        [Keys.Back] = "Backspace", [Keys.Snapshot] = "PrintScreen", [Keys.Scroll] = "ScrollLock",
    };

    private static readonly Dictionary<string, Keys> KeysByName =
        Names.ToDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>True while either Windows key is held; WinForms doesn't report it as a modifier.</summary>
    internal static bool WinKeyDown =>
        (User32.GetKeyState(0x5B) & 0x8000) != 0 || (User32.GetKeyState(0x5C) & 0x8000) != 0;

    public static bool IsFunctionKey(Keys key) => key is >= Keys.F1 and <= Keys.F24;

    /// <summary>The character this combination types on the current keyboard, or null when it types nothing.</summary>
    internal static string? TypedText(uint modifiers, Keys key)
    {
        const uint altGr = User32.MOD_CONTROL | User32.MOD_ALT;
        return (modifiers & altGr) == altGr && (modifiers & ModWin) == 0
            ? KeyboardLayout.TextWithAltGr(key, (modifiers & ModShift) != 0)
            : null;
    }

    public static bool TryParse(string text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= User32.MOD_CONTROL; break;
                case "Alt": modifiers |= User32.MOD_ALT; break;
                case "Shift": modifiers |= ModShift; break;
                case "Win": modifiers |= ModWin; break;
                default:
                    if (key != Keys.None || !TryReadKey(part, out key))
                        return false;
                    break;
            }
        }

        return key != Keys.None && (modifiers != 0 || IsFunctionKey(key));
    }

    private static bool TryReadKey(string name, out Keys key)
    {
        if (KeysByName.TryGetValue(name, out key))
            return true;
        return !char.IsAsciiDigit(name[0]) && Enum.TryParse(name, out key) && Enum.IsDefined(key);
    }

    public static string Format(Keys keyData, bool win = false)
    {
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control))
            parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt))
            parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift))
            parts.Add("Shift");
        if (win)
            parts.Add("Win");
        Keys key = keyData & Keys.KeyCode;
        parts.Add(Names.GetValueOrDefault(key) ?? key.ToString());
        return string.Join("+", parts);
    }
}
