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
    MinimizeToAnchor,
    KeepAreas,
    GlassLevels,
    ViewKeepHidden,
    ViewInteractive,
    ViewUseOriginal,
    ViewGoToOriginal,
    ViewMute,
    ViewFreeze,
    ViewRotateRight,
    ViewRotateLeft,
    ViewMirrorAcross,
    ViewMirrorDown,
    ViewUpright,
    ViewResetZoom,
    ViewMinimize,
    ViewCopyPicture,
    ViewFreezeCopy,
    ViewCopyText,
    ViewCover,
    ViewRecord,
}

public enum ModifierKey { Alt, Win }
public enum WindowMenuGesture { CtrlRightClick, CtrlShiftRightClick, CtrlMiddleClick }

// Click-through and Glass are left out on purpose: a window that opens unreachable, or with its colours removed, is
// never what someone wants.
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

    // All optional: a setup saved before has none of them, and older versions ignore these fields.

    public string? TitleContains { get; set; }
    public string? WindowClass { get; set; }
    public Windows.WindowAnchor? Place { get; set; }
    public string? Display { get; set; }
    public bool Borderless { get; set; }
    public bool Anchor { get; set; }
    public bool NoToolbar { get; set; }
    public bool ToolbarByPosition { get; set; }
    public bool EveryTime { get; set; }
    public bool Off { get; set; }

    public bool ChangesAnything => Pinned || Alpha < 255 || Dark || View is not null || Place is not null ||
                                   !string.IsNullOrEmpty(Display) || Borderless || Anchor || NoToolbar || ToolbarByPosition;

    internal bool Narrowed => !string.IsNullOrEmpty(TitleContains) || !string.IsNullOrEmpty(WindowClass);

    internal bool MatchesWindow(string title, string windowClass) =>
        (string.IsNullOrEmpty(WindowClass) || windowClass == WindowClass) &&
        (string.IsNullOrEmpty(TitleContains) || TitleMatches(title, TitleContains));

    internal static bool TitleMatches(string title, string pattern)
    {
        string[] parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        int at = 0;
        foreach (string part in parts)
        {
            int found = title.IndexOf(part, at, StringComparison.CurrentCultureIgnoreCase);
            if (found < 0) return false;
            at = found + part.Length;
        }
        return true;
    }

    internal static ProgramSetup? Choose(IEnumerable<ProgramSetup> rules, string program, string path, string title, string windowClass)
    {
        ProgramSetup[] on = rules.Where(rule => !rule.Off && rule.MatchesWindow(title, windowClass)).ToArray();
        IEnumerable<ProgramSetup> byPath = on.Where(rule => !string.IsNullOrEmpty(rule.ExecutablePath) &&
            string.Equals(rule.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        IEnumerable<ProgramSetup> byName = on.Where(rule => string.IsNullOrEmpty(rule.ExecutablePath) &&
            string.Equals(rule.Program, program, StringComparison.OrdinalIgnoreCase));
        return byPath.OrderByDescending(rule => rule.Narrowed).FirstOrDefault()
               ?? byName.OrderByDescending(rule => rule.Narrowed).FirstOrDefault();
    }

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
        if (Place is not null || !string.IsNullOrEmpty(Display))
            parts.Add(T("setups.placed"));
        if (Borderless)
            parts.Add(T("setups.borderless"));
        if (Anchor)
            parts.Add(T("setups.anchor"));
        if (NoToolbar)
            parts.Add(T("setups.noToolbar"));
        if (ToolbarByPosition)
            parts.Add(T("setups.toolbarByPosition"));
        if (EveryTime)
            parts.Add(T("setups.everyTime"));
        if (Off)
            parts.Add(T("setups.off"));
        if (!string.IsNullOrEmpty(TitleContains))
            parts.Insert(0, string.Format(T("setups.forTitle"), TitleContains));
        string location = string.IsNullOrWhiteSpace(ExecutablePath) ? "" : $" ({ExecutablePath})";
        return $"{Program}: {string.Join(", ", parts)}{location}";
    }
}

