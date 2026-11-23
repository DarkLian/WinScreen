using System.Reflection;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Each view command in each state a view can be in, with the owner's switches: the key does what the view's menu
// offers there and nothing when the menu greys it, and the view's state stays one the state table allows.
[Trait("Category", "RealWindows")]
public sealed class ViewCommandMatrixTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<string> States() =>
        ["shown", "hidden", "originalMinimized", "viewMinimized", "interactive", "frozen", "turned", "clickThrough", "region"];

    // What a command changes when it does something; Go to and Use original act on the original, Mute on its sound, and
    // the picture commands copy or open a window of their own (ViewPictureEndToEndTests).
    private static readonly AppCommand[] Visible = CommandCatalog.View
        .Where(command => command is not (AppCommand.ViewGoToOriginal or AppCommand.ViewUseOriginal or AppCommand.ViewMute or
            AppCommand.ViewCopyPicture or AppCommand.ViewFreezeCopy or AppCommand.ViewCopyText or AppCommand.ViewCover or AppCommand.ViewRecord)).ToArray();

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

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static string Signature(PipWindow view) =>
        $"{view.StateDescription}|{view.RequestedTurn}|{view.IsSuspended}|{view.KeepsUpdating}|{view.IsInteractive}|{view.Frozen}";

    private static bool Offered(PipWindow view, AppCommand command)
    {
        typeof(PipWindow).GetMethod("RefreshMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, []);
        ToolStripMenuItem item = Field<Dictionary<AppCommand, ToolStripMenuItem>>(view, "_keyed")[command];
        bool offered = item.Available && item.Enabled;
        for (ToolStripItem? parent = item.OwnerItem; parent is not null; parent = parent.OwnerItem)
            offered &= parent.Available && parent.Enabled;
        return offered;
    }

    [WgcTheory]
    [MemberData(nameof(States))]
    public void Each_view_command_does_what_the_menu_offers_and_keeps_the_state_true(string state)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-viewmatrix-").FullName;
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "C81E1E,1E64C8", "View matrix source");
        var problems = new List<string>();
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = true, ShowNotifications = false, WelcomeShown = true, ModifierDrag = true, WindowMenu = true,
                    ExperimentalFeatures = true, PipMiddleClickScroll = true, HighlightPinned = true,
                    PipViews = new PipViewPreferences { HoverPreview = WinScreen.Settings.PipHoverPreview.Lens, KeepHiddenOnNewViews = false } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                app.Pictures = new PictureShelf { Saves = () => false, PutPicture = _ => { }, PutText = _ => { }, ShowFile = _ => { },
                    RecordingFolder = () => Path.Combine(folder, "recordings") };
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Run(AppCommand command, nint window) { run.Invoke(app, [command, window]); pump(150); }
                try
                {
                    foreach (AppCommand command in CommandCatalog.View)
                    {
                        PipWindow view = Open();
                        try
                        {
                            bool offered = Offered(view, command);
                            string before = Signature(view);
                            view.Run(command);
                            pump(400);
                            string after = Signature(view);
                            string line = $"{state} + {command}: offered {offered}, {before} -> {after}";
                            output.WriteLine(line);
                            if (Visible.Contains(command) && offered && before == after) problems.Add("offered but did nothing: " + line);
                            if (Visible.Contains(command) && !offered && before != after) problems.Add("greyed but acted: " + line);
                            if (view.FirstStateProblem != PipStateProblem.None || view.UnexpectedModes != 0)
                                problems.Add($"state problem {view.FirstStateProblem}, unexpected modes {view.UnexpectedModes}: {line}\n{view.DiagnosticSnapshot}");
                        }
                        finally
                        {
                            if (User32.IsIconic(target.Handle)) User32.ShowWindow(target.Handle, User32.SW_RESTORE);
                            pump(300);
                            foreach (Form opened in Application.OpenForms.OfType<Form>().Where(form => form is StillPicture or ViewAreaPicker or KeptAreaOverlay).ToArray())
                                opened.Close();
                            if (!view.IsDisposed) view.Close();
                            pump(300);
                        }
                    }
                }
                finally { app.ExitThread(); pump(200); }

                PipWindow Open()
                {
                    if (state == "region")
                    {
                        typeof(TrayApplication).GetMethod("OpenScreenView", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(app, [new ScreenPipSource(Screen.PrimaryScreen!.DeviceName, new Rectangle(40, 40, 320, 200))]);
                        pump(200);
                    }
                    else Run(AppCommand.OpenPip, target.Handle);
                    PipWindow view = state == "region"
                        ? Field<List<PipWindow>>(app, "_viewsInOrder").Last()
                        : Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => pump(20)), view.DiagnosticSnapshot);
                    switch (state)
                    {
                        case "hidden": view.Run(AppCommand.ViewKeepHidden); break;
                        case "originalMinimized":
                            User32.ShowWindow(target.Handle, User32.SW_MINIMIZE);
                            Assert.True(Desktop.WaitFor(() => view.StateDescription.Contains(Localization.UiText.T("pip.state.minimizedHidden")), 5000, () => pump(20)),
                                "The view didn't take the minimized original: " + view.StateDescription);
                            break;
                        case "viewMinimized": view.Run(AppCommand.ViewMinimize); break;
                        case "interactive": view.Run(AppCommand.ViewInteractive); break;
                        case "frozen": view.Run(AppCommand.ViewFreeze); break;
                        case "turned": view.Run(AppCommand.ViewRotateRight); break;
                        case "clickThrough": view.Overrides = view.Overrides with { ClickThrough = true }; break;
                    }
                    pump(600);
                    if (state is "turned") Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => pump(20)), view.DiagnosticSnapshot);
                    return view;
                }
            });
        }
        finally { Directory.Delete(folder, true); }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
