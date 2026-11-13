using static WinScreen.Localization.UiText;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    PickPipSource,
    Arrange,
    AlignTopLeft,
    AlignTop,
    AlignTopRight,
    AlignLeft,
    AlignCenter,
    AlignRight,
    AlignBottomLeft,
    AlignBottom,
    AlignBottomRight,
    MoveToNextDisplay,
    PlaceExactly,
    RestorePlacement,
    SendToBottom,
    ShowWindowInfo,
    ToggleRollUp,
    ShowWindowMenu,
    ToggleBorderless,
    ToggleBorderlessFill,
    ToggleDarkTitleBar,
    ClickThroughOffEverywhere,
    UnpinEverywhere,
    HideAnchors,
    HideToolbar,
}

/// <summary>The key to hold for modifier move and resize.</summary>
public enum ModifierKey { Alt, Win }
public enum WindowMenuGesture { CtrlRightClick, CtrlShiftRightClick, CtrlMiddleClick }

/// <summary>What a program's windows get automatically. Click-through and Glass are left out on purpose:
/// a window that opens unreachable, or with its colours removed, is never what someone wants.</summary>
public sealed class ProgramSetup
{
    public string Program { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public bool Pinned { get; set; }
    public byte Alpha { get; set; } = 255;
    public bool Dark { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(SavedViewConverter))]
    public PipPreset? View { get; set; }
    public bool ViewKeepsUpdating { get; set; }

    /// <summary>False when the setup leaves a window exactly as it opens, so it isn't worth keeping.</summary>
    public bool ChangesAnything => Pinned || Alpha < 255 || Dark || View is not null;

    /// <summary>What tells one program's setup from another's.</summary>
    internal string Key => string.IsNullOrEmpty(ExecutablePath) ? Program : ExecutablePath;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Pinned)
            parts.Add(T("setups.pinned"));
        if (Alpha < 255)
            parts.Add(string.Format(T("window.opacity.percent"), (int)Math.Round(Alpha * 100.0 / 255)));
        if (Dark)
            parts.Add(T("setups.dark"));
        if (View is not null)
            parts.Add(T("setups.view"));
        string location = string.IsNullOrWhiteSpace(ExecutablePath) ? "" : $" ({ExecutablePath})";
        return $"{Program}: {string.Join(", ", parts)}{location}";
    }
}

