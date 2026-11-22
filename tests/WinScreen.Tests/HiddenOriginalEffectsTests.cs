using System.Reflection;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Each window command on an original hidden behind its view, with the owner's switches: the view and its state stay
// sound, the original stays hidden unless the command is about showing it, nothing escapes as a crash, and Restore
// window puts the original back as it was.
[Trait("Category", "RealWindows")]
public sealed class HiddenOriginalEffectsTests(Xunit.Abstractions.ITestOutputHelper output)
{
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

    private sealed record Look(Rectangle Bounds, long Style, long ExStyle, byte? Alpha);

    private static Look LookOf(nint window) =>
        new(Desktop.BoundsOf(window), User32.GetWindowLongPtr(window, User32.GWL_STYLE), Desktop.ExStyle(window), Desktop.Alpha(window));

    [WgcFact]
    public void Each_window_command_on_a_hidden_original_keeps_the_view_sound_and_Restore_window_puts_it_back()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-hiddeneffects-").FullName;
        using var target = new TargetProcess(new Rectangle(260, 220, 460, 320), "E8E8E8,1E64C8", "Hidden original effects");
        target.UseFrame();
        var problems = new List<string>();
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = true, ShowNotifications = false, WelcomeShown = true, ModifierDrag = true, WindowMenu = true,
                    ExperimentalFeatures = true, PipMiddleClickScroll = true, HighlightPinned = true,
                    PipViews = new PipViewPreferences { HoverPreview = WinScreen.Settings.PipHoverPreview.Lens } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Run(AppCommand command, nint window) { run.Invoke(app, [command, window]); pump(500); }
                try
                {
                    Look start = LookOf(target.Handle);
                    AppCommand[] commands =
                    [
                        AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.ToggleDefaultOpacity, AppCommand.ToggleClickThrough,
                        AppCommand.ToggleDarkTitleBar, AppCommand.ToggleDark, AppCommand.ToggleGlass, AppCommand.ToggleCrop,
                        AppCommand.ToggleBorderless, AppCommand.ToggleRollUp, AppCommand.ToggleAnchor, AppCommand.AlignCenter,
                        AppCommand.SendToBottom, AppCommand.ToggleShrink, AppCommand.OpenPip,
                    ];
                    foreach (AppCommand command in commands)
                    {
                        Run(AppCommand.OpenPip, target.Handle);
                        PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                        Assert.True(Desktop.WaitFor(() => view.CanTurn && view.KeepsUpdating, 15000, () => pump(20)), view.DiagnosticSnapshot);
                        string status = "";
                        try { Run(command, target.Handle); }
                        catch (TargetInvocationException failure) { problems.Add($"{command}: escaped {failure.InnerException}"); }
                        status = Field<string>(app, "_lastStatus");
                        // Shrink and a second Open view close the view by design; everything else keeps it.
                        bool viewKept = command is not (AppCommand.ToggleShrink or AppCommand.OpenPip);
                        string line = $"{command}: view {(view.IsDisposed ? "closed" : view.StateDescription)}, original alpha {Desktop.Alpha(target.Handle)}, said '{status}'";
                        output.WriteLine(line);
                        if (viewKept && view.IsDisposed) problems.Add("closed the view: " + line);
                        if (viewKept && !view.IsDisposed && !view.KeepsUpdating) problems.Add("showed the original: " + line);
                        if (!view.IsDisposed && (view.FirstStateProblem != PipStateProblem.None || view.UnexpectedModes != 0))
                            problems.Add($"state {view.FirstStateProblem}, unexpected {view.UnexpectedModes}: {line}");

                        Run(AppCommand.RestoreWindow, target.Handle);
                        pump(500);
                        if (command == AppCommand.AlignCenter) { Run(AppCommand.RestorePlacement, target.Handle); pump(300); }
                        Look back = LookOf(target.Handle);
                        if (back != start) problems.Add($"{command}: Restore window left {back}, was {start}");
                        if (Field<Dictionary<nint, PipWindow>>(app, "_pips").ContainsKey(target.Handle)) problems.Add($"{command}: Restore window left the view open");
                    }
                }
                finally { app.ExitThread(); pump(300); }
            });
        }
        finally { Directory.Delete(folder, true); }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
