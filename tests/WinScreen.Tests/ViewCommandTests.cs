using System.Reflection;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Owner, 22 Nov: the view's own menu had no keys. Its High and Medium actions are commands now; each acts on the view in
// front, or on the view of the window in front, a region view included, and does nothing where the menu offers nothing.
[Trait("Category", "RealWindows")]
public sealed class ViewCommandTests
{
    private const string FreezeKeys = "Ctrl+Alt+Shift+F9";

    private static void OnUiThread(Action<Action<int>> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test(ms =>
                {
                    long end = Environment.TickCount64 + ms;
                    while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
                });
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T Field<T>(TrayApplication app, string name) =>
        (T)typeof(TrayApplication).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    [WgcFact]
    public void View_keys_reach_the_view_in_front_or_the_view_of_the_window_in_front()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-viewkeys-").FullName;
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "C81E1E,1E64C8", "View keys source");
        using var other = new TargetProcess(new Rectangle(300, 560, 300, 200), "1EC864", "Window without a view");
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                Dictionary<AppCommand, string> keys = CommandCatalog.All.ToDictionary(command => command, _ => "");
                keys[AppCommand.ViewFreeze] = FreezeKeys;
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true, Hotkeys = keys };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Run(AppCommand command, nint window) { run.Invoke(app, [command, window]); pump(150); }
                bool On(AppCommand command, nint window) => (bool)isOn.Invoke(app, [command, window])!;
                try
                {
                    Run(AppCommand.OpenPip, target.Handle);
                    PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => pump(20)), view.DiagnosticSnapshot);

                    // From the original's handle (the hotkey's target when the view is in front) and from the view's own.
                    bool hidden = view.KeepsUpdating;
                    Run(AppCommand.ViewKeepHidden, target.Handle);
                    Assert.Equal(!hidden, view.KeepsUpdating);
                    Assert.Equal(!hidden, On(AppCommand.ViewKeepHidden, target.Handle));
                    Run(AppCommand.ViewKeepHidden, view.Handle);
                    Assert.Equal(hidden, view.KeepsUpdating);

                    // Shown, its picture may have come from Windows' thumbnail, which can't be turned (the menu greys it).
                    Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => pump(20)), view.DiagnosticSnapshot);
                    Run(AppCommand.ViewRotateRight, view.Handle);
                    Assert.Equal(1, view.RequestedTurn.Turns);
                    Run(AppCommand.ViewUpright, target.Handle);
                    Assert.True(view.RequestedTurn.IsUpright);

                    Run(AppCommand.ViewFreeze, target.Handle);
                    Assert.True(view.Frozen && On(AppCommand.ViewFreeze, target.Handle));
                    Run(AppCommand.ViewFreeze, target.Handle);
                    Assert.False(view.Frozen);

                    // The keys set are shown beside the item in the view's menu.
                    view.ContextMenuStrip!.Show(view, new Point(20, 20));
                    view.ContextMenuStrip.Close();
                    ToolStripMenuItem freeze = view.ContextMenuStrip.Items.OfType<ToolStripMenuItem>()
                        .Single(item => item.Text == Localization.UiText.T("pip.freeze"));
                    Assert.Contains(FreezeKeys, freeze.ShortcutKeyDisplayString ?? "");

                    // A window with no view: nothing happens to any view.
                    Run(AppCommand.ViewFreeze, other.Handle);
                    Assert.False(view.Frozen);

                    // Minimized, the view is never in front: the key brings back the one minimized last.
                    Run(AppCommand.ViewMinimize, target.Handle);
                    Assert.True(view.IsSuspended);
                    Run(AppCommand.ViewMinimize, other.Handle);
                    Assert.True(Desktop.WaitFor(() => !view.IsSuspended, 3000, () => pump(20)), "The key didn't bring the view back.");

                    // A region view is no window's view: in front, it is the target itself.
                    Screen screen = Screen.PrimaryScreen!;
                    typeof(TrayApplication).GetMethod("OpenScreenView", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app, [new ScreenPipSource(screen.DeviceName, new Rectangle(40, 40, 320, 200))]);
                    pump(200);
                    PipWindow region = Field<List<PipWindow>>(app, "_viewsInOrder").Last();
                    Assert.NotSame(view, region);
                    Assert.True(Desktop.WaitFor(() => region.CanTurn, 15000, () => pump(20)), region.DiagnosticSnapshot);
                    Run(AppCommand.ViewRotateLeft, region.Handle);
                    Assert.Equal(3, region.RequestedTurn.Turns);
                    Assert.True(view.RequestedTurn.IsUpright, "The window's view turned too.");
                    Run(AppCommand.ViewKeepHidden, region.Handle); // It has no original: nothing to do.
                    Assert.False(region.KeepsUpdating);

                    // A click-through view takes no click and never comes to the front: Turn off click-through everywhere
                    // reaches it.
                    view.Overrides = view.Overrides with { ClickThrough = true };
                    region.Overrides = region.Overrides with { ClickThrough = true };
                    Run(AppCommand.ClickThroughOffEverywhere, 0);
                    Assert.False(view.Overrides.ClickThrough || region.Overrides.ClickThrough, "A view stayed click-through.");

                    Assert.Equal(PipStateProblem.None, view.FirstStateProblem);
                    Assert.Equal(0, view.UnexpectedModes);
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
