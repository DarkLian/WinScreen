using WinScreen.Pip;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Which programs Enhanced hover may be used with, and which views start with it on.</summary>
public sealed class PipHoverAppsTests
{
    [Theory]
    [InlineData("chrome", "Chrome_WidgetWin_1", false, PipHoverApp.Browser)]
    [InlineData("msedge", "Chrome_WidgetWin_1", false, PipHoverApp.Browser)]
    [InlineData("Brave", "Chrome_WidgetWin_1", false, PipHoverApp.Browser)]
    [InlineData("vivaldi", "Chrome_WidgetWin_1", false, PipHoverApp.Browser)]
    [InlineData("firefox", "MozillaWindowClass", false, PipHoverApp.Firefox)]
    [InlineData("Code", "Chrome_WidgetWin_1", true, PipHoverApp.Electron)]
    [InlineData("Discord", "Chrome_WidgetWin_1", true, PipHoverApp.Electron)]
    [InlineData("explorer", "CabinetWClass", false, PipHoverApp.FileExplorer)]
    // The shell's desktop and taskbar belong to the same program as folder windows.
    [InlineData("explorer", "Progman", false, PipHoverApp.None)]
    [InlineData("explorer", "Shell_TrayWnd", false, PipHoverApp.None)]
    // A window class or a folder layout alone is not enough.
    [InlineData("launcher", "Chrome_WidgetWin_1", false, PipHoverApp.None)]
    [InlineData("game", "UnrealWindow", true, PipHoverApp.None)]
    [InlineData("notepad", "Notepad", false, PipHoverApp.None)]
    [InlineData("Listed", "AnyClass", false, PipHoverApp.Listed)]
    public void A_program_is_one_of_the_known_kinds_or_listed_or_left_alone(string program, string rootClass, bool electron, object expected)
    {
        Assert.Equal((PipHoverApp)expected, PipHoverApps.Classify(program, rootClass, electron, ["listed"]));
    }

    [Fact]
    public void An_electron_app_is_known_by_its_resources_beside_it_or_one_folder_down()
    {
        string root = Directory.CreateTempSubdirectory("winscreen-electron-layout").FullName;
        try
        {
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            string packed = Folder("packed"), unpacked = Folder("unpacked"), versioned = Folder("versioned"), plain = Folder("plain");
            Directory.CreateDirectory(Path.Combine(packed, "resources"));
            File.WriteAllText(Path.Combine(packed, "resources", "app.asar"), "");
            Directory.CreateDirectory(Path.Combine(unpacked, "resources", "app"));
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(versioned, "app-1.0.0", "resources")).FullName, "app.asar"), "");
            // Electron's own download keeps only its default app there, which is not an app of its own.
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(plain, "resources")).FullName, "default_app.asar"), "");

            Assert.True(PipEnhancedHover.HasElectronLayout(packed));
            Assert.True(PipEnhancedHover.HasElectronLayout(unpacked));
            Assert.True(PipEnhancedHover.HasElectronLayout(versioned));
            Assert.False(PipEnhancedHover.HasElectronLayout(plain));
            Assert.False(PipEnhancedHover.HasElectronLayout(Path.Combine(root, "missing")));
            Assert.False(PipEnhancedHover.HasElectronLayout(""));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_view_starts_with_hover_on_only_for_the_kinds_chosen_in_settings()
    {
        var nothing = new PipHoverPreferences { ChooseForMe = false };
        foreach (PipHoverApp app in Enum.GetValues<PipHoverApp>())
            Assert.Equal(app == PipHoverApp.Listed, PipHoverApps.StartsOn(app, nothing));

        var some = new PipHoverPreferences { ChooseForMe = false, Browsers = true, FileExplorer = true };
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Browser, some));
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.FileExplorer, some));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.Firefox, some));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.Electron, some));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.None, new PipHoverPreferences
            { ChooseForMe = false, Browsers = true, Firefox = true, Electron = true, FileExplorer = true }));
    }

    [Fact]
    public void Choosing_for_the_person_turns_on_the_kinds_known_to_work_and_ignores_their_own_picks()
    {
        // The default, and a settings file from before the choice existed.
        Assert.True(new AppSettings().PipHover.ChooseForMe);
        Assert.True(AppSettings.Parse("""{"PipHover":{"Browsers":false,"FileExplorer":true}}""").PipHover.ChooseForMe);

        var automatic = new PipHoverPreferences { FileExplorer = true };
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Browser, automatic));
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Firefox, automatic));
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Electron, automatic));
        // File Explorer's tab menus stop responding with the helper on, so it is never picked for anyone.
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.FileExplorer, automatic));
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Listed, automatic));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.None, automatic));
    }

    [Fact]
    public void Programs_to_use_directly_are_matched_by_name_and_survive_saving()
    {
        IReadOnlyList<string> before = PipOriginalApps.Programs;
        try
        {
            PipOriginalApps.Programs = ["ExampleGame", " "];
            Assert.True(PipOriginalApps.Includes("examplegame"));
            Assert.False(PipOriginalApps.Includes("notepad"));
            Assert.False(PipOriginalApps.Includes(""));
        }
        finally { PipOriginalApps.Programs = before; }

        Assert.Empty(new AppSettings().PipOriginalPrograms);
        var settings = new AppSettings { PipOriginalPrograms = ["game", " ", "GAME", "editor"] };
        Assert.Equal(["game", "editor"], AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(settings)).PipOriginalPrograms);
        Assert.Empty(AppSettings.Parse("""{"PipOriginalPrograms":null}""").PipOriginalPrograms);
    }

    [Fact]
    public void Own_hover_choices_are_off_by_default_and_survive_saving()
    {
        var defaults = new AppSettings();
        Assert.Equal((false, false, false, false), (defaults.PipHover.Browsers, defaults.PipHover.Firefox,
            defaults.PipHover.Electron, defaults.PipHover.FileExplorer));
        Assert.Empty(defaults.PipHover.Programs);

        defaults.PipHover = new PipHoverPreferences { ChooseForMe = false, Firefox = true, Electron = true, Programs = ["vlc", " ", "VLC", "mpv"] };
        AppSettings loaded = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(defaults));
        Assert.False(loaded.PipHover.ChooseForMe);
        Assert.True(loaded.PipHover.Firefox);
        Assert.False(loaded.PipHover.FileExplorer);
        Assert.True(loaded.PipHover.Electron);
        Assert.False(loaded.PipHover.Browsers);
        Assert.Equal(["vlc", "mpv"], loaded.PipHover.Programs);

        // A settings file from before this option, and one with the section emptied out.
        Assert.False(AppSettings.Parse("{}").PipHover.Browsers);
        Assert.Empty(AppSettings.Parse("""{"PipHover":{"Programs":null}}""").PipHover.Programs);
        Assert.Empty(AppSettings.Parse("""{"PipHover":null}""").PipHover.Programs);
    }
}
