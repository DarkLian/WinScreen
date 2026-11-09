using WinScreen.Localization;
using WinScreen.Settings;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace WinScreen.Tests;

public sealed class LocalizationTests : IDisposable
{
    public void Dispose() => UiText.Initialize("en");

    [Theory]
    [InlineData("zh-Hans", "en-US", "zh-Hans", "设置")]
    [InlineData("zh-Hant", "en-US", "zh-Hant", "設定")]
    [InlineData("system", "zh-CN", "zh-Hans", "设置")]
    [InlineData("system", "zh-SG", "zh-Hans", "设置")]
    [InlineData("system", "zh-TW", "zh-Hant", "設定")]
    [InlineData("system", "zh-HK", "zh-Hant", "設定")]
    [InlineData("system", "zh-MO", "zh-Hant", "設定")]
    [InlineData("en", "zh-TW", "en", "Settings")]
    [InlineData("unknown", "zh-TW", "en", "Settings")]
    public void Language_choice_overrides_system_and_falls_back_to_English(string choice, string system,
        string expected, string settings)
    {
        UiText.Initialize(choice, system);
        Assert.Equal(expected, UiText.Language);
        Assert.Equal(settings, UiText.T("settings.title"));
        Assert.Equal("unknown.key", UiText.T("unknown.key"));
    }

    [Theory]
    [InlineData("zh-Hans", "示例: 置顶, 不透明度 50%, 深色")]
    [InlineData("zh-Hant", "示例: 置頂, 不透明度 50%, 深色")]
    public void Saved_setups_translate_without_changing_program_names(string language, string expected)
    {
        UiText.Initialize(language);
        Assert.Equal(expected, new ProgramSetup { Program = "示例", Pinned = true, Alpha = 128, Dark = true }.ToString());
        Assert.DoesNotContain("{0}", string.Format(UiText.T("errors.window.changeRefusedCount"), 3));
        Assert.NotEqual("Another app already uses: ", UiText.T("hotkeys.conflictPrefix"));
    }

    [Theory]
    [InlineData("zh-Hans", "其他应用已占用： ", "WinScreen 浮动锚点 - ")]
    [InlineData("zh-Hant", "其他應用程式已占用： ", "WinScreen 浮動錨點 - ")]
    public void Translated_prefixes_preserve_the_spaces_before_dynamic_content(string language, string conflictPrefix,
        string anchorPrefix)
    {
        UiText.Initialize(language);
        Assert.Equal(conflictPrefix + "Ctrl+Alt+T", UiText.T("hotkeys.conflictPrefix") + "Ctrl+Alt+T");
        Assert.Equal(anchorPrefix + "ExampleProgram", UiText.T("anchor.titlePrefix") + "ExampleProgram");
    }

    private static readonly string[] Languages = ["en", "zh-Hans", "zh-Hant"];
    private static readonly Regex KeyPattern = new(@"\A[a-z][a-zA-Z0-9]*(?:\.[a-z][a-zA-Z0-9]*)+\z");
    private static readonly Regex Lookup = new("""\bT\s*\(\s*"(?<key>[^"\\]*)"\s*\)""");

    private static Stream Resource(string language) => typeof(UiText).Assembly
        .GetManifestResourceStream($"WinScreen.Localization.{language}.json")!;