public sealed record SettingsPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public Rectangle Bounds => new(X, Y, Width, Height);

    internal SettingsPlacement? OnScreen(IEnumerable<Rectangle> workingAreas)
    {
        if (Width < 200 || Height < 150) return null;
        var title = new Rectangle(X + 40, Y, Math.Max(1, Width - 80), 30);
        return workingAreas.Any(area => area.IntersectsWith(title)) ? this : null;
    }
}

public sealed record HotkeyProblem(AppCommand Command, string Message);

public static class CommandCatalog
{
    public static readonly AppCommand[] Ordered =
    [
        AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.ToggleDefaultOpacity, AppCommand.OpacityUp,
        AppCommand.ToggleClickThrough, AppCommand.OpenPip, AppCommand.ToggleAnchor, AppCommand.ToggleDark,
        AppCommand.ToggleGlass, AppCommand.ToggleShrink, AppCommand.ToggleCrop, AppCommand.Arrange, AppCommand.SaveWindowSetup,
        AppCommand.RestoreWindow, AppCommand.RestoreAll,
    ];

    // Every button the toolbar can have, in its order. All start on.
    public static readonly AppCommand[] ToolbarButtons =
    [
        AppCommand.TogglePin, AppCommand.ToggleDefaultOpacity, AppCommand.ToggleClickThrough,
        AppCommand.OpenPip, AppCommand.ToggleAnchor, AppCommand.MinimizeToAnchor, AppCommand.ToggleDark,
        AppCommand.ToggleGlass, AppCommand.GlassLevels, AppCommand.KeepAreas, AppCommand.ToggleShrink, AppCommand.ToggleCrop,
        AppCommand.ToggleDarkTitleBar, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.Arrange,
        AppCommand.SaveWindowSetup, AppCommand.RestoreWindow, AppCommand.ShowWindowMenu,
    ];

    // On the bar, default opacity's button opens the slider with default opacity under it.
    public static string ButtonName(AppCommand command) =>
        command == AppCommand.ToggleDefaultOpacity ? T("pip.opacity") : Name(command);

    public static bool OpensMenuOnToolbar(AppCommand command) =>
        command is AppCommand.ToggleDefaultOpacity or AppCommand.Arrange or AppCommand.ShowWindowMenu;

    // Every button there was before format 4; a saved bar is compared with it, so it stays as it was.
    private static readonly AppCommand[] FirstToolbarButtons =
    [
        AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.ToggleDefaultOpacity, AppCommand.OpacityUp,
        AppCommand.ToggleClickThrough, AppCommand.OpenPip, AppCommand.ToggleAnchor, AppCommand.ToggleDark, AppCommand.ToggleGlass,
        AppCommand.ToggleShrink, AppCommand.ToggleCrop, AppCommand.Arrange, AppCommand.SaveWindowSetup, AppCommand.RestoreWindow,
        AppCommand.ShowWindowMenu,
    ];

    internal static bool HadEveryFirstButton(IReadOnlySet<AppCommand> bar) => FirstToolbarButtons.All(bar.Contains);

    // Buttons since format 4; a bar that had every button before gets them once.
    internal static readonly AppCommand[] AddedToolbarButtons =
    [
        AppCommand.MinimizeToAnchor, AppCommand.GlassLevels, AppCommand.KeepAreas, AppCommand.ToggleDarkTitleBar,
        AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill,
    ];

    // Before Extra, which includes them. Each acts on the view in front, or on the view of the window in front.
    public static readonly AppCommand[] View =
    [
        AppCommand.ViewKeepHidden, AppCommand.ViewInteractive, AppCommand.ViewUseOriginal, AppCommand.ViewGoToOriginal,
        AppCommand.ViewMute, AppCommand.ViewFreeze, AppCommand.ViewRotateRight, AppCommand.ViewRotateLeft,
        AppCommand.ViewMirrorAcross, AppCommand.ViewMirrorDown, AppCommand.ViewUpright, AppCommand.ViewResetZoom,
        AppCommand.ViewMinimize, AppCommand.ViewCopyPicture, AppCommand.ViewFreezeCopy, AppCommand.ViewCopyText, AppCommand.ViewCover,
        AppCommand.ViewRecord,
    ];