/// <summary>Where the Settings window was: its size when not maximized, and whether it was maximized.</summary>
public sealed record SettingsPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public Rectangle Bounds => new(X, Y, Width, Height);

    /// <summary>
    /// The place to open at, or null when it would be mostly off every screen now, as after a display was unplugged.
    /// Its title bar must be on a screen, so it can always be moved.
    /// </summary>
    internal SettingsPlacement? OnScreen(IEnumerable<Rectangle> workingAreas)
    {
        if (Width < 200 || Height < 150) return null;
        var title = new Rectangle(X + 40, Y, Math.Max(1, Width - 80), 30);
        return workingAreas.Any(area => area.IntersectsWith(title)) ? this : null;
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
        AppCommand.ToggleGlass, AppCommand.ToggleShrink, AppCommand.ToggleCrop, AppCommand.Arrange, AppCommand.SaveWindowSetup,
        AppCommand.RestoreWindow, AppCommand.RestoreAll,
    ];

    /// <summary>
    /// The toolbar's buttons, in order: the commands above but Restore all windows, then the window menu, which lists
    /// what the bar doesn't show itself.
    /// </summary>
    public static readonly AppCommand[] ToolbarButtons =
        [.. Ordered.Where(command => command != AppCommand.RestoreAll), AppCommand.ShowWindowMenu];

    /// <summary>Commands with a place in the tray and Hotkeys but no toolbar button.</summary>
    public static readonly AppCommand[] Extra =
    [
        AppCommand.PickPipSource, AppCommand.AlignTopLeft, AppCommand.AlignTop, AppCommand.AlignTopRight,
        AppCommand.AlignLeft, AppCommand.AlignCenter, AppCommand.AlignRight, AppCommand.AlignBottomLeft,
        AppCommand.AlignBottom, AppCommand.AlignBottomRight, AppCommand.MoveToNextDisplay, AppCommand.PlaceExactly,
        AppCommand.RestorePlacement, AppCommand.SendToBottom, AppCommand.ShowWindowInfo, AppCommand.ToggleRollUp,
        AppCommand.ShowWindowMenu, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar,
    ];

    /// <summary>Commands for every window at once, in the tray and Hotkeys only. None has a key until one is chosen.</summary>
    public static readonly AppCommand[] Everywhere =
    [
        AppCommand.ClickThroughOffEverywhere, AppCommand.UnpinEverywhere, AppCommand.HideAnchors, AppCommand.HideToolbar,
    ];

    /// <summary>True for a command that works on no window in particular.</summary>
    public static bool ForAllWindows(AppCommand command) =>
        command is AppCommand.RestoreAll or AppCommand.PickPipSource || Everywhere.Contains(command);

    /// <summary>What the Arrange menu offers, in order, with null where a divider goes.</summary>
    public static readonly AppCommand?[] ArrangeMenu =
    [
        AppCommand.AlignTopLeft, AppCommand.AlignTop, AppCommand.AlignTopRight, AppCommand.AlignLeft,
        AppCommand.AlignCenter, AppCommand.AlignRight, AppCommand.AlignBottomLeft, AppCommand.AlignBottom,
        AppCommand.AlignBottomRight, null, AppCommand.MoveToNextDisplay, AppCommand.PlaceExactly,
        AppCommand.RestorePlacement, null, AppCommand.ToggleRollUp, AppCommand.SendToBottom, null, AppCommand.ShowWindowInfo,
    ];

    /// <summary>The corner or edge an alignment command goes to, or null for any other command.</summary>
    public static Windows.WindowAnchor? AnchorOf(AppCommand command) =>
        command is >= AppCommand.AlignTopLeft and <= AppCommand.AlignBottomRight
            ? (Windows.WindowAnchor)(command - AppCommand.AlignTopLeft)
            : null;

    /// <summary>Everything that can have a hotkey.</summary>
    public static IEnumerable<AppCommand> All => Ordered.Concat(Extra).Concat(Everywhere);

    /// <summary>The last command of each group, where a divider follows.</summary>
    public static bool EndsGroup(AppCommand command) =>
        command is AppCommand.ToggleClickThrough or AppCommand.ToggleCrop or AppCommand.SaveWindowSetup or AppCommand.RestoreWindow;

    public static bool IsExperimental(AppCommand command) =>
        command is AppCommand.ToggleDark or AppCommand.ToggleGlass or AppCommand.ToggleShrink or AppCommand.ToggleCrop
            or AppCommand.ToggleRollUp;

    public static string Name(AppCommand command) => command switch
    {
        AppCommand.TogglePin => T("window.pin"),
        AppCommand.OpacityDown => T("window.opacity.decrease"),
        AppCommand.ToggleDefaultOpacity => T("window.opacity.toggleDefault"),
        AppCommand.OpacityUp => T("window.opacity.increase"),
        AppCommand.ToggleClickThrough => T("window.clickThrough"),
        AppCommand.OpenPip => T("pip.open"),
        AppCommand.ToggleAnchor => T("anchor.toggle"),
        AppCommand.ToggleDark => T("window.dark"),
        AppCommand.ToggleGlass => T("window.glass"),
        AppCommand.ToggleShrink => T("window.shrink"),
        AppCommand.ToggleCrop => T("window.crop"),
        AppCommand.SaveWindowSetup => T("setups.remember"),
        AppCommand.RestoreWindow => T("window.restore"),
        AppCommand.RestoreAll => T("window.restoreAll"),
        AppCommand.PickPipSource => T("pip.chooseSource"),
        AppCommand.Arrange => T("window.arrange"),
        AppCommand.AlignTopLeft => T("placement.topLeft"),
        AppCommand.AlignTop => T("placement.top"),
        AppCommand.AlignTopRight => T("placement.topRight"),
        AppCommand.AlignLeft => T("placement.left"),
        AppCommand.AlignCenter => T("placement.center"),
        AppCommand.AlignRight => T("placement.right"),
        AppCommand.AlignBottomLeft => T("placement.bottomLeft"),
        AppCommand.AlignBottom => T("placement.bottom"),
        AppCommand.AlignBottomRight => T("placement.bottomRight"),
        AppCommand.MoveToNextDisplay => T("placement.nextDisplay"),
        AppCommand.PlaceExactly => T("placement.exact"),
        AppCommand.RestorePlacement => T("placement.restore"),
        AppCommand.SendToBottom => T("window.sendToBottom"),
        AppCommand.ShowWindowInfo => T("window.info"),
        AppCommand.ToggleRollUp => T("window.rollUp"),
        AppCommand.ShowWindowMenu => T("window.menu"),
        AppCommand.ToggleBorderless => T("window.borderless"),
        AppCommand.ToggleBorderlessFill => T("window.fillDisplay"),
        AppCommand.ToggleDarkTitleBar => T("window.darkTitleBar"),
        AppCommand.ClickThroughOffEverywhere => T("everywhere.clickThroughOff"),
        AppCommand.UnpinEverywhere => T("everywhere.unpin"),
        AppCommand.HideAnchors => T("everywhere.anchors"),
        AppCommand.HideToolbar => T("everywhere.toolbar"),
        _ => command.ToString(),
    };
}

