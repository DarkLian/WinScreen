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
public sealed class ViewCommandTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // No Shift, as the owner's Ctrl+Alt+F; the owner's switches (toolbar, gestures, experimental, hover preview).
    private const string FreezeKeys = "Ctrl+Alt+J";

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

    // Owner, 22 Nov: Claude, an administrator app, made click-through by the helper; the tray of the ordinary copy is the
    // way back, so it offers what the helper holds: its click-through and pinned windows, and its views.
    [Fact]
    public void The_tray_offers_what_the_helper_holds()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-helperstate-").FullName;
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                try
                {
                    ContextMenuStrip menu = Field<NotifyIcon>(app, "_tray").ContextMenuStrip!;
                    string clickThroughOff = CommandCatalog.Name(AppCommand.ClickThroughOffEverywhere), unpin = CommandCatalog.Name(AppCommand.UnpinEverywhere);
                    ToolStripItem Item(string text) => menu.Items.Cast<ToolStripItem>().Single(item => item.Text == text);
                    void Open() { menu.Show(new Point(200, 200)); pump(100); }
                    void Close() { menu.Close(); pump(50); }

                    Open();
                    Assert.False(Item(clickThroughOff).Available || Item(unpin).Available, "Offered with nothing changed.");
                    Close();

                    MethodInfo fromHelper = typeof(TrayApplication).GetMethod("FromHelper", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var view = new HelperLink.HelperView(0x1234, 0x5678, "PiP - Claude", Suspended: true, SourceIsWindow: true,
                        ClickThrough: true, CanRetry: false, PointedAt: 1, SuspendedAt: 2);
                    fromHelper.Invoke(app, [new HelperLink.Message(HelperLink.State, Counts: [1, 1], Views: [view])]);
                    Open();
                    Assert.True(Item(clickThroughOff).Available, "Turn off click-through everywhere isn't offered for the helper's window.");
                    Assert.True(Item(unpin).Available, "Unpin everywhere isn't offered for the helper's window.");
                    var listed = (ToolStripMenuItem)Item("PiP - Claude");
                    string[] actions = listed.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text ?? "").ToArray();
                    Assert.Equal([Localization.UiText.T("pip.restoreView"), Localization.UiText.T("pip.goToOriginal"),
                        Localization.UiText.T("pip.reenableClicks"), Localization.UiText.T("common.close")], actions);
                    Close();

                    // The window last in front is an administrator app's: its ticks are what the helper says.
                    MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    fromHelper.Invoke(app, [new HelperLink.Message(HelperLink.State, Window: 0x9999, Counts: [1, 1], Views: [view],
                        On: [(int)AppCommand.TogglePin, (int)AppCommand.ToggleClickThrough])]);
                    Assert.True((bool)isOn.Invoke(app, [AppCommand.TogglePin, (nint)0x9999])!);
                    Assert.True((bool)isOn.Invoke(app, [AppCommand.ToggleClickThrough, (nint)0x9999])!);
                    Assert.False((bool)isOn.Invoke(app, [AppCommand.ToggleAnchor, (nint)0x9999])!);
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

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
                var settings = new AppSettings { ShowToolbar = true, ShowNotifications = false, WelcomeShown = true, Hotkeys = keys, ModifierDrag = true, WindowMenu = true, ExperimentalFeatures = true, PipMiddleClickScroll = true, HighlightPinned = true, PipViews = new PipViewPreferences { HoverPreview = WinScreen.Settings.PipHoverPreview.Lens, KeepHiddenOnNewViews = false } };
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

                    // Pressed for real: a view doesn't take the focus, so another window is in front, and a hidden
                    // original never is. The view under the pointer is the one meant.
                    Rectangle work = screen.WorkingArea;
                    region.Location = new Point(work.Right - region.Width - 40, work.Top + 40);
                    view.Location = new Point(work.Right - view.Width - 40, work.Bottom - view.Height - 40);
                    pump(200);
                    void PressFreezeOver(PipWindow pointed)
                    {
                        Desktop.GainForegroundRights();
                        Desktop.Activate(other.Handle);
                        pump(200);
                        // Owner, 22 Nov: clicked on a region view first, the key still found no view.
                        var middle = new Point(pointed.Left + pointed.Width / 2, pointed.Top + pointed.Height / 2);
                        Desktop.ClickOn(middle, pointed.Handle);
                        pump(1500);
                        var viewUnder = (PipWindow?)typeof(TrayApplication).GetMethod("ViewUnderPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, []);
                        output.WriteLine($"Pointer lookup found {(viewUnder == pointed ? "the view" : viewUnder is null ? "nothing" : "another view")}");
                        nint under = Native.User32.GetAncestor(Native.User32.WindowFromPoint(new Native.NativePoint { X = middle.X, Y = middle.Y }), Native.User32.GA_ROOTOWNER);
                        output.WriteLine($"Clicked: front {WindowInfo.ClassName(Native.User32.GetForegroundWindow())}, under the pointer {WindowInfo.ClassName(under)} (view {under == pointed.Handle}), at {Cursor.Position}");
                        Desktop.Press(Keys.J, Keys.ControlKey, Keys.Menu);
                    }
                    PressFreezeOver(region);
                    Assert.True(Desktop.WaitFor(() => region.Frozen, 3000, () => pump(20)), "The key didn't reach the region view under the pointer.");
                    Assert.False(view.Frozen);
                    if (!view.KeepsUpdating) Run(AppCommand.ViewKeepHidden, target.Handle);
                    Assert.True(Desktop.WaitFor(() => view.KeepsUpdating && view.Frozen == false && view.CanTurn, 15000, () => pump(20)), view.StateDescription);
                    PressFreezeOver(view);
                    Assert.True(Desktop.WaitFor(() => view.Frozen, 3000, () => pump(20)), "The key didn't reach the window's view under the pointer.");
                    Run(AppCommand.ViewFreeze, view.Handle);
                    Run(AppCommand.ViewFreeze, region.Handle);
                    Assert.False(view.Frozen || region.Frozen);

                    // With the pointer off every view, the key goes to the view it was last on.
                    PressFreezeOver(region);
                    Assert.True(Desktop.WaitFor(() => region.Frozen, 3000, () => pump(20)));
                    Run(AppCommand.ViewFreeze, region.Handle);
                    Assert.False(region.Frozen);
                    Desktop.MoveMouse(new Point(other.Bounds.Left + 30, other.Bounds.Top + 30));
                    pump(200);
                    Desktop.Press(Keys.J, Keys.ControlKey, Keys.Menu);
                    Assert.True(Desktop.WaitFor(() => region.Frozen, 3000, () => pump(20)), "The key didn't reach the view the pointer was last on.");
                    Assert.False(view.Frozen);
                    Run(AppCommand.ViewFreeze, region.Handle);

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