    public static bool ForView(AppCommand command) => View.Contains(command);

    public static readonly AppCommand[] Extra =
    [
        AppCommand.PickPipSource, AppCommand.AlignTopLeft, AppCommand.AlignTop, AppCommand.AlignTopRight,
        AppCommand.AlignLeft, AppCommand.AlignCenter, AppCommand.AlignRight, AppCommand.AlignBottomLeft,
        AppCommand.AlignBottom, AppCommand.AlignBottomRight, AppCommand.MoveToNextDisplay, AppCommand.PlaceExactly,
        AppCommand.RestorePlacement, AppCommand.SendToBottom, AppCommand.ShowWindowInfo, AppCommand.ToggleRollUp,
        AppCommand.ShowWindowMenu, AppCommand.ToggleBorderless, AppCommand.ToggleBorderlessFill, AppCommand.ToggleDarkTitleBar,
        AppCommand.MinimizeToAnchor, AppCommand.KeepAreas, AppCommand.GlassLevels, .. View,
    ];

    public static readonly AppCommand[] Everywhere =
    [
        AppCommand.ClickThroughOffEverywhere, AppCommand.UnpinEverywhere, AppCommand.HideAnchors, AppCommand.HideToolbar,
    ];

    public static bool ForAllWindows(AppCommand command) =>
        command is AppCommand.RestoreAll or AppCommand.PickPipSource || Everywhere.Contains(command);

    public static readonly AppCommand?[] ArrangeMenu =
    [
        AppCommand.AlignTopLeft, AppCommand.AlignTop, AppCommand.AlignTopRight, AppCommand.AlignLeft,
        AppCommand.AlignCenter, AppCommand.AlignRight, AppCommand.AlignBottomLeft, AppCommand.AlignBottom,
        AppCommand.AlignBottomRight, null, AppCommand.MoveToNextDisplay, AppCommand.PlaceExactly,
        AppCommand.RestorePlacement, null, AppCommand.ToggleRollUp, AppCommand.SendToBottom, null, AppCommand.ShowWindowInfo,
    ];

    public static Windows.WindowAnchor? AnchorOf(AppCommand command) =>
        command is >= AppCommand.AlignTopLeft and <= AppCommand.AlignBottomRight
            ? (Windows.WindowAnchor)(command - AppCommand.AlignTopLeft)
            : null;

    public static IEnumerable<AppCommand> All => Ordered.Concat(Extra).Concat(Everywhere);

    public static bool EndsGroup(AppCommand command) =>
        command is AppCommand.ToggleClickThrough or AppCommand.ToggleCrop or AppCommand.SaveWindowSetup or AppCommand.RestoreWindow;

    // None since Dark mode left it in 1.5.0; a command put here is tagged and needs the switch in Settings.
    private static readonly HashSet<AppCommand> Experimental = [];

    public static bool IsExperimental(AppCommand command) => Experimental.Contains(command);

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
        AppCommand.MinimizeToAnchor => T("anchor.minimize"),
        AppCommand.KeepAreas => T("window.keepAreas"),
        AppCommand.GlassLevels => T("window.glassLevels"),
        AppCommand.ViewKeepHidden => T("view.command.keepHidden"),
        AppCommand.ViewInteractive => T("view.command.interactive"),
        AppCommand.ViewUseOriginal => T("view.command.useOriginal"),
        AppCommand.ViewGoToOriginal => T("view.command.goToOriginal"),
        AppCommand.ViewMute => T("menu.original.mute"),
        AppCommand.ViewFreeze => T("view.command.freeze"),
        AppCommand.ViewRotateRight => T("view.command.rotateRight"),
        AppCommand.ViewRotateLeft => T("view.command.rotateLeft"),
        AppCommand.ViewMirrorAcross => T("view.command.mirrorAcross"),
        AppCommand.ViewMirrorDown => T("view.command.mirrorDown"),
        AppCommand.ViewUpright => T("view.command.upright"),
        AppCommand.ViewResetZoom => T("view.command.resetZoom"),
        AppCommand.ViewMinimize => T("view.command.minimize"),
        AppCommand.ViewCopyPicture => T("view.command.copyPicture"),
        AppCommand.ViewFreezeCopy => T("view.command.freezeCopy"),
        AppCommand.ViewCopyText => T("view.command.copyText"),
        AppCommand.ViewCover => T("view.command.cover"),
        AppCommand.ViewRecord => T("view.command.record"),
        _ => command.ToString(),
    };
}