    [Fact]
    public void Every_source_lookup_is_a_stable_literal_key_present_in_English_and_no_entries_are_unused()
    {
        IReadOnlyDictionary<string, string> english = UiText.GetCatalog("en");
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        Assembly assembly = typeof(LocalizationTests).Assembly;
        string[] sources = assembly.GetManifestResourceNames().Where(name => name.StartsWith("LocalizationSource.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sources);
        foreach (string source in sources.Where(name => !name.EndsWith("Localization.cs", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(source)!);
            string code = reader.ReadToEnd();
            // Preserve string literals while removing comments, so examples in comments aren't lookups.
            code = Regex.Replace(code,
                """
                "(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/
                """,
                match => match.Value.StartsWith('"') ? match.Value : new string(' ', match.Length));
            Match[] lookups = Lookup.Matches(code).Cast<Match>().ToArray();
            Assert.True(lookups.Length == Regex.Matches(code, @"\bT\s*\(").Count,
                $"{source}: use T(\"stable.key\") at each label's producer; helpers should accept translated text.");
            foreach (Match lookup in lookups)
            {
                string key = lookup.Groups["key"].Value;
                Assert.Matches(KeyPattern, key);
                Assert.True(english.ContainsKey(key), $"{source}: missing English key {key}.");
                referenced.Add(key);
            }
        }
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), referenced.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Embedded_catalogs_have_the_same_keys_valid_JSON_and_nonempty_translations(string language)
    {
        using Stream resource = Resource(language);
        Assert.NotNull(resource);
        var parsed = UiText.ParseCatalog(resource); // Rejects duplicates instead of silently keeping the last value.
        Assert.NotEmpty(parsed);
        Assert.Equal(UiText.GetCatalog("en").Keys.Order(StringComparer.Ordinal), parsed.Keys.Order(StringComparer.Ordinal));
        foreach ((string key, string value) in parsed)
        {
            Assert.Matches(KeyPattern, key);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language}: {key} is empty.");
            Assert.DoesNotContain("??", value);
            Assert.DoesNotContain("\uFFFD", value);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Catalog_files_keep_their_entries_sorted_by_key(string language)
    {
        using Stream resource = Resource(language);
        using JsonDocument document = JsonDocument.Parse(resource);
        string[] keys = document.RootElement.EnumerateObject().Select(entry => entry.Name).ToArray();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"settings.title":"One","settings.title":"Two"}""")]
    [InlineData("""{"settings.title":"One","settings.\u0074itle":"Two"}""")]
    [InlineData("""{"settings.title":""}""")]
    [InlineData("""{"settings.title":" \t "}""")]
    [InlineData("""{"settings.title":null}""")]
    [InlineData("""{"settings.title":42}""")]
    [InlineData("""{"":"Empty key"}""")]
    public void Invalid_catalogs_are_rejected(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.ThrowsAny<JsonException>(() => UiText.ParseCatalog(stream));
    }

    private static int[] PlaceholderIndices(string text)
    {
        _ = CompositeFormat.Parse(text);
        // Escaped braces are tokens of their own; a translator may reorder arguments but cannot lose or add one.
        return Regex.Matches(text, @"\{\{|\}\}|\{(?<index>\d+)(?:,\s*-?\d+\s*)?(?::[^{}]*)?\}")
            .Cast<Match>().Where(match => match.Groups["index"].Success)
            .Select(match => int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
    }

    [Fact]
    public void Translations_preserve_all_format_arguments_including_repeated_placeholders()
    {
        foreach ((string key, string english) in UiText.GetCatalog("en"))
        foreach (string language in Languages)
        {
            string translation = UiText.GetCatalog(language)[key];
            Assert.Equal(PlaceholderIndices(english), PlaceholderIndices(translation));
        }
        Assert.Equal([0, 1, 1], PlaceholderIndices("{{literal}} {1} {0,10:N2} {1}"));
        Assert.Equal([0], PlaceholderIndices("{{{0}}}"));
        Assert.Throws<FormatException>(() => PlaceholderIndices("{0"));
    }

    [Fact]
    public void Missing_translations_fall_back_to_English_without_changing_spacing_or_accelerators()
    {
        IReadOnlyDictionary<string, string> english = UiText.GetCatalog("en");
        var partial = new Dictionary<string, string> { ["settings.title"] = "Custom" };
        Assert.Equal("Custom", UiText.Translate("settings.title", partial, english));
        Assert.Equal("&Save", UiText.Translate("settings.save", partial, english));
        Assert.Equal("Target: ", UiText.Translate("tray.targetPrefix", partial, english));
        Assert.Equal("unknown.key", UiText.Translate("unknown.key", partial, english));
    }

    [Fact]
    public void Switching_languages_reuses_the_embedded_catalogs()
    {
        var catalogs = Languages.Select(UiText.GetCatalog).ToArray();
        for (int pass = 0; pass < 3; pass++)
        for (int i = 0; i < Languages.Length; i++)
        {
            UiText.Initialize(Languages[i]);
            Assert.Same(catalogs[i], UiText.GetCatalog(Languages[i]));
            Assert.Equal(catalogs[i]["settings.title"], UiText.T("settings.title"));
        }
    }

    [Theory]
    // Reviewed catalog values, sorted by stable key with NUL separators.
    // Update deliberately when adding labels; existing values retain their migration output.
    [InlineData("en", "F88F5389AB6F4DDA6BFAF42D7B2753271DC24C78BC0B1E7B96AA7B0FB23B093E")]
    [InlineData("zh-Hans", "D74450EE2A24E887B6A85E9C46712604950244FA9CA054EA180D87305E004A6D")]
    [InlineData("zh-Hant", "E88234F5E269D233541D72BDBBA47B112C408C20B2D9B04F525CDD90A461C3A8")]
    public void Every_translation_matches_the_reviewed_catalog(string language, string expectedDigest)
    {
        UiText.Initialize(language);
        string values = string.Concat(UiText.GetCatalog("en").Keys.Order(StringComparer.Ordinal)
            .Select(key => key + '\0' + UiText.T(key) + '\0'));
        Assert.Equal(expectedDigest, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values))));
    }
}

[Trait("Category", "RealWindows")]
public sealed class LocalizationSurfaceTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void Settings_window_menus_PiP_Anchors_and_errors_use_the_selected_catalog(string language)
    {
        using var target = new Support.TargetProcess(new Rectangle(240, 180, 400, 300), title: "Localization & test target");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            try
            {
                UiText.Initialize(language);
                var catalog = UiText.GetCatalog(language);
                using var settings = new SettingsWindow(new AppSettings { Language = language },
                    new Tray.StartWithWindows(@"Software\WinScreenTests\Localization"), _ => []);
                Assert.Equal(catalog["settings.window.title"], settings.Text);
                var controls = Descendants(settings).ToArray();
                Assert.Contains(controls, control => control.Text == catalog["settings.save"]);
                foreach (string key in new[] { "settings.general", "features.title", "settings.programs", "gestures.title", "pip.rendering", "pip.frameRate" })
                    Assert.Contains(controls, control => control.Text == catalog[key]);

                using var menu = new ContextMenuStrip();
                var builder = new Tray.WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true,
                    (_, _) => true, (_, _) => false, () => { });
                builder.Populate(menu.Items, false);
                foreach (string key in new[] { "window.restore", "window.borderless", "pip.open", "common.copy", "window.openLocation", "window.arrange" })
                    Assert.Contains(menu.Items.OfType<ToolStripMenuItem>(), item => item.Text == catalog[key]);
                ToolStripMenuItem arrange = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == catalog["window.arrange"]);
                Assert.Contains(arrange.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text == catalog["window.info"]);

                var windows = new Windows.WindowController([]);
                using var pip = new Pip.PipWindow(target.Handle, windows);
                Assert.Equal(catalog["pip.titlePrefix"] + "Localization & test target", pip.Text);
                foreach (string key in new[] { "pip.keepUpdating", "pip.options", "pip.close", "pip.goToOriginal" })
                    Assert.Contains(pip.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), item => item.Text == catalog[key]);

                using var anchor = new Anchors.AnchorButton(target.Handle);
                Assert.Equal(catalog["anchor.titlePrefix"] + "Localization & test target", anchor.Text);
                foreach (string key in new[] { "window.show", "window.minimize", "anchor.closeWindow", "anchor.remove" })
                    Assert.Contains(anchor.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), item => item.Text == catalog[key]);

                Assert.Equal(catalog["window.info.title"], Windows.WindowInfo.Facts(target.Handle)[0].Label);
                var error = Assert.Throws<Windows.WindowOperationException>(() => windows.Crop(target.Handle, Rectangle.Empty));
                Assert.Equal(catalog["errors.crop.empty"], error.Message);
                Assert.Equal(0, windows.RestoreAll());
            }
            finally { UiText.Initialize("en"); }
        });
    }

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
