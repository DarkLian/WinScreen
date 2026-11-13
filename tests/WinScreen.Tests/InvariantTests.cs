using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinScreen.Settings;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Rules that keep windows from being stranded and settings from being misread, checked against the product's own
/// sources and types (roadmap P0-2). Logic only: no window opens. Each rule also runs once on a deliberately broken
/// input, so a check that can't fail can't hide here.
/// </summary>
public class InvariantTests
{
    // ── Product sources, as built with this test assembly ──────────────────────────────────────────────────────────

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Sources = new(() =>
    {
        Assembly assembly = typeof(InvariantTests).Assembly;
        const string prefix = "LocalizationSource.";
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            sources[name[prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }
        return sources;
    });

    private static string Source(string path) =>
        Sources.Value.TryGetValue(path, out string? code) ? code : throw new InvalidOperationException($"No source {path}.");

    private static readonly Regex Comments = new("""//[^\n]*|/\*.*?\*/""", RegexOptions.Singleline);
    private static readonly Regex Strings = new("""@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)'""");

    private static string WithoutComments(string code) => Comments.Replace(code, "");

    /// <summary>The body of the first method whose declaration contains <paramref name="signature"/>, braces included.</summary>
    internal static string MethodBody(string code, string signature)
    {
        // Blank out strings and comments for counting, keeping positions, so a brace in text can't end the body early.
        string blank = Comments.Replace(Strings.Replace(code, m => new string(' ', m.Length)), m => new string(' ', m.Length));
        int start = blank.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"No method '{signature}'.");
        int open = blank.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < blank.Length; i++)
        {
            if (blank[i] == '{') depth++;
            else if (blank[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"Method '{signature}' never ends.");
    }

    // ── 1. Journal symmetry ────────────────────────────────────────────────────────────────────────────────────────

    private const string Controller = "Windows/WindowController.cs";

    /// <summary>
    /// What is wrong with the journal: a changed-window member that is neither marked as left out nor both written
    /// and read back, or a record member that recovery never reads.
    /// </summary>
    internal static List<string> JournalProblems(IEnumerable<(string Name, string? NotJournalled)> stateMembers,
        IEnumerable<string> recordMembers, string toRecord, string recover)
    {
        var problems = new List<string>();
        foreach ((string name, string? reason) in stateMembers)
        {
            bool written = Regex.IsMatch(toRecord, $@"\bstate\.{name}\b");
            bool read = Regex.IsMatch(recover, $@"\b{name}\s*=(?!=)");
            if (reason is not null)
            {
                // Recovery may still give it a starting value of its own; only writing it contradicts the mark.
                if (string.IsNullOrWhiteSpace(reason)) problems.Add($"{name}: [NotJournalled] needs a reason.");
                if (written) problems.Add($"{name}: marked [NotJournalled] but is journalled.");
            }
            else if (!written) problems.Add($"{name}: not written by ToRecord and not marked [NotJournalled(\"why\")].");
            else if (!read) problems.Add($"{name}: written by ToRecord but never read back when recovering.");
        }
        foreach (string name in recordMembers)
            if (!Regex.IsMatch(recover, $@"\brecord\.{name}\b"))
                problems.Add($"WindowRecord.{name}: saved but never read when recovering.");
        return problems;
    }

    private static IEnumerable<(string, string?)> ChangedWindowMembers()
    {
        Type state = typeof(WindowController).GetNestedType("ChangedWindow", BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("WindowController.ChangedWindow has moved.");
        return state.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(p => (p.Name, p.GetCustomAttribute<NotJournalledAttribute>() is { } a ? a.Reason : (string?)null));
    }

    private static IEnumerable<string> WindowRecordMembers() =>
        typeof(WindowRecord).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.Name != "EqualityContract").Select(p => p.Name);

    [Fact]
    public void Every_change_to_a_window_is_written_to_the_journal_and_read_back_after_a_crash_or_says_why_not()
    {
        string code = WithoutComments(Source(Controller));
        List<string> problems = JournalProblems(ChangedWindowMembers(), WindowRecordMembers(),
            MethodBody(code, "WindowRecord ToRecord("), MethodBody(code, "void RecoverJournalUnderLock("));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_journal_check_fails_for_a_change_that_is_saved_but_not_recovered()
    {
        (string, string?)[] members = [("Pinned", null), ("Cropped", null), ("Busy", "Transient.")];
        const string toRecord = "{ new WindowRecord(...) { Pinned = state.Pinned, Cropped = state.Cropped } }";
        const string recover = "{ new ChangedWindow { Cropped = record.Cropped, Pinned = record.Pinned } }";
        Assert.Empty(JournalProblems(members, ["Cropped", "Pinned"], toRecord, recover));

        Assert.Contains(JournalProblems(members, ["Cropped", "Pinned"], toRecord, recover.Replace("Pinned = record.Pinned", "")),
            p => p.StartsWith("Pinned: written by ToRecord but never read back", StringComparison.Ordinal));
        Assert.Contains(JournalProblems([.. members, ("Rolled", null)], ["Cropped", "Pinned"], toRecord, recover),
            p => p.StartsWith("Rolled: not written", StringComparison.Ordinal));
        Assert.Contains(JournalProblems(members, ["Cropped", "Pinned", "Origin"], toRecord, recover),
            p => p.StartsWith("WindowRecord.Origin", StringComparison.Ordinal));
        Assert.Contains(JournalProblems([("Cropped", "Says it isn't.")], [], toRecord, recover),
            p => p.Contains("marked [NotJournalled] but is journalled", StringComparison.Ordinal));
    }

    // ── 2. Frozen persisted enums ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every enum that reaches settings.json or the change journal, with its names and numbers. Saved files hold
    /// these, as numbers in values and as names in dictionary keys, so a rename or renumbering misreads a file a
    /// person already has. Adding a member at the end means adding it here; nothing else may change.
    /// </summary>
    private static readonly Dictionary<string, string> FrozenEnums = new()
    {
        ["WinScreen.Settings.AppCommand"] =
            "TogglePin=1 OpacityUp=2 OpacityDown=3 ToggleClickThrough=4 OpenPip=5 RestoreAll=6 ToggleDark=7 " +
            "ToggleGlass=8 ToggleAnchor=9 ToggleDefaultOpacity=10 SaveWindowSetup=11 ToggleShrink=12 ToggleCrop=13 " +
            "RestoreWindow=14 PickPipSource=15 Arrange=16 AlignTopLeft=17 AlignTop=18 AlignTopRight=19 AlignLeft=20 " +
            "AlignCenter=21 AlignRight=22 AlignBottomLeft=23 AlignBottom=24 AlignBottomRight=25 MoveToNextDisplay=26 " +
            "PlaceExactly=27 RestorePlacement=28 SendToBottom=29 ShowWindowInfo=30 ToggleRollUp=31 ShowWindowMenu=32 " +
            "ToggleBorderless=33 ToggleBorderlessFill=34 ToggleDarkTitleBar=35 ClickThroughOffEverywhere=36 " +
            "UnpinEverywhere=37 HideAnchors=38 HideToolbar=39",
        ["WinScreen.Settings.AnchorLook"] = "Dark=0 Light=1 NoBackground=2",
        ["WinScreen.Settings.AnchorSide"] = "Right=0 Left=1 NearWindow=2",
        ["WinScreen.Settings.ModifierKey"] = "Alt=0 Win=1",
        ["WinScreen.Settings.PipBackendPreference"] = "Auto=0 Dwm=1 Wgc=2",
        ["WinScreen.Settings.PipHoverPreview"] = "Off=0 WholeView=1 Lens=2",
        ["WinScreen.Settings.PipScaling"] = "Auto=0 Crisp=1 Smooth=2",
        ["WinScreen.Settings.ToolbarPlacement"] = "Left=0 Centre=1 Right=2",
        ["WinScreen.Settings.ToolbarTrigger"] = "TitleBar=0 MinimizeButton=1",
        ["WinScreen.Settings.WindowMenuGesture"] = "CtrlRightClick=0 CtrlShiftRightClick=1 CtrlMiddleClick=2",
    };

    internal static string Describe(Type enumType) => string.Join(' ',
        Enum.GetNames(enumType).Select(name => $"{name}={Convert.ToInt64(Enum.Parse(enumType, name))}"));

    /// <summary>The enums a serializer meets walking from <paramref name="roots"/> through public, saved properties.</summary>
    internal static SortedSet<Type> PersistedEnums(params Type[] roots)
    {
        HashSet<Assembly> walked = roots.Select(root => root.Assembly).ToHashSet();
        var found = new SortedSet<Type>(Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName)));
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(roots);
        while (pending.TryPop(out Type? type))
        {
            if (!seen.Add(type)) continue;
            if (type.IsEnum) { found.Add(type); continue; }
            if (type.IsArray) { pending.Push(type.GetElementType()!); continue; }
            if (type.IsGenericType)
                foreach (Type argument in type.GetGenericArguments()) pending.Push(argument);
            if (!walked.Contains(type.Assembly)) continue;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0) continue;
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                pending.Push(property.PropertyType);
            }
        }
        return found;
    }

    internal static List<string> EnumProblems(IReadOnlyDictionary<string, string> frozen, IEnumerable<Type> persisted)
    {
        var problems = new List<string>();
        foreach (Type type in persisted)
        {
            string actual = Describe(type);
            if (!frozen.TryGetValue(type.FullName!, out string? expected))
                problems.Add($"{type.FullName} reaches a saved file but isn't frozen. Add: [\"{type.FullName}\"] = \"{actual}\"");
            else if (!actual.StartsWith(expected, StringComparison.Ordinal) ||
                     (actual.Length > expected.Length && actual[expected.Length] != ' '))
                problems.Add($"{type.FullName} changed. Frozen: {expected}{Environment.NewLine}  Now: {actual}");
            else if (actual != expected)
                problems.Add($"{type.FullName} gained members at the end; add them to the table: {actual}");
        }
        foreach (string name in frozen.Keys.Except(persisted.Select(t => t.FullName!)))
            problems.Add($"{name} is frozen but no longer reaches a saved file; remove it only if no published version saved it.");
        return problems;
    }

    [Fact]
    public void Enums_in_saved_files_keep_their_names_and_numbers()
    {
        List<string> problems = EnumProblems(FrozenEnums, PersistedEnums(typeof(AppSettings), typeof(WindowRecord)));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private enum Before { First = 1, Second, Third }
    private enum Renumbered { First = 1, Third, Second }
    private enum Renamed { First = 1, Second, Thrid }
    private enum Extended { First = 1, Second, Third, Fourth }
    private sealed class Saved { public Dictionary<Before, string> Keys { get; set; } = []; public Before? Value { get; set; } }

    [Fact]
    public void The_enum_check_fails_for_a_renumbered_or_renamed_member_and_finds_enums_inside_collections()
    {
        Assert.Contains(typeof(Before), PersistedEnums(typeof(Saved)));
        string frozen = Describe(typeof(Before));
        Dictionary<string, string> Table(Type type) => new() { [type.FullName!] = frozen };

        Assert.Empty(EnumProblems(Table(typeof(Before)), [typeof(Before)]));
        Assert.Contains(EnumProblems(Table(typeof(Renumbered)), [typeof(Renumbered)]), p => p.Contains("changed", StringComparison.Ordinal));
        Assert.Contains(EnumProblems(Table(typeof(Renamed)), [typeof(Renamed)]), p => p.Contains("changed", StringComparison.Ordinal));
        Assert.Contains(EnumProblems(Table(typeof(Extended)), [typeof(Extended)]), p => p.Contains("gained", StringComparison.Ordinal));
        Assert.Contains(EnumProblems(new Dictionary<string, string>(), [typeof(Before)]), p => p.Contains("isn't frozen", StringComparison.Ordinal));
    }

    // ── 5. Network boundary ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Network types by name, not only by namespace: System.Net.Http is an implicit global using in the product, so
    /// a bare HttpRequestMessage needs no using line. Windows' own networking (Windows.Networking, Windows.Web) is
    /// reachable through the Windows target too. Case matters, so text such as "https://" doesn't count.
    /// </summary>
    private static readonly Regex NetworkCode = new(
        @"\bSystem\.Net\b|\bWindows\.(?:Networking|Web)\b|\b(?:Http(?:Client|Content|Method|Headers)\w*|" +
        @"Http(?:Message|Request|Response)\w*|\w*HttpHandler|(?:String|ByteArray|Stream|Json|FormUrlEncoded|Multipart\w*)Content|" +
        @"WebRequest|WebClient|TcpClient|TcpListener|UdpClient|NetworkStream|\w*Socket\w*|Dns|HttpBaseProtocolFilter|" +
        @"UpdateManager|GithubSource|SimpleWebSource)\b|""(?i:wininet|winhttp|ws2_32|urlmon|websocket)(?i:\.dll)?""");

    /// <summary>
    /// Velopack's start-up hook runs install and uninstall steps and never connects; it has to be the first thing
    /// Main does, so Program.cs may name it. Every other use of the update library lives in Updates/.
    /// </summary>
    private static readonly Regex AllowedOutsideUpdates = new("""^using Velopack;\r?$|\bVelopackApp\b""", RegexOptions.Multiline);

    internal static List<string> NetworkProblems(IEnumerable<KeyValuePair<string, string>> sources)
    {
        var problems = new List<string>();
        foreach ((string path, string raw) in sources)
        {
            if (path.StartsWith("Updates/", StringComparison.Ordinal)) continue;
            string code = WithoutComments(raw);
            foreach (Match match in NetworkCode.Matches(code))
                problems.Add($"{path}: '{match.Value}' outside Updates/.");
            string rest = AllowedOutsideUpdates.Replace(code, "");
            if (Regex.IsMatch(rest, @"\busing\s+Velopack\b|\bVelopack\.") || (path != "Program.cs" && code != rest))
                problems.Add($"{path}: uses the update library outside Updates/.");
        }
        return problems;
    }

    [Fact]
    public void Network_code_exists_only_in_the_updater()
    {
        List<string> problems = NetworkProblems(Sources.Value);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_network_check_fails_for_a_connection_made_outside_the_updater()
    {
        KeyValuePair<string, string> Entry(string path, string code) => new(path, code);
        Assert.Empty(NetworkProblems([Entry("Updates/Feed.cs", "using System.Net.Http; new HttpClient();"),
            Entry("Program.cs", "using Velopack;\r\nVelopackApp.Build().Run();"), Entry("Pip/View.cs", "// HttpClient in a comment")]));

        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "using System.Net.Http;")]));
        Assert.NotEmpty(NetworkProblems([Entry("Tray/Ping.cs", "var c = new HttpClient();")]));
        Assert.NotEmpty(NetworkProblems([Entry("Native/Net.cs", "[LibraryImport(\"winhttp.dll\")] static partial nint Open();")]));
        Assert.NotEmpty(NetworkProblems([Entry("Tray/Check.cs", "using Velopack;\nnew UpdateManager(source);")]));
        Assert.NotEmpty(NetworkProblems([Entry("Tray/Check.cs", "VelopackApp.Build().Run();")]));
        // Found by the implicit using alone, with no namespace in the file.
        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "using var send = new HttpMessageInvoker(new SocketsHttpHandler());")]));
        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "var request = new HttpRequestMessage(HttpMethod.Get, uri);")]));
        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "var body = new StringContent(text);")]));
        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "var s = new Windows.Networking.Sockets.StreamSocket();")]));
        Assert.NotEmpty(NetworkProblems([Entry("Pip/View.cs", "var s = new StreamSocket();")]));
        Assert.NotEmpty(NetworkProblems([Entry("Native/Net.cs", "[DllImport(\"WS2_32.DLL\")] static extern int send();")]));
        // Text that only looks like it.
        Assert.Empty(NetworkProblems([Entry("Tray/About.cs", "string site = \"https://example.org\"; var socketless = 1; var content = Contents();")]));
    }

    // ── 6. Ratchets: global state and scattered P/Invoke may shrink, never grow ───────────────────────────────────

    /// <summary>
    /// Settable static properties: state any code can change from anywhere, which is how a test or a second view
    /// changes behaviour it shouldn't. Pass the value in instead. Taken on 13 Nov 2026.
    /// </summary>
    private static readonly string[] SettableStatics =
    [
        "WinScreen.Native.Translucency.Allowed",
        "WinScreen.Pip.CaptureBorder.Ask",
        "WinScreen.Pip.CaptureBorder.Supported",
        "WinScreen.Pip.CaptureBorder.Wanted",
        "WinScreen.Pip.PipHoverApps.Preferences",
        "WinScreen.Pip.PipInteraction.DiagnoseInput",
        "WinScreen.Pip.PipInteraction.ShareInput",
        "WinScreen.Pip.PipOriginalApps.Programs",
        "WinScreen.Settings.FocusVisuals.Keyboard",
        "WinScreen.Settings.ProgramIcons.File",
        "WinScreen.Windows.TopLayer.Above",
        "WinScreen.Windows.TopLayer.IsSystemSurface",
        "WinScreen.Windows.TopLayer.IsTip",
        "WinScreen.Windows.WindowEvents.Active",
    ];

    /// <summary>
    /// [DllImport]/[LibraryImport] declarations outside Native/, per file. New ones go in Native/, where DPI,
    /// marshalling and error handling are kept in one place. Taken on 13 Nov 2026.
    /// </summary>
    private static readonly Dictionary<string, int> ScatteredPInvoke = new()
    {
        ["Glass/ContentOverlay.cs"] = 1,
        ["Pip/PipControlInput.cs"] = 1,
        ["Pip/PipDisplay.cs"] = 3,
        ["Pip/PipEnhancedHover.cs"] = 3,
        ["Pip/PipInteraction.cs"] = 11,
        ["Pip/PipMessageInput.cs"] = 12,
        ["Pip/PipSourceList.cs"] = 1,
        ["Pip/PipTitleBar.cs"] = 3,
        ["Pip/PipWindow.cs"] = 1,
        ["Pip/PipWorkerSignal.cs"] = 2,
        ["Pip/PopupGuard.cs"] = 6,
        ["Pip/SourcePopups.cs"] = 2,
        ["Pip/WgcInterop.cs"] = 4,
        ["Settings/SettingsInputs.cs"] = 3,
        ["Settings/SettingsScroll.cs"] = 1,
        ["Tray/WindowMenuHost.cs"] = 1,
        ["Windows/ElementFinder.cs"] = 2,
        ["Windows/FullScreen.cs"] = 1,
        ["Windows/MouseGestures.cs"] = 15,
        ["Windows/WindowController.cs"] = 1,
    };

    internal static List<string> RatchetProblems(IReadOnlyDictionary<string, int> baseline, IReadOnlyDictionary<string, int> now)
    {
        var problems = new List<string>();
        foreach (string key in baseline.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            int before = baseline.GetValueOrDefault(key), after = now.GetValueOrDefault(key);
            if (after > before) problems.Add($"{key}: {after}, more than the {before} allowed. Don't add more; see the baseline's comment.");
            else if (after < before) problems.Add($"{key}: down to {after} from {before}. Good; lower the baseline to {after} so it stays down.");
        }
        return problems;
    }

    private static Dictionary<string, int> CurrentSettableStatics() =>
        typeof(AppSettings).Assembly.GetTypes()
            .Where(type => !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod is not null)
                .Select(property => $"{type.FullName!.Replace('+', '.')}.{property.Name}"))
            .ToDictionary(name => name, _ => 1, StringComparer.Ordinal);

    private static readonly Regex PInvoke = new("""\[\s*(?:DllImport|LibraryImport)\s*\(""");

    private static Dictionary<string, int> CurrentScatteredPInvoke() =>
        Sources.Value.Where(pair => !pair.Key.StartsWith("Native/", StringComparison.Ordinal))
            .Select(pair => (pair.Key, Count: PInvoke.Matches(WithoutComments(pair.Value)).Count))
            .Where(pair => pair.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Count, StringComparer.Ordinal);

    [Fact]
    public void Settable_static_properties_do_not_grow()
    {
        List<string> problems = RatchetProblems(SettableStatics.ToDictionary(name => name, _ => 1), CurrentSettableStatics());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Platform_calls_outside_Native_do_not_grow()
    {
        List<string> problems = RatchetProblems(ScatteredPInvoke, CurrentScatteredPInvoke());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_ratchet_fails_when_a_count_grows_or_shrinks_without_its_baseline()
    {
        var baseline = new Dictionary<string, int> { ["Pip/A.cs"] = 2 };
        Assert.Empty(RatchetProblems(baseline, new Dictionary<string, int> { ["Pip/A.cs"] = 2 }));
        Assert.Contains(RatchetProblems(baseline, new Dictionary<string, int> { ["Pip/A.cs"] = 3 }), p => p.Contains("more than", StringComparison.Ordinal));
        Assert.Contains(RatchetProblems(baseline, new Dictionary<string, int> { ["Pip/A.cs"] = 2, ["Tray/B.cs"] = 1 }), p => p.StartsWith("Tray/B.cs", StringComparison.Ordinal));
        Assert.Contains(RatchetProblems(baseline, new Dictionary<string, int>()), p => p.Contains("lower the baseline", StringComparison.Ordinal));
        Assert.Equal(2, PInvoke.Matches(WithoutComments("[DllImport(\"user32.dll\")] x; [LibraryImport( \"a\")] y; // [DllImport(")).Count);
    }

    // ── 7. Every command has a name in every language and something that runs it ─────────────────────────────────

    private static readonly Regex NameArm = new("""AppCommand\.(?<command>\w+)\s*=>\s*T\(\s*"(?<key>[^"]+)"\s*\)""");

    internal static List<string> CommandProblems(IEnumerable<AppCommand> commands, string nameBody,
        IReadOnlyList<IReadOnlySet<string>> catalogs, IEnumerable<AppCommand> listed, IEnumerable<AppCommand> everywhere,
        string runOnDisplay, string runEverywhere)
    {
        var problems = new List<string>();
        Dictionary<string, string> keys = NameArm.Matches(nameBody)
            .ToDictionary(m => m.Groups["command"].Value, m => m.Groups["key"].Value, StringComparer.Ordinal);
        var listedSet = listed.ToHashSet();
        var everywhereSet = everywhere.ToHashSet();
        foreach (AppCommand command in commands)
        {
            string name = command.ToString();
            if (!keys.TryGetValue(name, out string? key))
                problems.Add($"{name}: CommandCatalog.Name has no text for it.");
            else
                for (int i = 0; i < catalogs.Count; i++)
                    if (!catalogs[i].Contains(key))
                        problems.Add($"{name}: '{key}' is missing from catalog {i}.");
            if (!listedSet.Contains(command))
                problems.Add($"{name}: not in CommandCatalog.All, so it can't get a hotkey or a menu place.");
            bool handled = everywhereSet.Contains(command)
                ? Regex.IsMatch(runEverywhere, $@"case\s+AppCommand\.{name}\s*:")
                : Regex.IsMatch(runOnDisplay, $@"\bAppCommand\.{name}\b") || CommandCatalog.AnchorOf(command) is not null;
            if (!handled) problems.Add($"{name}: nothing runs it.");
        }
        return problems;
    }

    private static IReadOnlySet<string> CatalogKeys(string language)
    {
        using Stream stream = typeof(AppSettings).Assembly.GetManifestResourceStream($"WinScreen.Localization.{language}.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_command_has_a_name_in_all_three_languages_and_is_run_by_something()
    {
        string settings = WithoutComments(Source("Settings/AppSettings.cs"));
        string tray = WithoutComments(Source("Tray/TrayApplication.cs"));
        List<string> problems = CommandProblems(Enum.GetValues<AppCommand>(),
            MethodBody(settings, "public static string Name(AppCommand command)"),
            [CatalogKeys("en"), CatalogKeys("zh-Hans"), CatalogKeys("zh-Hant")],
            CommandCatalog.All, CommandCatalog.Everywhere,
            MethodBody(tray, "void RunOnDisplay("), MethodBody(tray, "void RunEverywhere("));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_command_check_fails_for_a_command_with_no_text_or_nothing_to_run_it()
    {
        IReadOnlySet<string> full = new HashSet<string> { "window.pin", "everywhere.unpin" };
        IReadOnlySet<string> partial = new HashSet<string> { "window.pin" };
        const string names = """{ AppCommand.TogglePin => T("window.pin"), AppCommand.UnpinEverywhere => T("everywhere.unpin"), }""";
        const string run = "{ switch (command) { case AppCommand.TogglePin: break; } }";
        const string everywhere = "{ switch (command) { case AppCommand.UnpinEverywhere: break; } }";
        AppCommand[] both = [AppCommand.TogglePin, AppCommand.UnpinEverywhere];
        AppCommand[] all = [AppCommand.UnpinEverywhere];

        Assert.Empty(CommandProblems(both, names, [full, full], both, all, run, everywhere));
        Assert.Contains(CommandProblems(both, names, [full, partial], both, all, run, everywhere), p => p.Contains("missing from catalog 1", StringComparison.Ordinal));
        Assert.Contains(CommandProblems([.. both, AppCommand.ToggleCrop], names, [full], [.. both, AppCommand.ToggleCrop], all, run, everywhere),
            p => p.StartsWith("ToggleCrop: CommandCatalog.Name", StringComparison.Ordinal));
        Assert.Contains(CommandProblems(both, names, [full], both, all, "{ }", everywhere), p => p == "TogglePin: nothing runs it.");
        Assert.Contains(CommandProblems(both, names, [full], both, all, run, "{ }"), p => p == "UnpinEverywhere: nothing runs it.");
        Assert.Contains(CommandProblems(both, names, [full], [AppCommand.TogglePin], all, run, everywhere), p => p.Contains("not in CommandCatalog.All", StringComparison.Ordinal));
    }
}
