using System.Diagnostics;
using System.Reflection;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// Owner, 23 Nov: Picture > Record makes an MP4 of what the view shows: crop, zoom and turn as shown, every covered part
// black in every frame, never the screen. It stops cleanly, the file finished, however the recording ends: Stop, the
// view closing or minimized, capture starting again, WinScreen exiting. Recordings go to the test's own folder.
[Trait("Category", "RealWindows")]
public sealed class ViewRecordingEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
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

    private static bool Black(Color c) => c.R < 50 && c.G < 50 && c.B < 50;
    private static bool Red(Color c) => c.R > 140 && c.G < 100 && c.B < 100;
    private static bool Green(Color c) => c.G > 140 && c.R < 100 && c.B < 120;
    private static bool Blue(Color c) => c.B > 140 && c.R < 100;

    private sealed record Scene(TrayApplication App, PipWindow View, TargetProcess Target, string Folder, Action<int> Pump);

    // A view of a window red on the left and blue on the right, drawn by capture, recordings to a folder of the test's.
    private void WithView(Action<Scene> test, bool sound = false, Rectangle? bounds = null)
    {
        if (!ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        string folder = Directory.CreateTempSubdirectory("winscreen-recording-").FullName;
        using var target = new TargetProcess(bounds ?? new Rectangle(240, 200, 420, 300), "C81E1E,1E64C8", "Recording source");
        try
        {
            OnUiThread(pump =>
            {
                Desktop.GainForegroundRights();
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings { ShowToolbar = false, ShowNotifications = false, WelcomeShown = true, ExperimentalFeatures = true,
                    PipViews = new PipViewPreferences { KeepHiddenOnNewViews = false } };
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), startup: Setup.OwnStartup());
                app.Pictures = new PictureShelf
                {
                    Saves = () => false, PutPicture = _ => { }, PutText = _ => { }, ShowFile = _ => { },
                    RecordingFolder = () => Path.Combine(folder, "recordings"), RecordsSound = () => sound,
                };
                MethodInfo run = typeof(TrayApplication).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    run.Invoke(app, [AppCommand.OpenPip, target.Handle]);
                    pump(200);
                    PipWindow view = Field<Dictionary<nint, PipWindow>>(app, "_pips")[target.Handle];
                    Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => pump(20)), view.DiagnosticSnapshot);
                    test(new Scene(app, view, target, folder, pump));
                }
                finally
                {
                    foreach (Form opened in Application.OpenForms.OfType<Form>().Where(form => form is StillPicture or ViewAreaPicker or KeptAreaOverlay).ToArray())
                        opened.Close();
                    if (!app.Exiting) app.ExitThread();
                    pump(200);
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    private static ViewRecording Start(Scene scene)
    {
        scene.View.Run(AppCommand.ViewRecord);
        Assert.True(scene.View.IsOn(AppCommand.ViewRecord), scene.View.RecordingRefusedBecause() ?? "Recording didn't start.");
        return scene.View.ActiveRecording!;
    }

    private static RecordingResult Finished(Scene scene, ViewRecording recording)
    {
        Assert.True(Desktop.WaitFor(() => recording.Completion.IsCompleted, 20000, () => scene.Pump(20)), "The recording never finished.");
        RecordingResult result = recording.Completion.Result;
        Assert.True(result.File is not null && File.Exists(result.File), $"No file: {result}");
        return result;
    }

    // Where a point of the source (from the window's visible corner) is in a recorded frame, as the view showed it.
    private static Point FrameSpot(PipWindow view, Point source, Size frame)
    {
        Rectangle shown = Field<Rectangle>(view, "_lastShown"), whole = Field<Rectangle>(view, "_whole");
        ViewTurn turn = view.Turn;
        Point inShown = turn.ToView(new Point(source.X + whole.X - shown.X, source.Y + whole.Y - shown.Y), shown.Size);
        Rectangle box = RecordingGeometry.Letterbox(turn.Oriented(shown.Size), frame);
        Size oriented = turn.Oriented(shown.Size);
        return new Point(box.X + inShown.X * box.Width / oriented.Width, box.Y + inShown.Y * box.Height / oriented.Height);
    }

    [WgcFact]
    public void A_recording_has_the_steady_rate_the_changes_and_the_cover_black_in_every_frame()
    {
        WithView(scene =>
        {
            PipWindow view = scene.View;
            var cover = new Rectangle(280, 90, 90, 70);
            view.SetCovers([cover]);
            scene.Pump(400);
            Rectangle shown = Field<Rectangle>(view, "_lastShown");
            Size expected = RecordingGeometry.FrameSize(shown.Size);
            var clock = Stopwatch.StartNew();
            ViewRecording recording = Start(scene);
            for (int i = 0; i < 8; i++)
            {
                scene.Target.SetColors(i % 2 == 0 ? "1EC81E,1E64C8" : "C81E1E,1E64C8");
                scene.Pump(400);
            }
            scene.View.Run(AppCommand.ViewRecord);
            double seconds = clock.Elapsed.TotalSeconds;
            Assert.False(view.IsOn(AppCommand.ViewRecord));
            RecordingResult result = Finished(scene, recording);
            Assert.True(Desktop.WaitFor(() => view.LastRecording is not null, 5000, () => scene.Pump(20)));
            Assert.Equal(PipChipState.SavedFile, view.ChipShown);
            output.WriteLine($"{result}, {seconds:0.00} s, shown {shown}");

            using VideoFile video = VideoFile.Read(result.File!, index => index % 3 == 0);
            Assert.Equal(expected, video.Size);
            Assert.InRange(video.Frames, (int)(seconds * 30 * 0.7), (int)(seconds * 30) + 3);
            Point covered = FrameSpot(view, new Point(cover.X + cover.Width / 2, cover.Y + cover.Height / 2), video.Size);
            Point left = FrameSpot(view, new Point(100, 150), video.Size), right = FrameSpot(view, new Point(380, 250), video.Size);
            bool sawRed = false, sawGreen = false;
            foreach ((int index, Bitmap picture) in video.Kept)
            {
                Assert.True(Black(picture.GetPixel(covered.X, covered.Y)), $"Frame {index} shows {picture.GetPixel(covered.X, covered.Y)} under the cover.");
                // Near its edges too, a few pixels in.
                Point corner = FrameSpot(view, new Point(cover.X + 4, cover.Y + 4), video.Size);
                Assert.True(Black(picture.GetPixel(corner.X, corner.Y)), $"Frame {index} shows {picture.GetPixel(corner.X, corner.Y)} at the cover's corner.");
                Assert.True(Blue(picture.GetPixel(right.X, right.Y)), $"Frame {index} right half {picture.GetPixel(right.X, right.Y)}.");
                sawRed |= Red(picture.GetPixel(left.X, left.Y));
                sawGreen |= Green(picture.GetPixel(left.X, left.Y));
            }
            Assert.True(sawRed && sawGreen, $"The window's change isn't in the recording: red {sawRed}, green {sawGreen}.");
        });
    }

    [WgcFact]
    public void Crop_zoom_and_turn_are_recorded_as_the_view_shows_them()
    {
        WithView(scene =>
        {
            PipWindow view = scene.View;
            view.SetCrop(new Rectangle(120, 40, 240, 200));
            view.Run(AppCommand.ViewRotateRight);
            Assert.True(Desktop.WaitFor(() => view.Turn.Turns == 1, 5000, () => scene.Pump(20)));
            scene.Pump(500);
            Rectangle shown = Field<Rectangle>(view, "_lastShown");
            ViewRecording recording = Start(scene);
            scene.Pump(1500);
            view.Run(AppCommand.ViewRecord);
            RecordingResult result = Finished(scene, recording);
            using VideoFile video = VideoFile.Read(result.File!, index => index % 5 == 2);
            // Turned a quarter: the cropped part stands up.
            Assert.Equal(RecordingGeometry.FrameSize(new Size(shown.Height, shown.Width)), video.Size);
            Point left = FrameSpot(view, new Point(150, 140), video.Size), right = FrameSpot(view, new Point(330, 140), video.Size);
            Assert.True(left.Y < right.Y, $"Turned right, the left of the window should be at the top: {left} {right}.");
            foreach ((int index, Bitmap picture) in video.Kept)
            {
                Assert.True(Red(picture.GetPixel(left.X, left.Y)), $"Frame {index} at {left}: {picture.GetPixel(left.X, left.Y)}.");
                Assert.True(Blue(picture.GetPixel(right.X, right.Y)), $"Frame {index} at {right}: {picture.GetPixel(right.X, right.Y)}.");
            }
        });
    }

    [WgcFact]
    public void A_size_change_while_recording_keeps_the_first_size()
    {
        WithView(scene =>
        {
            Size first = RecordingGeometry.FrameSize(Field<Rectangle>(scene.View, "_lastShown").Size);
            ViewRecording recording = Start(scene);
            scene.Pump(800);
            scene.Target.Resize(560, 260);
            scene.Pump(800);
            scene.View.ZoomAt(new PointF(0.5f, 0.5f), 1.4);
            scene.Pump(800);
            scene.View.Run(AppCommand.ViewRecord);
            RecordingResult result = Finished(scene, recording);
            using VideoFile video = VideoFile.Read(result.File!, index => index == 5);
            Assert.Equal(first, video.Size);
            Assert.InRange(video.Frames, 50, 90);
        });
    }

    public static TheoryData<string> Endings() => ["closed", "minimized", "captureRestarted", "exit"];

    [WgcTheory]
    [MemberData(nameof(Endings))]
    public void However_a_recording_ends_its_file_is_finished(string ending)
    {
        WithView(scene =>
        {
            ViewRecording recording = Start(scene);
            scene.Pump(1500);
            switch (ending)
            {
                case "closed": scene.View.Close(); break;
                case "minimized": scene.View.Run(AppCommand.ViewMinimize); break;
                case "captureRestarted": scene.View.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Wgc }); break;
                case "exit": scene.App.ExitThread(); break;
            }
            if (ending == "exit") Assert.True(recording.Completion.IsCompleted, "Exit didn't wait for the recording to finish.");
            RecordingResult result = Finished(scene, recording);
            if (ending is "minimized" or "captureRestarted")
            {
                Assert.True(Desktop.WaitFor(() => scene.View.LastRecording is not null, 5000, () => scene.Pump(20)));
                Assert.False(scene.View.IsOn(AppCommand.ViewRecord));
            }
            using VideoFile video = VideoFile.Read(result.File!);
            output.WriteLine($"{ending}: {result}, read {video.Frames} frames of {video.Size}");
            Assert.InRange(video.Frames, 25, 70);
        });
    }

    [WgcFact]
    public void A_hidden_original_records_where_capture_can_draw_it_and_says_why_not_elsewhere()
    {
        WithView(scene =>
        {
            scene.View.Run(AppCommand.ViewKeepHidden);
            Assert.True(Desktop.WaitFor(() => scene.View.KeepsUpdating, 5000, () => scene.Pump(20)));
            scene.Pump(1500);
            string? refused = scene.View.RecordingRefusedBecause();
            output.WriteLine($"Windows build {Environment.OSVersion.Version.Build}: {refused ?? "records"}");
            if (Environment.OSVersion.Version.Build < 22000)
                Assert.Equal(Localization.UiText.T("pip.record.notHidden"), refused);
            if (refused is not null)
            {
                scene.View.Run(AppCommand.ViewRecord);
                Assert.False(scene.View.IsOn(AppCommand.ViewRecord));
                Assert.Null(scene.View.ActiveRecording);
                return;
            }
            ViewRecording recording = Start(scene);
            scene.Pump(1500);
            scene.View.Run(AppCommand.ViewRecord);
            using VideoFile video = VideoFile.Read(Finished(scene, recording).File!);
            Assert.True(video.Frames > 20);
        });
    }

    [WgcFact]
    public void A_view_in_standard_mode_records_by_capture_and_goes_back_after()
    {
        WithView(scene =>
        {
            scene.View.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            Assert.True(Desktop.WaitFor(() => scene.View.BackendKind == PipBackendKind.DwmThumbnail, 5000, () => scene.Pump(20)));
            ViewRecording recording = Start(scene);
            Assert.True(Desktop.WaitFor(() => scene.View.BackendKind != PipBackendKind.DwmThumbnail, 15000, () => scene.Pump(20)), scene.View.DiagnosticSnapshot);
            scene.Pump(1500);
            scene.View.Run(AppCommand.ViewRecord);
            RecordingResult result = Finished(scene, recording);
            Assert.True(Desktop.WaitFor(() => scene.View.BackendKind == PipBackendKind.DwmThumbnail, 5000, () => scene.Pump(20)));
            using VideoFile video = VideoFile.Read(result.File!, index => index == 10);
            Assert.True(video.Frames > 20);
            Assert.True(Red(video.Kept[0].Picture.GetPixel(20, video.Size.Height / 2)));
        });
    }

    [WgcFact]
    public void A_part_of_the_screen_records_too()
    {
        WithView(scene =>
        {
            Screen screen = Screen.FromHandle(scene.Target.Handle);
            Rectangle region = new(scene.Target.Bounds.X - screen.Bounds.X + 20, scene.Target.Bounds.Y - screen.Bounds.Y + 40, 360, 200);
            typeof(TrayApplication).GetMethod("OpenScreenView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scene.App, [new ScreenPipSource(screen.DeviceName, region)]);
            scene.Pump(300);
            PipWindow view = Field<List<PipWindow>>(scene.App, "_viewsInOrder").Last();
            Assert.False(view.SourceIsWindow);
            Assert.True(Desktop.WaitFor(() => view.CanTurn, 15000, () => scene.Pump(20)), view.DiagnosticSnapshot);
            // Out of the way of the part it shows.
            view.Location = new Point(screen.WorkingArea.Right - view.Width - 10, screen.WorkingArea.Bottom - view.Height - 10);
            scene.Pump(500);
            view.Run(AppCommand.ViewRecord);
            ViewRecording recording = view.ActiveRecording ?? throw new InvalidOperationException(view.RecordingRefusedBecause() ?? "Not recording.");
            scene.Pump(1500);
            view.Run(AppCommand.ViewRecord);
            RecordingResult result = Finished(scene, recording);
            Assert.False(result.Sound);
            using VideoFile video = VideoFile.Read(result.File!, index => index == 10);
            Bitmap picture = video.Kept[0].Picture;
            Assert.True(Red(picture.GetPixel(30, video.Size.Height / 2)), $"Left: {picture.GetPixel(30, video.Size.Height / 2)}");
            Assert.True(Blue(picture.GetPixel(video.Size.Width - 30, video.Size.Height / 2)), $"Right: {picture.GetPixel(video.Size.Width - 30, video.Size.Height / 2)}");
            view.Close();
        });
    }

    [WgcFact]
    public void With_sound_on_the_recording_has_the_programs_own_sound()
    {
        WithView(scene =>
        {
            scene.Target.PlayTone();
            scene.Pump(500);
            ViewRecording recording = Start(scene);
            scene.Pump(3000);
            scene.View.Run(AppCommand.ViewRecord);
            RecordingResult result = Finished(scene, recording);
            output.WriteLine(result.ToString());
            if (result.SoundRefused) { Skipped.Because("Windows didn't share the program's sound here (no sound device?)."); return; }
            using VideoFile video = VideoFile.Read(result.File!);
            output.WriteLine($"{video.Frames} frames, sound {video.SoundSeconds:0.00} s at level {video.SoundLevel:0.0000}");
            Assert.True(video.HasSound);
            Assert.InRange(video.SoundSeconds, video.Frames / 30.0 - 0.6, video.Frames / 30.0 + 0.6);
            Assert.True(video.SoundLevel > 0.003, $"The tone isn't in the recording (level {video.SoundLevel}).");
        }, sound: true);
    }
}
