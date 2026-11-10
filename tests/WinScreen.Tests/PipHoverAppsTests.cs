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
    // Not a known kind: its parked window keeps the hover but doesn't redraw it. It can still be listed by name.
    [InlineData("firefox", "MozillaWindowClass", false, PipHoverApp.None)]
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
    public void A_view_starts_with_hover_on_only_for_the_kinds_chosen_in_settings()
    {
        var nothing = new PipHoverPreferences();
        foreach (PipHoverApp app in Enum.GetValues<PipHoverApp>())
            Assert.Equal(app == PipHoverApp.Listed, PipHoverApps.StartsOn(app, nothing));

        var some = new PipHoverPreferences { Browsers = true, FileExplorer = true };
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.Browser, some));
        Assert.True(PipHoverApps.StartsOn(PipHoverApp.FileExplorer, some));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.Electron, some));
        Assert.False(PipHoverApps.StartsOn(PipHoverApp.None, new PipHoverPreferences
            { Browsers = true, Electron = true, FileExplorer = true }));
    }

    [Fact]
    public void Hover_choices_are_off_by_default_and_survive_saving()
    {
        var defaults = new AppSettings();
        Assert.Equal((false, false, false), (defaults.PipHover.Browsers, defaults.PipHover.Electron, defaults.PipHover.FileExplorer));
        Assert.Empty(defaults.PipHover.Programs);

        defaults.PipHover = new PipHoverPreferences { FileExplorer = true, Electron = true, Programs = ["vlc", " ", "VLC", "mpv"] };
        AppSettings loaded = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(defaults));
        Assert.True(loaded.PipHover.FileExplorer);
        Assert.True(loaded.PipHover.Electron);
        Assert.False(loaded.PipHover.Browsers);
        Assert.Equal(["vlc", "mpv"], loaded.PipHover.Programs);

        // A settings file from before this option, and one with the section emptied out.
        Assert.False(AppSettings.Parse("{}").PipHover.Browsers);
        Assert.Empty(AppSettings.Parse("""{"PipHover":{"Programs":null}}""").PipHover.Programs);
        Assert.Empty(AppSettings.Parse("""{"PipHover":null}""").PipHover.Programs);
    }
}
