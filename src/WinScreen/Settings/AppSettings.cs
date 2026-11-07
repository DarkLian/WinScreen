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
}

/// <summary>What a program's windows get automatically. Click-through and Glass are left out on purpose:
/// a window that opens unreachable, or with its colours removed, is never what someone wants.</summary>
public sealed class ProgramSetup
{
    public string Program { get; set; } = "";
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
        return $"{Program}: {string.Join(", ", parts)}";
    }
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
        [AppCommand.SaveWindowSetup] = "Ctrl+Alt+S",
        [AppCommand.ToggleShrink] = "Ctrl+Alt+W",
        [AppCommand.ToggleCrop] = "Ctrl+Alt+X",
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
    public bool PausePinWhenMaximized { get; set; }
    public bool OpaqueOnHover { get; set; }
    public bool HighlightPinned { get; set; }
    public bool PipAutoMove { get; set; }

    /// <summary>How solid a window is after the default see-through command, and after click-through.</summary>
    public int DefaultOpacityPercent { get; set; } = 80;

    /// <summary>The default level as Windows wants it, whatever the settings file says.</summary>
    [JsonIgnore]
    public byte DefaultAlpha => (byte)Math.Round(Math.Clamp(DefaultOpacityPercent, LowestOpacityPercent, HighestOpacityPercent) * 255 / 100.0);

    public ProgramSetup? SetupFor(string program)
    {
        return SavedSetups.Find(setup => string.Equals(setup.Program, program, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the settings. A missing or damaged file gives the defaults.</summary>
    public static AppSettings Load(string path)
    {
        var defaults = new AppSettings();
        if (!File.Exists(path))
            return defaults;

        try
        {
            AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? defaults;
            foreach ((AppCommand command, string hotkey) in defaults.Hotkeys)
                loaded.Hotkeys.TryAdd(command, hotkey);
            return loaded;
        }
        catch (JsonException)
        {
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
                    if (key != Keys.None || !Enum.TryParse(part, out key))
                        return false;
                    break;
            }
        }

        return key != Keys.None && modifiers != 0;
    }

    public static string Format(Keys keyData)
    {
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control))
            parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt))
            parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift))
            parts.Add("Shift");
        parts.Add((keyData & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }
}
