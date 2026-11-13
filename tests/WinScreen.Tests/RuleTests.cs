using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Which saved setup ("rule", roadmap P1-5) a window gets.</summary>
public sealed class RuleTests
{
    [Theory]
    [InlineData("Team chat - Work", "chat", true)]
    [InlineData("Team chat - Work", "CHAT", true)]
    [InlineData("Team chat - Work", "team*work", true)]
    [InlineData("Team chat - Work", "work*team", false)]
    [InlineData("Team chat - Work", "*", true)]
    [InlineData("Inbox", "chat", false)]
    public void A_title_matches_by_contains_with_star_for_any_run(string title, string pattern, bool matches) =>
        Assert.Equal(matches, ProgramSetup.TitleMatches(title, pattern));

    private static ProgramSetup Rule(string name, string program = "app", string? path = null, string? title = null, string? cls = null, bool off = false) =>
        new() { Program = program, ExecutablePath = path, TitleContains = title, WindowClass = cls, Off = off, Pinned = true, ViewKeepsUpdating = name == "x" };

    private static ProgramSetup? Choose(ProgramSetup[] rules, string title = "Main window", string cls = "AppWindow", string path = @"C:\App\app.exe") =>
        ProgramSetup.Choose(rules, "app", path, title, cls);

    [Fact]
    public void A_setup_saved_before_rules_still_matches_every_window_of_its_program()
    {
        ProgramSetup old = Rule("old");
        Assert.Same(old, Choose([old]));
        Assert.Same(old, Choose([old], title: "Anything at all", cls: "Other"));
        Assert.Null(ProgramSetup.Choose([old], "other", @"C:\Other\other.exe", "Main window", "AppWindow"));
    }

    [Fact]
    public void A_rule_for_some_windows_wins_over_one_for_the_whole_program_and_leaves_the_others_to_it()
    {
        ProgramSetup whole = Rule("whole"), chat = Rule("chat", title: "chat");
        Assert.Same(chat, Choose([whole, chat], title: "Team chat"));
        Assert.Same(whole, Choose([whole, chat], title: "Inbox"));
    }

    [Fact]
    public void A_class_must_match_exactly()
    {
        ProgramSetup dialog = Rule("dialog", cls: "#32770");
        Assert.Same(dialog, Choose([dialog], cls: "#32770"));
        Assert.Null(Choose([dialog], cls: "#327700"));
    }

    [Fact]
    public void A_rule_for_the_file_path_comes_before_one_for_the_program_name()
    {
        ProgramSetup byName = Rule("name", title: "chat"), byPath = Rule("path", path: @"C:\App\app.exe");
        Assert.Same(byPath, Choose([byName, byPath], title: "Team chat"));
        Assert.Same(byName, Choose([byName, byPath], title: "Team chat", path: @"D:\Elsewhere\app.exe"));
    }

    [Fact]
    public void A_rule_that_is_off_never_counts()
    {
        ProgramSetup off = Rule("off", title: "chat", off: true), whole = Rule("whole");
        Assert.Same(whole, Choose([off, whole], title: "Team chat"));
        Assert.Null(Choose([off], title: "Team chat"));
    }

    [Fact]
    public void Only_a_rule_that_changes_something_is_worth_keeping()
    {
        Assert.False(new ProgramSetup { Program = "a" }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "a", Place = WinScreen.Windows.WindowAnchor.TopRight }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "a", Display = @"\\.\DISPLAY2" }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "a", Borderless = true }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "a", Anchor = true }.ChangesAnything);
        Assert.True(new ProgramSetup { Program = "a", NoToolbar = true }.ChangesAnything);
        Assert.False(new ProgramSetup { Program = "a", TitleContains = "chat", EveryTime = true }.ChangesAnything);
    }

    [Fact]
    public void A_rule_survives_saving_and_loading()
    {
        var settings = new AppSettings();
        settings.SavedSetups.Add(new ProgramSetup
        {
            Program = "app", TitleContains = "chat", WindowClass = "AppWindow", Place = WinScreen.Windows.WindowAnchor.TopRight,
            Display = @"\\.\DISPLAY2", Borderless = true, Anchor = true, NoToolbar = true, EveryTime = true, Off = true, Pinned = true,
        });
        ProgramSetup loaded = Assert.Single(AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(settings)).SavedSetups);
        Assert.Equal(("chat", "AppWindow", WinScreen.Windows.WindowAnchor.TopRight, @"\\.\DISPLAY2"),
            (loaded.TitleContains, loaded.WindowClass, loaded.Place, loaded.Display));
        Assert.True(loaded.Borderless && loaded.Anchor && loaded.NoToolbar && loaded.EveryTime && loaded.Off && loaded.Pinned);
    }
}
