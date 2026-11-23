using System.Reflection;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Owner, 23 Nov: Copy picture (copy only, or copy and save to a folder), Freeze a copy, Copy text (also over Glass
// with an animated background) and covers that "survive through zooming, resetting, cropping, keep hidden,
// minimizing, use it then hide again, opacity, etc. basically all settings". Pictures and text go to the test's own
// shelf: the PC's clipboard and Screenshots folder are left alone.
[Trait("Category", "RealWindows")]
public sealed class ViewPictureEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
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

    private sealed class Shelf
    {
        internal readonly List<Bitmap> Pictures = [];
        internal readonly List<string> Texts = [];
        internal PictureShelf Make() => new()
        {
            Saves = () => false, PutPicture = picture => Pictures.Add(new Bitmap(picture)), PutText = Texts.Add, ShowFile = _ => { },
        };
    }

    // Where a point of the source (from the window's visible corner) is in the view's picture, if it shows.
    private static Point? PictureSpot(PipWindow view, Point source)
    {
        Rectangle shown = Field<Rectangle>(view, "_lastShown"), whole = Field<Rectangle>(view, "_whole");
        ViewTurn turn = Field<ViewTurn>(view, "_appliedTurn");
        var surface = new Point(source.X + whole.X, source.Y + whole.Y);
        if (!shown.Contains(surface)) return null;
        return turn.ToView(new Point(surface.X - shown.X, surface.Y - shown.Y), shown.Size);
    }

    private static Point? ScreenSpot(PipWindow view, Point source)
    {
        Rectangle shown = Field<Rectangle>(view, "_lastShown"), whole = Field<Rectangle>(view, "_whole");
        var surface = new Point(source.X + whole.X, source.Y + whole.Y);
        if (!shown.Contains(surface)) return null;
        var area = (Rectangle)typeof(PipWindow).GetProperty("PictureArea", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Rectangle at = new PipMapping(area, shown, Field<ViewTurn>(view, "_appliedTurn")).ToView(new Rectangle(surface, new Size(1, 1)));
        return view.PointToScreen(new Point(at.X + at.Width / 2, at.Y + at.Height / 2));
    }

    private static Color OnScreen(Point point)
    {
        using var pixel = new Bitmap(1, 1);
        using (Graphics graphics = Graphics.FromImage(pixel)) graphics.CopyFromScreen(point, Point.Empty, new Size(1, 1));
        return pixel.GetPixel(0, 0);
    }

    private static string Map(Form view)
    {
        var map = new System.Text.StringBuilder();
        for (int y = 0; y < view.Height; y += 12)
        {
            for (int x = 0; x < view.Width; x += 8)
            {
                Color c = OnScreen(view.PointToScreen(new Point(x, y)));
                map.Append(Black(c) ? '#' : Red(c) ? 'r' : Blue(c) ? 'b' : Green(c) ? 'g' : '.');
            }
            map.AppendLine();
        }
        return map.ToString();
    }

    // Drawn and ready: by WinScreen's capture, or on Windows 10 by Windows' thumbnail for a hidden original.
    private static bool Drawn(PipWindow view) => view.CanTurn ||
        Environment.OSVersion.Version.Build < 22000 && view.KeepsUpdating && view.BackendKind == PipBackendKind.DwmThumbnail &&
        view.BackendState == PipSessionState.Compositor;

    private static bool Black(Color c) => c.R < 40 && c.G < 40 && c.B < 40;
    private static bool Red(Color c) => c.R > 150 && c.G < 90 && c.B < 90;
    private static bool Blue(Color c) => c.B > 150 && c.R < 90;
    private static bool Green(Color c) => c.G > 150 && c.R < 90;

    [WgcFact]
    public void Covers_stay_through_every_view_setting_and_every_copy_has_them()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-covers-").FullName;
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "C81E1E,1E64C8", "Cover source");
        try
        {
            OnUiThread(pump =>
            {
                Desktop.GainForegroundRights();
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true, ExperimentalFeatures = true };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                var shelf = new Shelf();
                app.Pictures = shelf.Make();
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    run.Invoke(app, [AppCommand.OpenPip, target.Handle]);
                    pump(200);
                    PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);

                    // Marked with the view's own editor, as a person drags over it.
                    view.Run(AppCommand.ViewCover);
                    pump(200);
                    KeptAreaOverlay editor = view.CoverEditor ?? throw new InvalidOperationException("No cover editor.");
                    var area = (Rectangle)typeof(PipWindow).GetProperty("PictureArea", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                    Rectangle shown = Field<Rectangle>(view, "_lastShown"), whole = Field<Rectangle>(view, "_whole");
                    var wanted = new Rectangle(40, 60, 80, 50);
                    Rectangle inEditor = new PipMapping(area, shown).ToView(wanted with { X = wanted.X + whole.X, Y = wanted.Y + whole.Y });
                    editor.Choose(inEditor with { X = inEditor.X - area.X, Y = inEditor.Y - area.Y }, Point.Empty);
                    typeof(KeptAreaOverlay).GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [true]);
                    pump(300);
                    Rectangle cover = Assert.Single(view.Covers);
                    Assert.True(Math.Abs(cover.X - wanted.X) <= 3 && Math.Abs(cover.Width - wanted.Width) <= 6, $"Marked {cover}, wanted {wanted}.");
                    Assert.True(view.IsOn(AppCommand.ViewCover));
                    var middle = new Point(cover.X + cover.Width / 2, cover.Y + cover.Height / 2);
                    Point red = new(cover.Right + 40, 160), blue = new(330, 160);

                    void Check(string when, bool screen = true, Func<Color, bool>? left = null, Func<Color, bool>? right = null)
                    {
                        pump(400);
                        Assert.Equal([cover], view.Covers.Where(each => each == cover));
                        int before = shelf.Pictures.Count;
                        view.Run(AppCommand.ViewCopyPicture);
                        Assert.True(Desktop.WaitFor(() => shelf.Pictures.Count > before, 8000, () => pump(20)), $"{when}: no picture copied.");
                        Bitmap picture = shelf.Pictures[^1];
                        int checkedSpots = 0;
                        foreach ((Point spot, Func<Color, bool> looks, string name) in new[] { (middle, (Func<Color, bool>)Black, "the cover"),
                                     (red, left ?? Red, "the left half"), (blue, right ?? Blue, "the right half") })
                        {
                            if (PictureSpot(view, spot) is not { } at) continue;
                            checkedSpots++;
                            Color found = picture.GetPixel(at.X, at.Y);
                            Assert.True(looks(found), $"{when}: the copy shows {found} at {name} ({at} in a {picture.Size} picture).");
                        }
                        Assert.True(PictureSpot(view, middle) is not null, $"{when}: the cover isn't in the view at all.");
                        if (screen && ScreenSpot(view, middle) is { } onScreen && !Black(OnScreen(onScreen)))
                            Assert.Fail( $"{when}: the view shows {OnScreen(onScreen)} over the cover ({view.BackendKind}) at {onScreen}, view {view.Bounds}, shown {Field<Rectangle>(view, "_lastShown")}, whole {Field<Rectangle>(view, "_whole")}, turn {Field<ViewTurn>(view, "_appliedTurn")}, covers {string.Join(" ", view.Covers)}{Environment.NewLine}{Map(view)}");
                        output.WriteLine($"{when}: {checkedSpots} spots right, {view.BackendKind}, picture {picture.Size}");
                    }

                    Check("marked");
                    view.ZoomAt(new PointF(0.4f, 0.4f), 1.3);
                    Check("zoomed");
                    view.Run(AppCommand.ViewResetZoom);
                    Check("zoom reset");
                    view.SetCrop(new Rectangle(30, 50, 250, 180));
                    Check("cropped");
                    view.Run(AppCommand.ViewResetZoom);
                    Check("crop reset");

                    view.Run(AppCommand.ViewKeepHidden);
                    Check("keep hidden switched");
                    view.Run(AppCommand.ViewKeepHidden);
                    Check("keep hidden switched back");
                    if (!view.KeepsUpdating) view.Run(AppCommand.ViewKeepHidden);
                    Assert.True(view.KeepsUpdating);
                    view.Run(AppCommand.ViewUseOriginal);
                    pump(500);
                    view.Run(AppCommand.ViewKeepHidden);
                    Assert.True(Desktop.WaitFor(() => view.KeepsUpdating, 3000, () => pump(20)));
                    Check("used, then hidden again");

                    view.Run(AppCommand.ViewInteractive);
                    Check("interactive");
                    view.Run(AppCommand.ViewInteractive);

                    view.Overrides = view.Overrides with { OpacityPercent = 50 };
                    Check("at half opacity", screen: false);
                    view.Overrides = view.Overrides with { OpacityPercent = null };

                    view.Run(AppCommand.ViewMinimize);
                    pump(300);
                    Assert.True(view.IsSuspended);
                    // The key brings a minimized view back to copy it.
                    Check("copied while minimized", screen: false);
                    Assert.False(view.IsSuspended);
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);
                    Check("restored");

                    // Windows 10 draws a hidden original's view with its thumbnail, which can't be frozen or turned.
                    if (!view.CanTurn) output.WriteLine("Freeze and turning: not offered here (Windows' thumbnail).");
                    else
                    {
                        view.Run(AppCommand.ViewFreeze);
                        Assert.True(view.Frozen);
                        target.SetColors("1EC864");
                        pump(400);
                        Check("frozen, the window changed");
                        var second = new Rectangle(cover.Right + 20, 140, 40, 40);
                        view.SetCovers([cover, second]);
                        pump(300);
                        Assert.True(Black(Copy(new Point(second.X + second.Width / 2, second.Y + second.Height / 2))), "A cover added while frozen isn't in the copy.");
                        view.SetCovers([cover]);
                        pump(300);
                        Assert.True(Red(Copy(new Point(second.X + second.Width / 2, second.Y + second.Height / 2))), "A cover taken away while frozen left the frozen picture black.");
                        view.Run(AppCommand.ViewFreeze);
                        Check("live again", left: Green, right: Green);
                        target.SetColors("C81E1E,1E64C8");

                        view.Run(AppCommand.ViewRotateRight);
                        Assert.True(Desktop.WaitFor(() => Field<ViewTurn>(view, "_appliedTurn").Turns == 1, 5000, () => pump(20)));
                        Check("turned");
                        view.Run(AppCommand.ViewMirrorAcross);
                        Assert.True(Desktop.WaitFor(() => Field<ViewTurn>(view, "_appliedTurn").Mirrored, 5000, () => pump(20)));
                        Check("turned and mirrored");
                        view.Run(AppCommand.ViewUpright);
                        Assert.True(Desktop.WaitFor(() => Field<ViewTurn>(view, "_appliedTurn").IsUpright, 5000, () => pump(20)));
                    }

                    view.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
                    Assert.True(Desktop.WaitFor(() => view.BackendKind == PipBackendKind.DwmThumbnail, 5000, () => pump(20)));
                    Check("drawn by Windows' thumbnail");
                    view.ApplyPipPreferences(new PipPreferences());
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);
                    Check("captured again");
                    view.RetryCapture();
                    Check("after trying capture again");

                    // A frozen copy has them too, and keeps them whatever happens to the view after.
                    int stills = Application.OpenForms.OfType<StillPicture>().Count();
                    view.Run(AppCommand.ViewFreezeCopy);
                    Assert.True(Desktop.WaitFor(() => Application.OpenForms.OfType<StillPicture>().Count() > stills, 8000, () => pump(20)));
                    StillPicture still = Application.OpenForms.OfType<StillPicture>().Last();
                    view.SetCovers([]);
                    using (Bitmap kept = still.ShownPicture())
                    {
                        Point at = PictureSpot(view, middle)!.Value;
                        Assert.True(Black(kept.GetPixel(at.X, at.Y)), "The frozen copy lost its cover.");
                    }
                    still.CopyPicture();
                    Assert.True(Black(shelf.Pictures[^1].GetPixel(PictureSpot(view, middle)!.Value.X, PictureSpot(view, middle)!.Value.Y)));
                    still.Close();
                    Assert.True(Red(Copy(middle)), "Uncovered, the view's copy is still black there.");

                    Color Copy(Point spot)
                    {
                        int before = shelf.Pictures.Count;
                        view.Run(AppCommand.ViewCopyPicture);
                        Assert.True(Desktop.WaitFor(() => shelf.Pictures.Count > before, 8000, () => pump(20)), "No picture copied.");
                        Point at = PictureSpot(view, spot) ?? throw new InvalidOperationException("Not in the view.");
                        return shelf.Pictures[^1].GetPixel(at.X, at.Y);
                    }
                }
                finally
                {
                    foreach (StillPicture still in Application.OpenForms.OfType<StillPicture>().ToArray()) still.Close();
                    app.ExitThread();
                    pump(200);
                    foreach (Bitmap picture in shelf.Pictures) picture.Dispose();
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    // Windows' thumbnail draws the view in pieces around the covers; the view's own black shows there.
    [WgcFact]
    public void In_standard_mode_the_covered_part_is_never_drawn()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-dwmcovers-").FullName;
        using var target = new TargetProcess(new Rectangle(240, 200, 420, 300), "C81E1E,1E64C8", "Standard cover source");
        try
        {
            OnUiThread(pump =>
            {
                Desktop.GainForegroundRights();
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Pip = new PipPreferences { Backend = PipBackendPreference.Dwm } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                app.Pictures = new Shelf().Make();
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    run.Invoke(app, [AppCommand.OpenPip, target.Handle]);
                    pump(800);
                    PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.Equal(PipBackendKind.DwmThumbnail, view.BackendKind);
                    var cover = new Rectangle(40, 60, 80, 50);
                    var middle = new Point(cover.X + cover.Width / 2, cover.Y + cover.Height / 2);
                    foreach (bool hidden in new[] { view.KeepsUpdating, !view.KeepsUpdating })
                    {
                        if (view.KeepsUpdating != hidden) view.Run(AppCommand.ViewKeepHidden);
                        view.SetCovers([cover]);
                        pump(500);
                        object selector = Field<object>(view, "_thumbnail");
                        object? dwm = selector.GetType().GetField("_dwm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selector);
                        object pieces = dwm!.GetType().GetField("_thumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dwm)!;
                        int count = ((System.Collections.ICollection)pieces.GetType().GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pieces)!).Count;
                        var map = new System.Text.StringBuilder();
                        for (int y = 0; y < view.Height; y += 12)
                        {
                            for (int x = 0; x < view.Width; x += 8)
                            {
                                Color c = OnScreen(view.PointToScreen(new Point(x, y)));
                                map.Append(Black(c) ? '#' : Red(c) ? 'r' : Blue(c) ? 'b' : '.');
                            }
                            map.AppendLine();
                        }
                        output.WriteLine($"hidden {hidden}, {count} thumbnails, {view.BackendKind}, shown {Field<Rectangle>(view, "_lastShown")}, whole {Field<Rectangle>(view, "_whole")}");
                        output.WriteLine(map.ToString());
                        Point onScreen = ScreenSpot(view, middle)!.Value;
                        Assert.True(Black(OnScreen(onScreen)), $"hidden {hidden}: {OnScreen(onScreen)} over the cover");
                        Point red = ScreenSpot(view, new Point(cover.Right + 40, 160))!.Value;
                        Assert.True(Red(OnScreen(red)), $"hidden {hidden}: {OnScreen(red)} beside the cover");
                        view.SetCovers([]);
                        pump(500);
                        Assert.True(Red(OnScreen(onScreen)), $"hidden {hidden}: uncovered, {OnScreen(onScreen)}");
                    }

                    // From Smooth capture to Windows' thumbnail, as a change in Settings or a fallback does.
                    view.ApplyPipPreferences(new PipPreferences());
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);
                    view.SetCovers([cover]);
                    pump(500);
                    view.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
                    foreach (int wait in new[] { 0, 300, 1500 })
                    {
                        pump(wait);
                        var map = new System.Text.StringBuilder();
                        for (int y = 0; y < view.Height; y += 12)
                        {
                            for (int x = 0; x < view.Width; x += 8)
                            {
                                Color c = OnScreen(view.PointToScreen(new Point(x, y)));
                                map.Append(Black(c) ? '#' : Red(c) ? 'r' : Blue(c) ? 'b' : '.');
                            }
                            map.AppendLine();
                        }
                        output.WriteLine($"after switching, {wait} ms more, {view.BackendKind}, {view.BackendState}");
                        output.WriteLine(map.ToString());
                    }
                    Point spot = ScreenSpot(view, middle)!.Value;
                    Assert.True(Black(OnScreen(spot)), $"switched to Windows' thumbnail: {OnScreen(spot)} over the cover");
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [WgcFact]
    public void Copy_text_reads_the_part_dragged_over_or_all_of_it_and_never_a_covered_part()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-copytext-").FullName;
        using var target = new TargetProcess(new Rectangle(240, 200, 520, 300), "F0F0F0", "Text source");
        target.ShowText("Pictures 2468 Kiwi");
        try
        {
            OnUiThread(pump =>
            {
                Desktop.GainForegroundRights();
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                var shelf = new Shelf();
                app.Pictures = shelf.Make();
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    run.Invoke(app, [AppCommand.OpenPip, target.Handle]);
                    pump(200);
                    PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);

                    string Read(Rectangle part)
                    {
                        int before = shelf.Texts.Count;
                        view.Run(AppCommand.ViewCopyText);
                        pump(200);
                        ViewAreaPicker picker = view.AreaPicker ?? throw new InvalidOperationException("No area picker.");
                        picker.Choose(part);
                        Assert.True(Desktop.WaitFor(() => shelf.Texts.Count > before || view.SaidOnView.Length > 0, 15000, () => pump(20)),
                            "Nothing was read.");
                        Desktop.WaitFor(() => shelf.Texts.Count > before, 1000, () => pump(20));
                        string text = shelf.Texts.Count > before ? shelf.Texts[^1] : "";
                        output.WriteLine($"{part}: \"{text}\" ({view.SaidOnView})");
                        return text;
                    }

                    string all = Read(Rectangle.Empty);
                    Assert.Contains("Pictures", all);
                    Assert.Contains("2468", all);

                    // The left part of the words only: a third of the view's width.
                    var area = (Rectangle)typeof(PipWindow).GetProperty("PictureArea", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                    string left = Read(new Rectangle(0, 0, area.Width / 3, area.Height / 3));
                    Assert.Contains("Pictures", left);
                    Assert.DoesNotContain("Kiwi", left);

                    // Covered, a word can't be read from the view.
                    Rectangle whole = Field<Rectangle>(view, "_whole");
                    view.SetCovers([new Rectangle(0, 0, whole.Width, whole.Height / 2)]);
                    pump(400);
                    string covered = Read(Rectangle.Empty);
                    Assert.DoesNotContain("2468", covered);
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    // Owner: "Make sure it survives through glass mode with animated background." A window with Glass can't have a
    // view of its own (opening one turns Glass off), so this is a region view over it: what it reads is the screen,
    // Glass and the moving background behind the text included. Glass stays on.
    [WgcFact]
    public void Copy_text_reads_through_glass_with_an_animated_background()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-glasstext-").FullName;
        string gif = Path.Combine(folder, "busy.gif");
        AnimatedGif.Write(gif, 160, 120, 4);
        using var target = new TargetProcess(new Rectangle(200, 160, 560, 320), "FFFFFF", "Glass text source");
        target.ShowText("Glass reads 7531 Plum");
        try
        {
            OnUiThread(pump =>
            {
                Desktop.GainForegroundRights();
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings
                {
                    ShowToolbar = false, ShowNotifications = false, WelcomeShown = true, ExperimentalFeatures = true,
                    Backgrounds = new BackgroundPreferences { Everywhere = new BackgroundChoice { File = gif } },
                };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                var shelf = new Shelf();
                app.Pictures = shelf.Make();
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo isOn = typeof(TrayApplication).GetMethod("IsOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool GlassOn() => (bool)isOn.Invoke(app, [AppCommand.ToggleGlass, target.Handle])!;
                try
                {
                    Desktop.Activate(target.Handle);
                    run.Invoke(app, [AppCommand.ToggleGlass, target.Handle]);
                    if (!Desktop.WaitFor(GlassOn, 8000, () => pump(20)))
                    {
                        // Glass needs a graphics card, which the test VMs don't have.
                        Skipped.Because("Glass didn't come on here (no graphics card?).");
                        return;
                    }
                    pump(1500);

                    User32Bounds(target.Handle, out Rectangle frame);
                    Screen screen = Screen.FromRectangle(frame);
                    var region = new Rectangle(frame.X - screen.Bounds.X, frame.Y - screen.Bounds.Y, frame.Width, Math.Min(frame.Height, 200));
                    typeof(TrayApplication).GetMethod("OpenScreenView", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app, [new ScreenPipSource(screen.DeviceName, region)]);
                    pump(300);
                    PipWindow view = Field<List<PipWindow>>(app, "_viewsInOrder").Last();
                    Assert.True(Desktop.WaitFor(() => Drawn(view), 15000, () => pump(20)), view.DiagnosticSnapshot);
                    pump(500);

                    int before = shelf.Texts.Count;
                    view.Run(AppCommand.ViewCopyText);
                    pump(200);
                    (view.AreaPicker ?? throw new InvalidOperationException("No area picker.")).Choose(Rectangle.Empty);
                    Assert.True(Desktop.WaitFor(() => shelf.Texts.Count > before || view.SaidOnView.Length > 0, 20000, () => pump(20)), "Nothing was read.");
                    Desktop.WaitFor(() => shelf.Texts.Count > before, 1000, () => pump(20));
                    string text = shelf.Texts.Count > before ? shelf.Texts[^1] : "";
                    output.WriteLine($"Read through Glass: \"{text}\" ({view.SaidOnView})");
                    Assert.Contains("7531", text);
                    Assert.Contains("Plum", text);
                    Assert.True(GlassOn(), "Glass went off.");
                    view.Close();
                    run.Invoke(app, [AppCommand.ToggleGlass, target.Handle]);
                    pump(300);
                }
                finally { app.ExitThread(); pump(200); }
            });
        }
        finally { Directory.Delete(folder, true); }

        static void User32Bounds(nint window, out Rectangle bounds) =>
            WinScreen.Windows.WindowInfo.TryGetFrameBounds(window, out bounds, out _);
    }
}

// A small looping GIF of moving coloured blocks: a busy background for Glass.
internal static class AnimatedGif
{
    internal static void Write(string path, int width, int height, int frames)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write("GIF89a"u8);
        file.Write((ushort)width); file.Write((ushort)height);
        file.Write((byte)0xF7); file.Write((byte)0); file.Write((byte)0); // 256-colour global table.
        var random = new Random(7);
        for (int i = 0; i < 256; i++)
        {
            file.Write((byte)random.Next(40, 230)); file.Write((byte)random.Next(40, 230)); file.Write((byte)random.Next(40, 230));
        }
        file.Write([0x21, 0xFF, 0x0B]); file.Write("NETSCAPE2.0"u8); file.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
        for (int frame = 0; frame < frames; frame++)
        {
            file.Write([0x21, 0xF9, 0x04, 0x00]); file.Write((ushort)10); file.Write([0x00, 0x00]);
            file.Write((byte)0x2C); file.Write((ushort)0); file.Write((ushort)0); file.Write((ushort)width); file.Write((ushort)height); file.Write((byte)0);
            var pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((x / 12 + y / 12 * 7 + frame * 13) * 37 % 256);
            file.Write((byte)8);
            byte[] data = Lzw(pixels);
            for (int at = 0; at < data.Length; at += 255)
            {
                int count = Math.Min(255, data.Length - at);
                file.Write((byte)count);
                file.Write(data, at, count);
            }
            file.Write((byte)0);
        }
        file.Write((byte)0x3B);
    }

    // Every pixel as its own 9-bit code, with a clear code before the table would need 10 bits.
    private static byte[] Lzw(byte[] pixels)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        void Put(int code)
        {
            buffer |= code << bits;
            bits += 9;
            while (bits >= 8) { output.Add((byte)buffer); buffer >>= 8; bits -= 8; }
        }
        Put(256);
        int sinceClear = 0;
        foreach (byte pixel in pixels)
        {
            if (sinceClear == 250) { Put(256); sinceClear = 0; }
            Put(pixel);
            sinceClear++;
        }
        Put(257);
        if (bits > 0) output.Add((byte)buffer);
        return output.ToArray();
    }
}
