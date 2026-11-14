using System.Text.Json;
using System.Text.Json.Nodes;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>The settings file's format number and the steps that bring older files up to date (roadmap P0-2).</summary>
public sealed class SettingsSchemaTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WinScreenSettingsSchema-" + Path.GetRandomFileName());
    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public SettingsSchemaTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Toolbar(string? schema, bool offered) =>
        $$"""{ {{(schema is null ? "" : $"\"SchemaVersion\": {schema},")}} "Toolbar": { "Buttons": ["TogglePin"]{{(offered ? ", \"WindowMenuOffered\": true" : "")}} } }""";

    [Fact]
    public void There_is_one_step_for_every_format_before_the_current_one()
    {
        Assert.True(AppSettings.Migrations.Length == AppSettings.CurrentSchemaVersion,
            $"{AppSettings.Migrations.Length} steps for format {AppSettings.CurrentSchemaVersion}.");
    }

    [Fact]
    public void Saved_settings_say_which_format_they_are_in()
    {
        new AppSettings().Save(SettingsFile);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        Assert.Equal(AppSettings.CurrentSchemaVersion, saved.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("\"one\"")] // unreadable numbers count as no number
    [InlineData("-3")]
    public void A_file_from_before_the_format_was_numbered_gets_each_step(string? schema)
    {
        AppSettings loaded = AppSettings.Parse(Toolbar(schema, offered: false));

        Assert.Equal([AppCommand.TogglePin, AppCommand.ShowWindowMenu], loaded.Toolbar.Buttons);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void A_step_already_taken_is_not_taken_again()
    {
        Assert.Equal([AppCommand.TogglePin], AppSettings.Parse(Toolbar("1", offered: false)).Toolbar.Buttons);
        Assert.Equal([AppCommand.TogglePin], AppSettings.Parse(Toolbar(null, offered: true)).Toolbar.Buttons);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_kept_aside_before_it_is_written_over()
    {
        const string newer = """{ "SchemaVersion": 7, "ShowToolbar": false, "SomethingNew": { "Level": 3 } }""";
        File.WriteAllText(SettingsFile, newer);

        AppSettings loaded = AppSettings.Load(SettingsFile);
        Assert.False(loaded.ShowToolbar);
        Assert.Equal(newer, File.ReadAllText(SettingsFile)); // loading alone changes nothing

        loaded.Save(SettingsFile);
        string kept = SettingsFile + ".from-schema-7";
        Assert.Equal(newer, File.ReadAllText(kept));
        Assert.Equal(AppSettings.CurrentSchemaVersion, AppSettings.Load(SettingsFile).SchemaVersion);

        // Once: a later newer file doesn't replace the first copy.
        File.WriteAllText(SettingsFile, newer.Replace("false", "true"));
        AppSettings.Load(SettingsFile).Save(SettingsFile);
        Assert.Equal(newer, File.ReadAllText(kept));
    }

    // ── A file from a newer version that adds a command or a choice (roadmap P0-5) ─────────────────────────────────

    private const string Newer = "SomethingNewer";

    [Fact]
    public void A_hotkey_for_a_command_from_a_newer_version_costs_only_that_hotkey()
    {
        string file = $$"""{ "SchemaVersion": 1, "ShowToolbar": false, "Hotkeys": { "TogglePin": "Ctrl+Shift+F9", "{{Newer}}": "Ctrl+Alt+N" } }""";
        File.WriteAllText(SettingsFile, file);

        AppSettings loaded = AppSettings.Load(SettingsFile);
        Assert.False(loaded.ShowToolbar);
        Assert.Equal("Ctrl+Shift+F9", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName)); // not set aside as damaged
        Assert.Equal(file, File.ReadAllText(SettingsFile)); // loading alone changes nothing

        loaded.Save(SettingsFile);
        Assert.Equal(file, File.ReadAllText(SettingsFile + ".from-newer"));
        Assert.DoesNotContain(Newer, File.ReadAllText(SettingsFile), StringComparison.Ordinal);

        // Once: the copy kept is the first one.
        File.WriteAllText(SettingsFile, file.Replace("false", "true"));
        AppSettings.Load(SettingsFile).Save(SettingsFile);
        Assert.Equal(file, File.ReadAllText(SettingsFile + ".from-newer"));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("3")] // a number, even one of a known command, isn't how hotkeys are saved
    [InlineData("togglepin, opacityup")] // parses as OpacityDown
    [InlineData("")]
    public void A_hotkey_name_this_version_cannot_place_is_dropped(string name)
    {
        AppSettings loaded = AppSettings.Parse($$"""{ "ShowToolbar": false, "Hotkeys": { "TogglePin": "Ctrl+Shift+F9", "{{name}}": "Ctrl+Alt+N" } }""",
            () => Assert.Fail("Read as damaged."));

        Assert.False(loaded.ShowToolbar);
        Assert.Equal("Ctrl+Shift+F9", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.DoesNotContain("Ctrl+Alt+N", loaded.Hotkeys.Values); // not moved to some other command
        Assert.All(loaded.Hotkeys.Keys, command => Assert.True(Enum.IsDefined(command)));
    }

    [Fact]
    public void A_hotkey_name_in_other_letter_case_is_kept()
    {
        AppSettings loaded = AppSettings.Parse("""{ "Hotkeys": { "opacityup": "Ctrl+Alt+N" } }""", () => Assert.Fail("Read as damaged."));
        Assert.Equal("Ctrl+Alt+N", loaded.Hotkeys[AppCommand.OpacityUp]);
    }

    // ── What an older version finds in a file this one wrote (roadmap step 6.4) ─────────────────────────────────────

    private static HashSet<string> HotkeyNames(string json) =>
        JsonNode.Parse(json)!["Hotkeys"]!.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_file_written_here_names_no_command_newer_than_1_0_0_unless_it_was_given_a_key()
    {
        HashSet<string> published = HotkeyNames(SettingsFixtureTests.Fixture("1.0.0"));
        // Settings writes an entry for every command, empty ones too.
        var settings = new AppSettings();
        foreach (AppCommand command in CommandCatalog.All) settings.Hotkeys.TryAdd(command, "");
        settings.Save(SettingsFile);
        HashSet<string> written = HotkeyNames(File.ReadAllText(SettingsFile));
        Assert.Empty(written.Except(published));
        Assert.All(CommandCatalog.All, command => Assert.True(settings.Hotkeys.ContainsKey(command))); // the copy in memory keeps every entry

        // A key cleared that had a default is written, as an empty entry, so the default doesn't come back.
        settings.Hotkeys[AppCommand.TogglePin] = "";
        AppCommand newest = Enum.GetValues<AppCommand>().First(command => !published.Contains(command.ToString()));
        settings.Hotkeys[newest] = "Ctrl+Alt+F11";
        settings.Save(SettingsFile);
        AppSettings loaded = AppSettings.Load(SettingsFile);
        Assert.Equal("", loaded.Hotkeys[AppCommand.TogglePin]);
        Assert.Equal("Ctrl+Alt+F11", loaded.Hotkeys[newest]);
        Assert.Contains(newest.ToString(), HotkeyNames(File.ReadAllText(SettingsFile)));
    }

    [Theory]
    [MemberData(nameof(SettingsFixtureTests.Versions), MemberType = typeof(SettingsFixtureTests))]
    public void Every_published_file_keeps_its_hotkeys_through_a_save(string version)
    {
        File.WriteAllText(SettingsFile, SettingsFixtureTests.Fixture(version));
        AppSettings first = AppSettings.Load(SettingsFile);
        first.Save(SettingsFile);
        AppSettings second = AppSettings.Load(SettingsFile);
        // A missing entry means no key, as an empty one does.
        string[] Keys(AppSettings settings) => Enum.GetValues<AppCommand>().Select(command => settings.Hotkeys.GetValueOrDefault(command, "")).ToArray();
        Assert.Equal(Keys(first), Keys(second));
    }

    [Fact]
    public void A_file_this_version_understands_is_not_kept_aside()
    {
        new AppSettings().Save(SettingsFile);
        AppSettings.Load(SettingsFile).Save(SettingsFile);
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    /// <summary>
    /// The settings documents a newer version starts from: the newest published version's fixture, and this
    /// version's own defaults, so a setting added since the last release is covered too.
    /// </summary>
    public static TheoryData<string> Documents() => new("fixture", "defaults");

    private static string Document(string name) => name == "fixture"
        ? SettingsFixtureTests.Fixture(SettingsFixtureTests.VersionNames()
            .MaxBy(version => Version.Parse(version.Split('-')[0]))!)
        : JsonSerializer.Serialize(new AppSettings());

    /// <summary>One change a newer version could make to a saved file, and the part of the file it may cost.</summary>
    public sealed record NewerChange(string Description, string File, string MayCost);

    /// <summary>
    /// Every value turned into one a newer version could write (an unknown name, a small unknown number), every
    /// object given a member it doesn't know, every list of names given one more.
    /// </summary>
    internal static IEnumerable<NewerChange> NewerChanges(string json)
    {
        JsonNode original = JsonNode.Parse(json)!;
        foreach ((string path, JsonNode? node, JsonNode? parent) in Walk(original, "$", null))
        {
            string mayCost = parent is JsonArray ? path[..path.LastIndexOf('[')] : path;
            switch (node)
            {
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    yield return Change($"{path} = \"{Newer}\"", path, JsonValue.Create(Newer), mayCost);
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    yield return Change($"{path} = 9", path, JsonValue.Create(9), mayCost);
                    break;
                case JsonObject:
                    yield return Change($"{path}.{Newer} added", path + "." + Newer, JsonValue.Create("1"), path + "." + Newer);
                    break;
                case JsonArray array when array.Count > 0 && array.All(item => item?.GetValueKind() == JsonValueKind.String):
                    yield return Change($"{path} gets \"{Newer}\"", path + "[+]", JsonValue.Create(Newer), path);
                    break;
            }
        }

        NewerChange Change(string description, string path, JsonNode replacement, string mayCost)
        {
            JsonNode copy = JsonNode.Parse(json)!;
            Set(copy, path, replacement);
            return new NewerChange(description, copy.ToJsonString(), mayCost);
        }
    }

    private static IEnumerable<(string Path, JsonNode? Node, JsonNode? Parent)> Walk(JsonNode? node, string path, JsonNode? parent)
    {
        yield return (path, node, parent);
        if (node is JsonObject obj)
            foreach ((string name, JsonNode? child) in obj)
                foreach (var found in Walk(child, path + "." + name, node)) yield return found;
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                foreach (var found in Walk(array[i], $"{path}[{i}]", node)) yield return found;
    }

    private static void Set(JsonNode root, string path, JsonNode replacement)
    {
        // Paths are $.Name, $.Name[2], $.Name.Inner, $.Name[+] (append); names hold no dots or brackets.
        string[] steps = path[2..].Split('.');
        JsonNode current = root;
        for (int i = 0; i < steps.Length; i++)
        {
            string step = steps[i];
            int bracket = step.IndexOf('[');
            string name = bracket < 0 ? step : step[..bracket];
            bool last = i == steps.Length - 1;
            if (bracket < 0)
            {
                if (last) { current.AsObject()[name] = replacement; return; }
                current = current[name]!;
                continue;
            }
            JsonArray array = current[name]!.AsArray();
            string index = step[(bracket + 1)..^1];
            if (index == "+") { array.Add(replacement); return; }
            if (last) { array[int.Parse(index)] = replacement; return; }
            current = array[int.Parse(index)]!;
        }
    }

    /// <summary>Every value of the loaded settings, as this version would save them, by path.</summary>
    private static Dictionary<string, string> Values(AppSettings settings) =>
        Walk(JsonSerializer.SerializeToNode(settings), "$", null)
            .Where(found => found.Node is not JsonObject and not JsonArray)
            .ToDictionary(found => found.Path, found => found.Node?.ToJsonString() ?? "null");

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_value_from_a_newer_version_costs_at_most_itself(string document)
    {
        string json = Document(document);
        Dictionary<string, string> expected = Values(AppSettings.Parse(json, () => Assert.Fail("The document itself reads as damaged.")));
        var problems = new List<string>();
        int changes = 0;
        foreach (NewerChange change in NewerChanges(json))
        {
            changes++;
            bool damaged = false;
            AppSettings loaded;
            try
            {
                loaded = AppSettings.Parse(change.File, () => damaged = true);
            }
            catch (Exception e)
            {
                problems.Add($"{change.Description}: the whole file reads as damaged ({e.GetType().Name}).");
                continue;
            }
            if (damaged)
            {
                problems.Add($"{change.Description}: the whole file reads as damaged.");
                continue;
            }
            Dictionary<string, string> actual = Values(loaded);
            string[] lost = expected.Keys.Union(actual.Keys)
                .Where(path => !path.StartsWith(change.MayCost, StringComparison.Ordinal))
                .Where(path => expected.GetValueOrDefault(path) != actual.GetValueOrDefault(path)).ToArray();
            if (lost.Length > 0)
                problems.Add($"{change.Description}: also changed {string.Join(", ", lost.Take(5))}{(lost.Length > 5 ? ", …" : "")}");
        }

        Assert.True(changes > 50, $"Only {changes} changes tried.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_enum_in_settings_appears_in_this_versions_defaults()
    {
        // So the newer-value check above meets every saved choice, including any added since the last release.
        var found = new HashSet<Type>();
        Visit(new AppSettings(), found, new HashSet<object>(ReferenceEqualityComparer.Instance));
        // A rule's place is empty by default; its newer-value case is its own test below.
        found.Add(typeof(WinScreen.Windows.WindowAnchor));
        Type[] missing = InvariantTests.PersistedEnums(typeof(AppSettings)).Where(type => !found.Contains(type)).ToArray();
        Assert.True(missing.Length == 0,
            "Give these a value in the defaults, or a case of their own in SettingsSchemaTests: " + string.Join(", ", missing.Select(type => type.Name)));

        static void Visit(object? value, HashSet<Type> found, HashSet<object> seen)
        {
            if (value is null) return;
            Type type = value.GetType();
            if (type.IsEnum) { found.Add(type); return; }
            if (type.IsPrimitive || value is string || !seen.Add(value)) return;
            if (value is System.Collections.IDictionary dictionary)
            {
                foreach (System.Collections.DictionaryEntry entry in dictionary) { Visit(entry.Key, found, seen); Visit(entry.Value, found, seen); }
                return;
            }
            if (value is System.Collections.IEnumerable items)
            {
                foreach (object? item in items) Visit(item, found, seen);
                return;
            }
            if (type.Assembly != typeof(AppSettings).Assembly) return;
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true }) continue;
                if (property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length > 0) continue;
                Visit(property.GetValue(value), found, seen);
            }
        }
    }

    [Fact]
    public void A_place_from_a_newer_version_costs_only_the_place()
    {
        AppSettings loaded = AppSettings.Parse("""{ "SavedSetups": [ { "Program": "a", "Pinned": true, "Place": 99, "Borderless": true } ] }""",
            () => Assert.Fail("Read as damaged."));
        ProgramSetup rule = Assert.Single(loaded.SavedSetups);
        Assert.Null(rule.Place);
        Assert.True(rule.Pinned && rule.Borderless);
    }

    [Fact]
    public void A_name_given_twice_keeps_the_last_as_before()
    {
        Assert.False(AppSettings.Parse("""{ "ShowToolbar": true, "ShowToolbar": false }""").ShowToolbar);

        File.WriteAllText(SettingsFile, """{ "ShowToolbar": true, "ShowToolbar": false }""");
        Assert.False(AppSettings.Load(SettingsFile).ShowToolbar);
    }
}
