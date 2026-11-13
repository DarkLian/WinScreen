// Writes one settings.json the way the WinScreen version it is built against writes it, with made-up,
// non-default choices. Built once per release tag; no real person's settings are involved.
using WinScreen.Settings;

var settings = new AppSettings();

settings.Hotkeys[AppCommand.TogglePin] = "Ctrl+Shift+F9";
settings.Hotkeys[AppCommand.OpacityUp] = ""; // cleared by the person
settings.Hotkeys[AppCommand.ToggleRollUp] = "Ctrl+Alt+Shift+R"; // a command with no default key

settings.ExcludedPrograms.Add("fixture-game");
settings.IncludedPrograms.Add("fixture-included");
settings.ModifierExclusions.Add("fixture-paint");
settings.WindowMenuExclusions.Add("fixture-menu");
settings.SavedSetups.Add(new ProgramSetup
{
    Program = "fixture-editor", ExecutablePath = @"C:\Fixture\editor.exe", Pinned = true, Alpha = 200, Dark = true,
});
settings.SavedSetups.Add(new ProgramSetup { Program = "fixture-chat", Pinned = true });
typeof(AppSettings).GetProperty("PipOriginalPrograms")?.SetValue(settings, new List<string> { "fixture-java" });

settings.ShowToolbar = false;
settings.ModifierDrag = true;
settings.ModifierKey = ModifierKey.Win;
settings.WindowMenu = true;
settings.WindowMenuGesture = WindowMenuGesture.CtrlMiddleClick;
settings.DefaultOpacityPercent = 60;
settings.Language = "zh-Hans";
settings.ExperimentalFeatures = true;
settings.Toolbar = settings.Toolbar with
{
    Placement = ToolbarPlacement.Right,
    Trigger = ToolbarTrigger.MinimizeButton,
    Buttons = [AppCommand.TogglePin, AppCommand.OpenPip, AppCommand.RestoreWindow],
};
settings.Anchors = settings.Anchors with { Side = AnchorSide.Left, Automatic = true };

settings.Save(args[0]);
Console.WriteLine($"Wrote {args[0]}");
