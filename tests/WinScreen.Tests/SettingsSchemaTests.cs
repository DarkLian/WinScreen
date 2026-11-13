using System.Text.Json;
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

    [Fact]
    public void A_name_given_twice_keeps_the_last_as_before()
    {
        Assert.False(AppSettings.Parse("""{ "ShowToolbar": true, "ShowToolbar": false }""").ShowToolbar);

        File.WriteAllText(SettingsFile, """{ "ShowToolbar": true, "ShowToolbar": false }""");
        Assert.False(AppSettings.Load(SettingsFile).ShowToolbar);
    }
}