/// <summary>Everything a person can choose, stored as JSON in their app data folder.</summary>
public sealed class AppSettings
{
    public const int LowestOpacityPercent = 20;
    public const int HighestOpacityPercent = 95;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>The format this version writes. Raise it only together with a new step in <see cref="Migrations"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Steps that bring a saved file up to date before it is read, in order: step <c>n</c> turns format <c>n</c>
    /// into <c>n + 1</c>. Each works on the file's JSON alone. Files from before the format was numbered are 0.
    /// </summary>
    internal static readonly Action<JsonObject>[] Migrations =
    [
        OfferWindowMenuButton, // 0 → 1
    ];

    /// <summary>Settings from before the toolbar's Window menu button get it once; after that, taking it off stays off.</summary>
    private static void OfferWindowMenuButton(JsonObject root)
    {
        if (root[nameof(Toolbar)] is not JsonObject toolbar || toolbar.ContainsKey(nameof(ToolbarPreferences.WindowMenuOffered)))
            return;
        if (toolbar[nameof(ToolbarPreferences.Buttons)] is JsonArray buttons)
            buttons.Add(nameof(AppCommand.ShowWindowMenu));
        toolbar[nameof(ToolbarPreferences.WindowMenuOffered)] = true;
    }

    /// <summary>The format a file says it is in; 0 when it doesn't say, as before formats were numbered.</summary>
    private static int SchemaVersionOf(JsonObject root) =>
        root[nameof(SchemaVersion)] is JsonValue value && value.TryGetValue(out JsonElement element) &&
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int version) && version > 0 ? version : 0;

    /// <summary>
    /// Takes out hotkeys for commands this version doesn't know, such as one a newer version added, and says how
    /// many. Read as they are, one such name would make the whole file unreadable and every setting go back to its
    /// default. Hotkeys are the only place a saved choice is a key; unknown values elsewhere are already tolerated.
    /// </summary>
    private static int DropUnknownKeys(JsonObject root)
    {
        if (root[nameof(Hotkeys)] is not JsonObject hotkeys) return 0;
        string[] unknown = hotkeys.Select(pair => pair.Key)
            .Where(name => !Enum.TryParse(name, ignoreCase: true, out AppCommand command) || !Enum.IsDefined(command)).ToArray();
        foreach (string name in unknown) hotkeys.Remove(name);
        return unknown.Length;
    }

    /// <summary>
    /// The file as a JSON object to change. Built from a <see cref="JsonDocument"/> so a name given twice keeps the
    /// last value, as reading straight into the settings always did; a parsed <see cref="JsonObject"/> throws instead.
    /// </summary>
    private static JsonObject ReadObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? (JsonObject)ToNode(document.RootElement)!
            : throw new JsonException("Settings must be an object.");

        static JsonNode? ToNode(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var node = new JsonObject();
                    foreach (JsonProperty property in element.EnumerateObject()) node[property.Name] = ToNode(property.Value);
                    return node;
                case JsonValueKind.Array:
                    var array = new JsonArray();
                    foreach (JsonElement item in element.EnumerateArray()) array.Add(ToNode(item));
                    return array;
                case JsonValueKind.Null:
                    return null;
                default:
                    return JsonValue.Create(element.Clone());
            }
        }
    }

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
    /// <summary>Menus get Windows 11's blurred background while its Transparency effects is on; off keeps them plain.</summary>
    public bool MenuTransparency { get; set; } = true;
    public bool AutomaticUpdates { get; set; }
    public string Language { get; set; } = "system";

    /// <summary>How WinScreen's own windows look: "system" follows Windows, or "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    public bool PauseInFullScreen { get; set; } = true;
    public bool AllowPipInFullScreen { get; set; }
    public bool PausePinWhenMaximized { get; set; }
    public bool OpaqueOnHover { get; set; }
    public bool HighlightPinned { get; set; }
    public bool PipAutoMove { get; set; }
    /// <summary>New views start with middle-click scrolling on while they are used directly; each view's menu still changes it.</summary>
    public bool PipMiddleClickScroll { get; set; }
    public bool ExperimentalFeatures { get; set; }

    /// <summary>Hold a key and drag anywhere in a window to move or resize it. Off until someone asks for it.</summary>
    public bool ModifierDrag { get; set; }

    public ModifierKey ModifierKey { get; set; }

    /// <summary>A window moved that way snaps to a display's edges, as with a title-bar drag.</summary>
    public bool SnapWhileDragging { get; set; } = true;

    public bool WindowMenu { get; set; }
    public WindowMenuGesture WindowMenuGesture { get; set; }
    public List<string> WindowMenuExclusions { get; set; } = [];

    /// <summary>Programs that use the modifier and a drag themselves, such as drawing and 3D apps.</summary>
    public List<string> ModifierExclusions { get; set; } = ["blender", "maya", "3dsmax", "Photoshop", "krita", "gimp"];

    [JsonConverter(typeof(PipPreferencesConverter))]
    public PipPreferences Pip { get; set; } = new();
    [JsonConverter(typeof(PipPresetListConverter))]
    public List<PipPreset> PipPresets { get; set; } = [];
    [JsonConverter(typeof(PipBookmarkListConverter))]
    public List<PipSourceBookmark> PipBookmarks { get; set; } = [];
    public bool? StartWithWindows { get; set; }

    /// <summary>WinScreen runs as administrator, so it can also change administrator apps' windows.</summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>Where Settings was last, and whether it filled the screen, to open it the same way next time.</summary>
    public SettingsPlacement? SettingsWindowPlacement { get; set; }

    /// <summary>True once the first-run hint has been shown, so it doesn't return at every sign-in.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>How solid a window is after the default see-through command, and after click-through.</summary>
    public int DefaultOpacityPercent { get; set; } = 80;
    public int OpacityStepPercent { get; set; } = 10;
    public bool OpacityWheel { get; set; }

    /// <summary>Asks Windows to leave off the yellow border it draws around a window being captured.</summary>
    public bool HideCaptureBorder { get; set; } = true;
    public bool DetectAlreadyDark { get; set; } = true;
    public PinBorderPreferences PinBorder { get; set; } = new();
    public PipViewPreferences PipViews { get; set; } = new();
    public PipHoverPreferences PipHover { get; set; } = new();

    /// <summary>Programs whose views show the original on a click, for apps that don't work through a view.</summary>
    public List<string> PipOriginalPrograms { get; set; } = [];
    public ToolbarPreferences Toolbar { get; set; } = new();
    public AnchorPreferences Anchors { get; set; } = new();
    public EffectPreferences Effects { get; set; } = new();

    /// <summary>The format the file is written in (<see cref="CurrentSchemaVersion"/>); older files are brought up to date as they load.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonIgnore]
    internal bool Imported { get; set; }

    /// <summary>The default level as Windows wants it, whatever the settings file says.</summary>
    [JsonIgnore]
    public byte DefaultAlpha =>
        (byte)Math.Round(Math.Clamp(DefaultOpacityPercent, LowestOpacityPercent, HighestOpacityPercent) * 255 / 100.0);

    /// <summary>
    /// True when WinScreen may work on this program: it isn't left alone, and it's on the list of programs
    /// to work on when there is one.
    /// </summary>
    public bool Allows(string program) =>
        !ExcludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) &&
        (IncludedPrograms.Count == 0 || IncludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase));

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
            return Parse(json, () => BackupInvalid(path));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            BackupInvalid(path);
            return defaults;
        }
    }

    internal static AppSettings Parse(string json, Action? invalid = null)
    {
        var defaults = new AppSettings();
        JsonObject root = ReadObject(json);
        for (int step = SchemaVersionOf(root); step < Migrations.Length; step++)
            Migrations[step](root);
        root.Remove(nameof(SchemaVersion)); // An unreadable number mustn't cost the rest of the file.
        if (DropUnknownKeys(root) is var dropped and > 0)
            Diagnostics.Log.Event("settings.unknownDropped", ("count", dropped));
        AppSettings loaded = root.Deserialize<AppSettings>() ?? throw new JsonException("Missing settings.");
        // Written back in this version's format; a newer file is kept aside first (Save).
        loaded.SchemaVersion = CurrentSchemaVersion;
        // Existing installations keep compositor-managed PiP until the person selects capture.
        loaded.Pip = root[nameof(Pip)] is JsonObject
            ? (loaded.Pip ?? new PipPreferences()).Validated()
            : new PipPreferences { Backend = PipBackendPreference.Dwm };
        if (loaded.Hotkeys is null || loaded.ExcludedPrograms is null || loaded.IncludedPrograms is null ||
            loaded.SavedSetups is null || loaded.SavedSetups.Any(value => value is null) ||
            loaded.Hotkeys.Any(pair => pair.Value is null))
        {
            invalid?.Invoke();
        }

        loaded.PipPresets ??= [];
        loaded.PipBookmarks ??= [];
        loaded.OpacityStepPercent = Math.Clamp(loaded.OpacityStepPercent, 1, 25);
        loaded.PinBorder = (loaded.PinBorder ?? new()).Validated();
        loaded.PipViews = (loaded.PipViews ?? new()).Validated();
        loaded.PipHover = (loaded.PipHover ?? new()).Validated();
        loaded.PipOriginalPrograms = (loaded.PipOriginalPrograms ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        loaded.Toolbar = (loaded.Toolbar ?? new()).Validated();
        loaded.Anchors = (loaded.Anchors ?? new()).Validated();
        loaded.Effects = (loaded.Effects ?? new()).Validated();
        loaded.ModifierExclusions = (loaded.ModifierExclusions ?? defaults.ModifierExclusions)
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        loaded.ModifierKey = Enum.IsDefined(loaded.ModifierKey) ? loaded.ModifierKey : ModifierKey.Alt;
        loaded.WindowMenuGesture = Enum.IsDefined(loaded.WindowMenuGesture) ? loaded.WindowMenuGesture : WindowMenuGesture.CtrlRightClick;
        loaded.WindowMenuExclusions = (loaded.WindowMenuExclusions ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        loaded.Hotkeys ??= [];
        loaded.ExcludedPrograms = (loaded.ExcludedPrograms ?? defaults.ExcludedPrograms)
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        loaded.IncludedPrograms = (loaded.IncludedPrograms ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        loaded.SavedSetups = (loaded.SavedSetups ?? [])
            .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.Program)).ToList();
        loaded.Language = loaded.Language is "en" or "zh-Hans" or "zh-Hant" ? loaded.Language : "system";
        loaded.Theme = loaded.Theme is "light" or "dark" ? loaded.Theme : "system";
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

    private static void BackupInvalid(string path)
    {
        try
        {
            File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            BackupFiles.Prune(path, ".invalid-");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(failure); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        KeepNewerFile(path);
        string temporary = path + ".tmp";
        // Straight to disk, as the change journal is: a power cut mustn't leave an empty settings file.
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, this, Format);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// A file a newer WinScreen wrote is copied aside, once, before this version writes over it in its own format,
    /// so it can be put back by hand after going back to the newer version. Loading alone never changes it. Newer
    /// means a higher format, or the same format with hotkeys for commands this version doesn't know, which it
    /// would leave out when saving.
    /// </summary>
    private static void KeepNewerFile(string path)
    {
        if (!File.Exists(path)) return;
        int version, unknown;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return;
            JsonObject root = ReadObject(File.ReadAllText(path));
            version = SchemaVersionOf(root);
            unknown = DropUnknownKeys(root);
        }
        catch (JsonException)
        {
            return; // Unreadable: Load already kept a copy aside as damaged.
        }
        string kept = version > CurrentSchemaVersion ? $"{path}.from-schema-{version}"
            : unknown > 0 ? $"{path}.from-newer"
            : "";
        if (kept.Length > 0 && !File.Exists(kept))
            File.Copy(path, kept);
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
