using System.Reflection;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>A rule (roadmap P1-5) acts on the windows it is for, and only on them.</summary>
[Trait("Category", "RealWindows")]
public sealed class RuleWindowTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetWindowText(nint window, string text);

    private static void Apply(TrayApplication app, nint window) =>
        typeof(TrayApplication).GetMethod("ApplySavedSetup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [window]);

    private static bool ToolbarOffered(TrayApplication app, nint window) =>
        (bool)typeof(TrayApplication).GetMethod("CanUseToolbar", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [window])!;

    [Fact]
    public void A_rule_for_some_titles_pins_places_and_frees_only_those_windows_and_keeps_the_toolbar_off_them()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-rules-").FullName;
        using var chat = new TargetProcess(new Rectangle(300, 300, 420, 300), title: "WinScreen test target - chat");
        using var inbox = new TargetProcess(new Rectangle(360, 360, 420, 300), title: "WinScreen test target - inbox");
        chat.UseFrame(); // A real frame, for Borderless to take away.
        inbox.UseFrame();
        Desktop.GainForegroundRights();
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings
                {
                    ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
                    SavedSetups = [new ProgramSetup
                    {
                        Program = "WinScreen.TestTarget", TitleContains = "CHAT", Pinned = true, Place = WindowAnchor.TopRight,
                        Borderless = true, NoToolbar = true,
                    }],
                };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                try
                {
                    Rectangle inboxBefore = Desktop.BoundsOf(inbox.Handle);
                    Desktop.Activate(chat.Handle);
                    Apply(app, chat.Handle);
                    Desktop.Activate(inbox.Handle);
                    Apply(app, inbox.Handle);

                    Assert.True(controller.IsPinned(chat.Handle), "The chat window wasn't pinned.");
                    Assert.True(controller.IsBorderless(chat.Handle), "The chat window wasn't made borderless.");
                    Assert.True(WindowInfo.TryGetFrameBounds(chat.Handle, out Rectangle frame, out _));
                    Rectangle area = Screen.FromHandle(chat.Handle).WorkingArea;
                    Assert.True(Math.Abs(frame.Right - area.Right) <= 2 && Math.Abs(frame.Top - area.Top) <= 2,
                        $"Not at the top right: {frame} in {area}.");
                    Assert.False(ToolbarOffered(app, chat.Handle), "The toolbar still offers itself on the chat window.");

                    Assert.False(controller.IsPinned(inbox.Handle), "The inbox window was pinned.");
                    Assert.False(controller.IsBorderless(inbox.Handle), "The inbox window lost its frame.");
                    Assert.Equal(inboxBefore, Desktop.BoundsOf(inbox.Handle));
                }
                finally { app.ExitThread(); }
            });
            Assert.True((Desktop.ExStyle(chat.Handle) & 0x8) == 0, "Exit left the chat window pinned.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Save(TrayApplication app, AppSettings settings) =>
        Assert.Empty((System.Collections.IEnumerable)typeof(TrayApplication)
            .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(AppSettings)])!.Invoke(app, [settings])!);

    [Fact]
    public void A_rule_changed_turned_off_or_removed_in_Settings_reaches_a_window_already_open()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-rules-").FullName;
        using var target = new TargetProcess(new Rectangle(300, 300, 420, 300), title: "WinScreen test target - rule change");
        using var other = new TargetProcess(new Rectangle(380, 380, 420, 300), title: "WinScreen test target - other");
        target.UseFrame();
        Desktop.GainForegroundRights();
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                AppSettings With(params ProgramSetup[] rules) => new()
                {
                    ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
                    SavedSetups = [.. rules],
                };
                ProgramSetup Rule(bool noToolbar, bool pinned = false, bool off = false) => new()
                {
                    Program = "WinScreen.TestTarget", TitleContains = "rule change", NoToolbar = noToolbar, Pinned = pinned, Off = off,
                };
                using var app = new TrayApplication(controller, With(Rule(noToolbar: true)), Path.Combine(folder, "settings.json"));
                void ComeForward(nint window)
                {
                    Desktop.Activate(window);
                    Apply(app, window);
                }
                try
                {
                    ComeForward(target.Handle);
                    Assert.False(ToolbarOffered(app, target.Handle), "No toolbar wasn't applied.");

                    // Changed: the toolbar at once, the rest when the window comes forward again.
                    Save(app, With(Rule(noToolbar: false, pinned: true)));
                    Assert.True(ToolbarOffered(app, target.Handle), "Clearing No toolbar didn't reach the open window.");
                    Assert.False(controller.IsPinned(target.Handle), "The changed rule acted before the window came forward.");
                    ComeForward(other.Handle);
                    ComeForward(target.Handle);
                    Assert.True(controller.IsPinned(target.Handle), "The changed rule didn't act when the window came forward.");

                    Save(app, With(Rule(noToolbar: true, pinned: true)));
                    Assert.False(ToolbarOffered(app, target.Handle), "Ticking No toolbar didn't reach the open window.");

                    Save(app, With(Rule(noToolbar: true, pinned: true, off: true)));
                    Assert.True(ToolbarOffered(app, target.Handle), "Turning the rule off left the toolbar away.");

                    // On again, it is a rule the window hasn't had: it comes with the next look.
                    Save(app, With(Rule(noToolbar: true)));
                    ComeForward(target.Handle);
                    Assert.False(ToolbarOffered(app, target.Handle), "Turning the rule on again left the toolbar there.");
                    Save(app, With());
                    Assert.True(ToolbarOffered(app, target.Handle), "Removing the rule left the toolbar away.");
                    Assert.True(controller.IsPinned(target.Handle), "Removing the rule undid what it had done.");
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_window_whose_title_comes_to_match_later_gets_the_rule_then()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-rules-").FullName;
        using var target = new TargetProcess(new Rectangle(300, 300, 420, 300), title: "WinScreen test target - loading");
        target.UseFrame();
        Desktop.GainForegroundRights(); // A rule is applied to the window in front, which is what pinning needs.
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings
                {
                    ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
                    SavedSetups = [new ProgramSetup { Program = "WinScreen.TestTarget", TitleContains = "ready", Pinned = true }],
                };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                try
                {
                    Apply(app, target.Handle);
                    Assert.False(controller.IsPinned(target.Handle), "Pinned before its title matched.");
                    SetWindowText(target.Handle, "WinScreen test target - ready");
                    Desktop.Activate(target.Handle);
                    Assert.Equal("WinScreen test target - ready", WindowInfo.Title(target.Handle));
                    Assert.True(WindowInfo.IsMainWindow(target.Handle), "Not a main window.");
                    Assert.NotNull(settings.SetupForWindow(target.Handle));
                    Apply(app, target.Handle);
                    Assert.True(controller.IsPinned(target.Handle), "Not pinned once its title matched: " + typeof(TrayApplication).GetField("_lastStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app));
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