public enum UpdateMode { Off = 0, TellMe = 1, Install = 2 }

public sealed class AppSettings
{
    public const int LowestOpacityPercent = 20;
    public const int HighestOpacityPercent = 95;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    // Raise it only together with a new step in Migrations.
    public const int CurrentSchemaVersion = 4;

    // Step n turns format n into n + 1, on the file's JSON alone. Files from before the format was numbered are 0.
    internal static readonly Action<JsonObject>[] Migrations =
    [
        OfferWindowMenuButton, // 0 → 1
        ChooseUpdates, // 1 → 2
        DropFirstSuggestions, // 2 → 3
        AddToolbarButtons, // 3 → 4
    ];

    // The first versions filled both lists with sample programs nobody chose.
    private static readonly string[] FirstExcluded = ["csgo", "cs2", "valorant", "eldenring"];
    private static readonly string[] FirstGestureExclusions = ["blender", "maya", "3dsmax", "Photoshop", "krita", "gimp"];

    private static void DropFirstSuggestions(JsonObject root)
    {
        Drop(nameof(ExcludedPrograms), FirstExcluded);
        Drop(nameof(ModifierExclusions), FirstGestureExclusions);

        void Drop(string list, string[] names)
        {
            if (root[list] is not JsonArray programs) return;
            foreach (JsonNode? program in programs.ToArray())
                if (program is JsonValue value && value.TryGetValue(out string? name) &&
                    names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    programs.Remove(program);
        }
    }

    // Only a bar nobody took a button off: someone who chose a short one keeps it.
    private static void AddToolbarButtons(JsonObject root)
    {
        if (root[nameof(Toolbar)] is not JsonObject toolbar || toolbar[nameof(ToolbarPreferences.Buttons)] is not JsonArray buttons) return;
        var shown = new HashSet<AppCommand>();
        foreach (JsonNode? button in buttons)
        {
            if (button is not JsonValue value) continue;
            if (value.TryGetValue(out string? name) && Enum.TryParse(name, true, out AppCommand named)) shown.Add(named);
            else if (value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String &&
                     Enum.TryParse(element.GetString(), true, out AppCommand read)) shown.Add(read);
            else if (value.TryGetValue(out JsonElement number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int known))
                shown.Add((AppCommand)known);
        }
        if (!CommandCatalog.HadEveryFirstButton(shown)) return;
        foreach (AppCommand added in CommandCatalog.AddedToolbarButtons.Where(added => !shown.Contains(added))) buttons.Add(added.ToString());
    }

    private static void ChooseUpdates(JsonObject root)
    {
        if (root.ContainsKey(nameof(Updates))) return;
        bool automatic = root[nameof(AutomaticUpdates)] is JsonValue value && value.TryGetValue(out bool on) && on;
        root[nameof(Updates)] = (int)(automatic ? UpdateMode.Install : UpdateMode.Off);
    }

    // Once: after that, taking it off stays off.
    private static void OfferWindowMenuButton(JsonObject root)
    {
        if (root[nameof(Toolbar)] is not JsonObject toolbar || toolbar.ContainsKey(nameof(ToolbarPreferences.WindowMenuOffered)))
            return;
        if (toolbar[nameof(ToolbarPreferences.Buttons)] is JsonArray buttons)
            buttons.Add(nameof(AppCommand.ShowWindowMenu));
        toolbar[nameof(ToolbarPreferences.WindowMenuOffered)] = true;
    }

    private static int SchemaVersionOf(JsonObject root) =>
        root[nameof(SchemaVersion)] is JsonValue value && value.TryGetValue(out JsonElement element) &&
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int version) && version > 0 ? version : 0;

