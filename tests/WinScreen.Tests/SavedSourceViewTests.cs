using System.Reflection;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Owner, 22 Nov: a saved region should come back with "the exact size/location/and position". A saved source with its
// view kept the view's long side and its place as shares of the work area, clamped onto it, so a size chosen with Keep
// aspect ratio off, or a view over the taskbar, came back elsewhere; crop and options were left out. Now the view comes
// back to the pixel on the display it was saved on, with everything else about it.
[Trait("Category", "RealWindows")]
public sealed class SavedSourceViewTests
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

    [WgcTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_saved_region_opens_again_with_its_view_exactly_as_it_was(bool turned)
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-savedsource-").FullName;
        try
        {
            OnUiThread(pump =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"));
                var views = (List<PipWindow>)typeof(TrayApplication)
                    .GetField("_viewsInOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
                MethodInfo openScreen = typeof(TrayApplication).GetMethod("OpenScreenView", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo openSaved = typeof(TrayApplication).GetMethod("OpenBookmark", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    Screen screen = Screen.PrimaryScreen!;
                    var area = new Rectangle(60, 50, 360, 240);
                    openScreen.Invoke(app, [new ScreenPipSource(screen.DeviceName, area)]);
                    pump(300);
                    PipWindow first = views.Last();
                    Assert.True(Desktop.WaitFor(() => first.CanTurn, 15000, () => pump(20)), first.DiagnosticSnapshot);

                    // Aspect off, an odd size, half over the taskbar at the bottom of the display, see-through.
                    first.Overrides = first.Overrides with { KeepAspectRatio = false, OpacityPercent = 75 };
                    // Turned, a new view starts upright on Windows' thumbnail and turns once capture draws.
                    if (turned) { first.SetTurn(ViewTurn.Upright.RotatedRight); pump(500); }
                    Rectangle work = screen.WorkingArea;
                    var placed = new Rectangle(work.Left + 137, work.Bottom - 61, 413, 129);
                    first.PlaceExactly(placed);
                    pump(1500); // Long enough for capture to have settled and any refit to have happened.
                    Assert.Equal(placed, first.Bounds);

                    PipSourceBookmark saved = first.SourceBookmark("Exact region", withView: true)!;
                    first.Close();
                    pump(300);
                    // Through the settings file, as it is opened another day.
                    settings.PipBookmarks = [saved];
                    settings.Save(Path.Combine(folder, "settings.json"));
                    PipSourceBookmark reread = AppSettings.Load(Path.Combine(folder, "settings.json")).PipBookmarks.Single();

                    openSaved.Invoke(app, [reread, false]);
                    pump(300);
                    PipWindow again = views.Last();
                    Assert.NotSame(first, again);
                    Assert.True(Desktop.WaitFor(() => again.CanTurn, 15000, () => pump(20)), again.DiagnosticSnapshot);
                    pump(1500);
                    Assert.Equal(placed, again.Bounds);
                    Assert.False(again.Overrides.KeepAspectRatio);
                    Assert.Equal(75, again.Overrides.OpacityPercent);
                    Assert.Equal(turned ? 1 : 0, again.Turn.Turns);

                    // The same area of the screen, and without the view only that is kept.
                    PipSourceBookmark areaOnly = again.SourceBookmark("Area only", withView: false)!;
                    Assert.Equal(area, areaOnly.Region!.Within(screen.Bounds.Size));
                    Assert.Null(areaOnly.View);
                    again.Close();
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
