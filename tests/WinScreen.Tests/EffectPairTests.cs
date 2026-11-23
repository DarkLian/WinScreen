using System.Reflection;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Window changes two and three at a time, each order, with the owner's switches: nothing escapes as a crash, each
// change that took is still ticked once the other is on, and Restore window takes everything away and puts the window
// back as it was. StateMatrixTests covers the cells of the compatibility tables one by one; this covers the pairs.
[Trait("Category", "RealWindows")]
public sealed class EffectPairTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly AppCommand[] Changes =
    [
        AppCommand.TogglePin, AppCommand.OpacityDown, AppCommand.ToggleClickThrough, AppCommand.ToggleDark, AppCommand.ToggleGlass,
        AppCommand.ToggleRollUp, AppCommand.ToggleBorderless, AppCommand.ToggleDarkTitleBar, AppCommand.ToggleAnchor,
        AppCommand.ToggleShrink, AppCommand.OpenPip, AppCommand.ToggleCrop,
    ];

    public static TheoryData<AppCommand> Firsts() => [.. Changes];

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

    [WgcTheory]
    [MemberData(nameof(Firsts))]
    public void Two_changes_each_order_stay_ticked_and_Restore_window_takes_both_away(AppCommand first)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-pairs-").FullName;
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8,1E64C8", "Effect pairs");
        target.UseFrame();
        var problems = new List<string>();
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = true, ShowNotifications = false, WelcomeShown = true, ModifierDrag = true, WindowMenu = true,
                    ExperimentalFeatures = true, HighlightPinned = true, PipViews = new PipViewPreferences { KeepHiddenOnNewViews = false } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                WindowController windows = Field<WindowController>(app, "_windows");
                nint window = target.Handle;
                string status() => Field<string>(app, "_lastStatus");
                bool On(AppCommand command) => (bool)isOn.Invoke(app, [command, window])!;
                // Crop asks for a part on screen; the part is given here, as the overlay would.
                void Do(AppCommand command)
                {
                    try
                    {
                        if (command == AppCommand.ToggleCrop) windows.Crop(window, new Rectangle(0, 0, 300, 200));
                        else run.Invoke(app, [command, window]);
                    }
                    catch (WindowOperationException refused) { output.WriteLine($"  {command} refused: {refused.Message}"); }
                    catch (TargetInvocationException failure) { problems.Add($"{command}: escaped {failure.InnerException}"); }
                    pump(500);
                }
                bool Ticks(AppCommand command) => command switch
                {
                    AppCommand.OpacityDown => windows.OpacityOf(window) < 255,
                    AppCommand.ToggleCrop => windows.IsCropped(window),
                    _ => On(command),
                };
                try
                {
                    Look start = LookOf(window);
                    foreach (AppCommand second in Changes.Where(each => each != first))
                    {
                        Do(first);
                        bool firstTook = Ticks(first);
                        Do(second);
                        bool secondTook = Ticks(second);
                        bool firstStill = Ticks(first);
                        string line = $"{first} ({firstTook}) then {second} ({secondTook}): first still {firstStill}, said '{status()}'";
                        output.WriteLine(line);
                        // A second change may take the first's place (a tile or view hides, Shrink and a view replace each
                        // other); then it says so, and the first's tick goes with it.
                        if (firstTook && secondTook && !firstStill && !Replaces(second, first))
                            problems.Add("the first's tick went: " + line);

                        Run(AppCommand.RestoreWindow);
                        Look back = LookOf(window);
                        if (back != start) problems.Add($"{first} then {second}: Restore window left {back}, was {start}");
                        foreach (AppCommand each in Changes.Where(each => each != AppCommand.OpacityDown && each != AppCommand.ToggleCrop))
                            if (On(each)) problems.Add($"{first} then {second}: {each} still ticked after Restore window");
                        if (windows.IsCropped(window) || windows.OpacityOf(window) < 255) problems.Add($"{first} then {second}: crop or opacity left");
                    }
                }
                finally { app.ExitThread(); pump(300); }

                void Run(AppCommand command) { run.Invoke(app, [command, window]); pump(600); }
            });
        }
        finally { Directory.Delete(folder, true); }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    public static TheoryData<AppCommand, AppCommand, AppCommand> Triples() => new()
    {
        { AppCommand.TogglePin, AppCommand.ToggleRollUp, AppCommand.ToggleBorderless },
        { AppCommand.ToggleGlass, AppCommand.ToggleCrop, AppCommand.TogglePin },
        { AppCommand.ToggleDark, AppCommand.TogglePin, AppCommand.ToggleClickThrough },
        { AppCommand.OpenPip, AppCommand.ToggleGlass, AppCommand.TogglePin },
        { AppCommand.ToggleBorderless, AppCommand.ToggleDark, AppCommand.OpacityDown },
        { AppCommand.ToggleAnchor, AppCommand.ToggleRollUp, AppCommand.ToggleCrop },
    };

    // Three at a time, in each of the six orders.
    [WgcTheory]
    [MemberData(nameof(Triples))]
    public void Three_changes_each_order_and_Restore_window_takes_them_all_away(AppCommand a, AppCommand b, AppCommand c)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-triples-").FullName;
        using var target = new TargetProcess(new Rectangle(300, 220, 460, 320), "E8E8E8,1E64C8", "Effect triples");
        target.UseFrame();
        var problems = new List<string>();
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = true, ShowNotifications = false, WelcomeShown = true, ModifierDrag = true, WindowMenu = true,
                    ExperimentalFeatures = true, HighlightPinned = true, PipViews = new PipViewPreferences { KeepHiddenOnNewViews = false } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                WindowController windows = Field<WindowController>(app, "_windows");
                nint window = target.Handle;
                void Do(AppCommand command)
                {
                    try
                    {
                        if (command == AppCommand.ToggleCrop) windows.Crop(window, new Rectangle(0, 0, 300, 200));
                        else run.Invoke(app, [command, window]);
                    }
                    catch (WindowOperationException refused) { output.WriteLine($"  {command} refused: {refused.Message}"); }
                    catch (TargetInvocationException failure) { problems.Add($"{command}: escaped {failure.InnerException}"); }
                    pump(500);
                }
                try
                {
                    Look start = LookOf(window);
                    AppCommand[][] orders = [[a, b, c], [a, c, b], [b, a, c], [b, c, a], [c, a, b], [c, b, a]];
                    foreach (AppCommand[] order in orders)
                    {
                        foreach (AppCommand command in order) Do(command);
                        string said = Field<string>(app, "_lastStatus");
                        run.Invoke(app, [AppCommand.RestoreWindow, window]);
                        pump(700);
                        Look back = LookOf(window);
                        string line = $"{string.Join(" then ", order)}: said '{said}'";
                        output.WriteLine(line);
                        if (back != start) problems.Add($"{line}: Restore window left {back}, was {start}");
                        foreach (AppCommand each in Changes.Where(each => each != AppCommand.OpacityDown && each != AppCommand.ToggleCrop))
                            if ((bool)isOn.Invoke(app, [each, window])!) problems.Add($"{line}: {each} still ticked after Restore window");
                        if (windows.IsCropped(window) || windows.OpacityOf(window) < 255) problems.Add($"{line}: crop or opacity left");
                    }
                }
                finally { app.ExitThread(); pump(300); }
            });
        }
        finally { Directory.Delete(folder, true); }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // Which second change takes the first's place by design: hiding the window behind a tile or view ends what needs it
    // on screen, and the two kinds of hiding replace each other.
    private static bool Replaces(AppCommand second, AppCommand first) => (second, first) switch
    {
        (AppCommand.ToggleShrink, AppCommand.OpenPip) or (AppCommand.OpenPip, AppCommand.ToggleShrink) => true,
        (AppCommand.ToggleShrink or AppCommand.OpenPip, AppCommand.ToggleDark or AppCommand.ToggleGlass) => true,
        (AppCommand.ToggleDark, AppCommand.ToggleGlass) or (AppCommand.ToggleGlass, AppCommand.ToggleDark) => true,
        _ => false,
    };
}