    // Read as they are, one unknown command name would make the whole file unreadable and every setting go back to its
    // default. Hotkeys are the only place a saved choice is a key.
    private static int DropUnknownKeys(JsonObject root)
    {
        if (root[nameof(Hotkeys)] is not JsonObject hotkeys) return 0;
        // Names only: a number or a list of names ("A, B") would parse, as some other command.
        string[] unknown = hotkeys.Select(pair => pair.Key)
            .Where(name => !Enum.GetNames<AppCommand>().Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (string name in unknown) hotkeys.Remove(name);
        return unknown.Length;
    }

    // Through a JsonDocument so a name given twice keeps the last value, as reading straight into the settings does; a
    // parsed JsonObject throws instead.
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

    public List<string> ExcludedPrograms { get; set; } = [];

    public List<string> IncludedPrograms { get; set; } = [];

    public List<ProgramSetup> SavedSetups { get; set; } = [];

    public bool ShowToolbar { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool MenuTransparency { get; set; } = true;
    public UpdateMode Updates { get; set; }

    public string? LastSeenVersion { get; set; }

    // Written for copies from before the three update choices, which know only this switch; read only from an older
    // file (ChooseUpdates).
    public bool AutomaticUpdates => Updates == UpdateMode.Install;
    public string Language { get; set; } = "system";

    public string Theme { get; set; } = "system";
    public bool PauseInFullScreen { get; set; } = true;
    public bool AllowPipInFullScreen { get; set; }
    public bool PausePinWhenMaximized { get; set; }
    public bool OpaqueOnHover { get; set; }
    public bool HighlightPinned { get; set; }
    public bool PipAutoMove { get; set; }
    public bool PipMiddleClickScroll { get; set; }
    public bool ExperimentalFeatures { get; set; }

    public bool ModifierDrag { get; set; }

    public ModifierKey ModifierKey { get; set; }

    public bool SnapWhileDragging { get; set; } = true;

    public bool WindowMenu { get; set; }
    public WindowMenuGesture WindowMenuGesture { get; set; }
    public List<string> WindowMenuExclusions { get; set; } = [];

    public List<string> ModifierExclusions { get; set; } = [];

    [JsonConverter(typeof(PipPreferencesConverter))]
    public PipPreferences Pip { get; set; } = new();
    [JsonConverter(typeof(PipPresetListConverter))]
    public List<PipPreset> PipPresets { get; set; } = [];
    [JsonConverter(typeof(PipBookmarkListConverter))]
    public List<PipSourceBookmark> PipBookmarks { get; set; } = [];
    public bool? StartWithWindows { get; set; }

    public bool RunAsAdministrator { get; set; }

    // JSON only, no Settings control: false keeps the one elevated copy instead of the helper.
    public bool AdministratorHelper { get; set; } = true;

    public SettingsPlacement? SettingsWindowPlacement { get; set; }

    public bool WelcomeShown { get; set; }

    public int DefaultOpacityPercent { get; set; } = 80;
    public bool OpacityWheel { get; set; }

    public bool HideCaptureBorder { get; set; } = true;
    public bool DetectAlreadyDark { get; set; } = true;
    public PinBorderPreferences PinBorder { get; set; } = new();
    public PipViewPreferences PipViews { get; set; } = new();
    public PipHoverPreferences PipHover { get; set; } = new();

    public List<string> PipOriginalPrograms { get; set; } = [];
    public ToolbarPreferences Toolbar { get; set; } = new();
    public AnchorPreferences Anchors { get; set; } = new();
    public EffectPreferences Effects { get; set; } = new();
    public BackgroundPreferences Backgrounds { get; set; } = new();
    public KeptAreaPreferences KeptAreas { get; set; } = new();

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonIgnore]
    internal bool Imported { get; set; }

    [JsonIgnore]
    public byte DefaultAlpha =>
        (byte)Math.Round(Math.Clamp(DefaultOpacityPercent, LowestOpacityPercent, HighestOpacityPercent) * 255 / 100.0);

    public bool Allows(string program) =>
        !ExcludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) &&
        (IncludedPrograms.Count == 0 || IncludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase));

    public ProgramSetup? SetupFor(string program)
    {
        return SavedSetups.Find(setup => string.Equals(setup.Program, program, StringComparison.OrdinalIgnoreCase));
    }

    public ProgramSetup? SetupForWindow(nint window) =>
        SavedSetups.Count == 0 ? null : ProgramSetup.Choose(SavedSetups, Windows.WindowInfo.ProgramOf(window),
            Windows.WindowInfo.ExecutablePath(window), Windows.WindowInfo.Title(window), Windows.WindowInfo.ClassName(window));

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public void CopyFrom(AppSettings settings)
    {
        var copy = settings.Clone();
        foreach (var property in typeof(AppSettings).GetProperties().Where(property => property.CanWrite))
            property.SetValue(this, property.GetValue(copy));
    }

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
        loaded.PinBorder = (loaded.PinBorder ?? new()).Validated();
        loaded.PipViews = (loaded.PipViews ?? new()).Validated();
        loaded.PipHover = (loaded.PipHover ?? new()).Validated();
        loaded.PipOriginalPrograms = (loaded.PipOriginalPrograms ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        loaded.Toolbar = (loaded.Toolbar ?? new()).Validated();
        loaded.Anchors = (loaded.Anchors ?? new()).Validated();
        loaded.Effects = (loaded.Effects ?? new()).Validated();
        loaded.Backgrounds = (loaded.Backgrounds ?? new()).Validated();
        loaded.KeptAreas = (loaded.KeptAreas ?? new()).Validated();
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
        // A place a newer version added is no place here: the rest of the rule still applies.
        foreach (ProgramSetup setup in loaded.SavedSetups)
            if (setup.Place is { } place && !Enum.IsDefined(place)) setup.Place = null;
        loaded.Language = loaded.Language is "en" or "zh-Hans" or "zh-Hant" ? loaded.Language : "system";
        loaded.Theme = loaded.Theme is "light" or "dark" ? loaded.Theme : "system";
        loaded.Updates = Enum.IsDefined(loaded.Updates) ? loaded.Updates : UpdateMode.Off;
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
        Dictionary<AppCommand, string> hotkeys = Hotkeys;
        Hotkeys = WrittenHotkeys(hotkeys);
        try
        {
            using var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough);
            JsonSerializer.Serialize(file, this, Format);
            file.Flush(flushToDisk: true);
        }
        finally { Hotkeys = hotkeys; }
        File.Move(temporary, path, overwrite: true);
    }

    // A command without a key or a default is left out (a missing entry reads as none), so an older version, which
    // treats an unknown command as damage up to 1.0.0, is spared it until it is given a key.
    internal static Dictionary<AppCommand, string> WrittenHotkeys(Dictionary<AppCommand, string> hotkeys)
    {
        Dictionary<AppCommand, string> defaults = new AppSettings().Hotkeys;
        return hotkeys.Where(pair => !string.IsNullOrEmpty(pair.Value) || defaults.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    // Copied aside once before this version writes over it, so it can be put back by hand. Newer means a higher format,
    // or hotkeys for commands this version doesn't know and would leave out.
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

public static class HotkeyText
{
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;

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

    // WinForms doesn't report the Windows key as a modifier.
    internal static bool WinKeyDown =>
        (User32.GetKeyState(0x5B) & 0x8000) != 0 || (User32.GetKeyState(0x5C) & 0x8000) != 0;

    public static bool IsFunctionKey(Keys key) => key is >= Keys.F1 and <= Keys.F24;

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
