using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Show the largest video (roadmap step 8.2) with real capture: a test window with a part that keeps changing, the
/// view crops to that part and keeps following it, and a crop made by hand ends following.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class VideoFollowTests
{
    private static bool Near(Rectangle a, Rectangle b, int slack) =>
        Math.Abs(a.Left - b.Left) <= slack && Math.Abs(a.Top - b.Top) <= slack &&
        Math.Abs(a.Right - b.Right) <= slack && Math.Abs(a.Bottom - b.Bottom) <= slack;

    private static bool Pump(Func<bool> done, int milliseconds) =>
        Desktop.WaitFor(done, milliseconds, () => { Application.DoEvents(); Thread.Sleep(10); });

    [Fact]
    public void A_view_crops_to_the_moving_part_of_its_window_and_a_crop_by_hand_ends_following()
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 480, 360), "C81E1E");
        // In the window's own pixels, as the crop is measured; the target has no frame.
        var movie = new Rectangle(96, 64, 240, 160);
        source.AddMovie(movie);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            var notices = new List<string>();
            view.Notice += notices.Add;
            view.ApplyPipPreferences(new() { Backend = Settings.PipBackendPreference.Auto }); // As the app gives every view.
            view.ShowOnTop();
            view.Location = new Point(760, 120);
            Assert.True(Pump(() => view.BackendKind != PipBackendKind.DwmThumbnail, 10000), "The view never used smooth capture: " + view.DiagnosticSnapshot);

            view.FollowLargestVideo(true);
            Assert.True(Pump(() => !view.Crop.IsEmpty || notices.Count > 0, 10000), "Nothing was found in time.");
            Assert.Empty(notices);
            Assert.True(Near(view.Crop, movie, FrameSample.Tile), $"Cropped to {view.Crop}, the moving part is {movie}.");
            Assert.True(view.FollowsVideo);

            view.SetCrop(new Rectangle(0, 0, 100, 100));
            Assert.False(view.FollowsVideo);
            Thread.Sleep(4000);
            Pump(() => false, 200);
            Assert.Equal(new Rectangle(0, 0, 100, 100), view.Crop);
        });
    }

    /// <summary>Windows' own thumbnail hands over no frames, so the view reads its own picture from the screen.</summary>
    [Fact]
    public void With_standard_capture_the_view_finds_the_moving_part_in_its_own_picture()
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 480, 360), "C81E1E");
        var movie = new Rectangle(96, 64, 240, 160);
        source.AddMovie(movie);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            var notices = new List<string>();
            view.Notice += notices.Add;
            view.ApplyPipPreferences(new() { Backend = Settings.PipBackendPreference.Dwm });
            view.ShowOnTop();
            view.Location = new Point(760, 120);
            Pump(() => false, 1000);
            Assert.Equal(PipBackendKind.DwmThumbnail, view.BackendKind);
            view.FollowLargestVideo(true);
            Assert.True(Pump(() => !view.Crop.IsEmpty || notices.Count > 0, 10000), "Nothing was found in time.");
            Assert.Empty(notices);
            Assert.True(Near(view.Crop, movie, 40), $"Cropped to {view.Crop}, the moving part is {movie}.");
        });
    }

    [Fact]
    public void A_window_where_nothing_moves_says_so_and_stops()
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 480, 360), "1E64C8");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            var notices = new List<string>();
            view.Notice += notices.Add;
            view.ApplyPipPreferences(new() { Backend = Settings.PipBackendPreference.Auto }); // As the app gives every view.
            view.ShowOnTop();
            view.Location = new Point(760, 120);
            Assert.True(Pump(() => view.BackendKind != PipBackendKind.DwmThumbnail, 10000), "The view never used smooth capture: " + view.DiagnosticSnapshot);
            view.FollowLargestVideo(true);
            Assert.True(Pump(() => notices.Count > 0, 10000), "No word that nothing moves.");
            Assert.False(view.FollowsVideo);
            Assert.True(view.Crop.IsEmpty);
        });
    }

    /// <summary>A page with a playing video among text and a still image, in Chrome and in Edge where they are.</summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData("edge")]
    public void A_browser_view_crops_to_the_whole_player_when_only_part_of_the_video_moves(string which)
    {
        string path = which == "edge" ? BrowserInputTests.EdgePath : Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(path)) { Skipped.Because(which + " isn't here."); return; }
        // Like a talking head: a 640 by 360 video that is still and dark but for a patch in its middle.
        const string html = """
            <!doctype html><html><head><title>VideoFollow</title></head><body style="margin:20px;font:16px sans-serif">
            <p>Some text above the video, which stays still.</p>
            <canvas id="c" width="640" height="360" style="display:none"></canvas>
            <video id="v" width="640" height="360" muted autoplay style="display:block"></video>
            <div style="width:200px;height:120px;background:linear-gradient(red,blue);margin-top:10px"></div>
            <script>
              const c = document.getElementById('c'), g = c.getContext('2d'); let n = 0;
              setInterval(() => {
                g.fillStyle = '#141414'; g.fillRect(0, 0, 640, 360);
                for (let i = 0; i < 12; i++) { g.fillStyle = 'hsl(' + ((n * 7 + i * 31) % 360) + ',70%,' + (25 + (n * 13 + i * 17) % 50) + '%)'; g.fillRect(240 + (i % 4) * 40, 120 + Math.floor(i / 4) * 40, 40, 40); }
                n++;
              }, 50);
              const v = document.getElementById('v'); v.srcObject = c.captureStream(20); v.play();
            </script>
            </body></html>
            """;
        var (browser, window, root) = BrowserInputTests.Open(html, "VideoFollow", app: false, path: path);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(window, new WindowController([]));
                var notices = new List<string>();
                view.Notice += notices.Add;
                view.ApplyPipPreferences(new() { Backend = Settings.PipBackendPreference.Auto });
                view.ShowOnTop();
                view.Location = new Point(1100, 100);
                Assert.True(Pump(() => view.BackendKind != PipBackendKind.DwmThumbnail, 10000), "The view never used smooth capture: " + view.DiagnosticSnapshot);
                // A covered browser stops drawing; Keep hidden is how a view of one stays live, as the guide says.
                view.SetOriginalHidden(true);
                Pump(() => false, 1500);
                view.FollowLargestVideo(true);
                string[] videoNotices = [Localization.UiText.T("pip.video.none")];
                Assert.True(Pump(() => !view.Crop.IsEmpty || notices.Intersect(videoNotices).Any(), 12000), "Nothing was found in time.");
                Assert.Empty(notices.Intersect(videoNotices)); // Hiding the original can bring its own notice on Windows 10.
                Size wanted = new(640 * Native.Dpi.Of(window) / 96, 360 * Native.Dpi.Of(window) / 96);
                Assert.True(Math.Abs(view.Crop.Width - wanted.Width) <= 40 && Math.Abs(view.Crop.Height - wanted.Height) <= 40,
                    $"Cropped to {view.Crop}, the video is {wanted}.");
                view.FollowLargestVideo(false);
                view.SetOriginalHidden(false);
            });
        }
        finally { BrowserInputTests.Close(browser, root); }
    }
}
