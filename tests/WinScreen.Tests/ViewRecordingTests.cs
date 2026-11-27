using System.Diagnostics;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

// Owner, 23 Nov: a view records what it shows to an MP4, at a steady rate, into the Screen Recordings folder unless
// Settings names another. The frame size, file names, settings and the writer itself, without windows.
public sealed class ViewRecordingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(420, 300, 420, 300)]
    [InlineData(421, 301, 420, 300)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(8000, 1000, 4096, 512)]
    [InlineData(20, 10, 128, 64)]
    [InlineData(5000, 10, 4096, 64)]
    public void A_recording_is_the_first_picture_size_kept_even_and_within_what_encoders_take(int width, int height, int wantWidth, int wantHeight)
    {
        Size frame = RecordingGeometry.FrameSize(new Size(width, height));
        Assert.Equal(new Size(wantWidth, wantHeight), frame);
        Assert.True(frame.Width % 2 == 0 && frame.Height % 2 == 0);
        Assert.True((long)frame.Width * frame.Height <= 4096L * 2304);
    }

    [Fact]
    public void A_later_picture_of_another_shape_is_scaled_into_the_frame_centred()
    {
        Assert.Equal(new Rectangle(0, 40, 320, 160), RecordingGeometry.Letterbox(new Size(200, 100), new Size(320, 240)));
        Assert.Equal(new Rectangle(100, 0, 120, 240), RecordingGeometry.Letterbox(new Size(50, 100), new Size(320, 240)));
        Assert.Equal(new Rectangle(0, 0, 320, 240), RecordingGeometry.Letterbox(new Size(320, 240), new Size(320, 240)));
        // Turned a quarter, the same picture stands up in it.
        Assert.Equal(new Rectangle(70, 0, 180, 240), RecordingGeometry.Letterbox(new ViewTurn(1, false).Oriented(new Size(320, 240)), new Size(320, 240)));
    }

    [Fact]
    public void The_clock_writes_every_tick_unless_more_than_three_seconds_behind()
    {
        Assert.Equal(10, RecordingGeometry.NextTick(10, 10 / 30.0, 30));
        Assert.Equal(10, RecordingGeometry.NextTick(10, 2.0, 30));
        Assert.Equal(150, RecordingGeometry.NextTick(10, 5.0, 30));
        Assert.InRange(RecordingGeometry.Bitrate(new Size(1920, 1080), 30), 7_000_000, 8_000_000);
        Assert.InRange(RecordingGeometry.Bitrate(new Size(1920, 1080), 120), 15_000_000, 16_000_000);
    }

    // Owner, 23 Nov: the recording's rate is the view's own (its frame rate, or its display's refresh for "match"), as far
    // as H.264 allows at the size; an encoder that still refuses it gets the next usual rate below.
    [Theory]
    [InlineData(60, 1920, 1080, 60)]
    [InlineData(24, 1920, 1080, 24)]
    [InlineData(144, 1920, 1080, 144)]
    [InlineData(165, 2560, 1440, 144)]
    [InlineData(144, 3840, 2160, 60)]
    [InlineData(480, 1280, 720, 240)]
    [InlineData(0, 1280, 720, 60)]
    [InlineData(5, 1280, 720, 15)]
    public void A_recording_takes_the_views_rate_as_far_as_HD_video_allows(int viewRate, int width, int height, int expected)
    {
        Assert.Equal(expected, RecordingGeometry.RateFor(viewRate, new Size(width, height)));
        Assert.Equal([240, 144, 120, 60, 30], RecordingGeometry.RatesFrom(240));
        Assert.Equal([75, 60, 30], RecordingGeometry.RatesFrom(75));
    }

    [Fact]
    public void Recordings_are_named_as_the_Snipping_Tool_names_them_and_never_overwrite()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-recordings-").FullName;
        try
        {
            var shelf = new PictureShelf { RecordingFolder = () => folder, Now = () => new DateTime(2026, 11, 23, 17, 50, 8) };
            string first = shelf.NewRecordingPath(), second = shelf.NewRecordingPath();
            Assert.Equal(Path.Combine(folder, "Screen Recording 2026-11-23 175008.mp4"), first);
            Assert.Equal(Path.Combine(folder, "Screen Recording 2026-11-23 175008 (2).mp4"), second);
            Assert.True(File.Exists(first) && File.Exists(second));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Without_a_folder_chosen_recordings_go_to_Screen_Recordings_under_Videos()
    {
        string folder = PictureShelf.DefaultRecordingFolder();
        Assert.Equal("Screen Recordings", Path.GetFileName(folder));
        Assert.Equal(new PictureShelf().RecordingFolderInUse, folder);
        Assert.Equal(@"C:\Elsewhere", new PictureShelf { RecordingFolder = () => @"C:\Elsewhere" }.RecordingFolderInUse);
    }

    [Fact]
    public void The_recordings_folder_and_sound_switch_are_kept_in_the_settings_file()
    {
        var settings = new AppSettings { PipViews = new PipViewPreferences { RecordingFolder = @" D:\Clips ", RecordSound = true, RecordingFrameRate = 144 } };
        AppSettings read = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.Equal(@"D:\Clips", read.PipViews.Validated().RecordingFolder);
        Assert.True(read.PipViews.RecordSound);
        Assert.Equal(144, read.PipViews.Validated().RecordingFrameRate);
        // A rate Settings doesn't offer (an edited file, a newer copy's choice) is the view's own.
        Assert.Equal(0, new PipViewPreferences { RecordingFrameRate = 77 }.Validated().RecordingFrameRate);
        AppSettings fresh = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(new AppSettings()));
        Assert.Null(fresh.PipViews.Validated().RecordingFolder);
        Assert.False(fresh.PipViews.RecordSound);
        Assert.Equal(0, fresh.PipViews.RecordingFrameRate);
        Assert.Null(new PipViewPreferences { RecordingFolder = "  " }.Validated().RecordingFolder);
    }

    [Fact]
    public async Task A_recording_finished_before_any_frame_leaves_no_file()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-recordings-").FullName;
        try
        {
            string path = new PictureShelf { RecordingFolder = () => folder }.NewRecordingPath();
            var recording = new ViewRecording(path);
            recording.Finish();
            recording.Finish();
            RecordingResult result = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.File);
            Assert.Equal(0, result.Problem);
            Assert.False(File.Exists(path));
            Assert.Null(recording.Rent());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(true, 60)]
    [InlineData(false, 60)]
    [InlineData(true, 144)]
    [InlineData(true, 240)]
    public async Task Frames_handed_in_become_a_video_at_a_steady_rate_that_Windows_reads_back(bool softwareOnly, int viewRate)
    {
        if (!ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        string folder = Directory.CreateTempSubdirectory("winscreen-recordings-").FullName;
        try
        {
            // A hardware encoder is used only once it has finished a file of its own.
            bool hardware = false;
            if (!softwareOnly)
            {
                VideoFileWriter.CheckHardware();
                hardware = await VideoFileWriter.HardwareCheck.WaitAsync(TimeSpan.FromSeconds(15));
                output.WriteLine($"Hardware encoder proven: {hardware}");
            }
            string path = new PictureShelf { RecordingFolder = () => folder }.NewRecordingPath();
            var recording = new ViewRecording(path, softwareOnly: softwareOnly);
            // 420 isn't a multiple of 16, as encoders' rows are: a frame read or written at the wrong pitch shows sheared.
            Size size = recording.FrameSize(new Size(421, 301), viewRate);
            Assert.Equal(new Size(420, 300), size);
            // Red on the left, grey on the right, a white bar at the top (so a turned or flipped frame shows), then green,
            // then blue, a second each,
            // handed in faster and slower than the recording's rate; the recording repeats or skips as needed.
            byte[][] patterns = [.. new[] { 0xC81E1E, 0x1EC81E, 0x1E1EC8 }.Select(rgb =>
            {
                var pattern = new byte[size.Width * size.Height * 4];
                Fill(pattern, size, rgb);
                return pattern;
            })];
            // From the first frame taken, as the recording's own clock: on a slow PC (GitHub's ARM64 one) the encoder
            // took its first frame after the red second had passed, and the video had no red.
            var clock = new Stopwatch();
            var waited = Stopwatch.StartNew();
            int handed = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(3) && waited.Elapsed < TimeSpan.FromSeconds(20))
            {
                int phase = (int)clock.Elapsed.TotalSeconds;
                if (recording.Rent() is { } frame)
                {
                    clock.Start();
                    // Copied from patterns made once: painting each frame here took the processor the encoder needs on
                    // the two-core test VM (278 of 432 frames at 144 a second).
                    patterns[Math.Min(phase, 2)].CopyTo(frame, 0);
                    recording.Deliver(frame);
                    handed++;
                }
                Thread.Sleep(phase == 1 ? 100 : 8);
            }
            recording.Finish();
            RecordingResult result = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            output.WriteLine($"{result}, {handed} handed in");
            Assert.Equal(path, result.File);
            // The first frame taken covers the start, by design. On GitHub's ARM64 runner the start once took the whole
            // three seconds (one frame, then the last), and once the recording's thread began after the red second had
            // gone (no red): neither the rate nor the colours can be checked then.
            if (result.StartMilliseconds > 1000)
            {
                Skipped.Because($"The encoder took {result.StartMilliseconds} ms to start, so the recording couldn't run at its rate.");
                return;
            }
            Assert.Equal(hardware, result.Hardware);
            using VideoFile video = VideoFile.Read(path, index => index % 10 == 5);
            Assert.Equal(size, video.Size);
            // Windows' own encoder takes up to 144 a second; asked for 240, it gets the next usual rate it takes.
            int rate = viewRate == 240 && !result.Hardware ? 144 : viewRate;
            Assert.Equal(rate, result.Rate);
            Assert.Equal(result.Frames, video.Frames);
            // As long as the recording, whatever the processor managed.
            Assert.InRange(video.Times[^1] / 10_000_000.0, 2.8, 3.3);
            // Every frame on the 1/rate grid, without drift; one encoder too slow for the rate (the two-core test VM at 144
            // a second) leaves some out, where the last frame stays shown.
            int gaps = 0;
            for (int i = 1; i < video.Times.Count; i++)
            {
                long step = video.Times[i] - video.Times[i - 1];
                long whole = (long)Math.Round(step * rate / 10_000_000.0);
                Assert.True(whole >= 1 && Math.Abs(step - whole * 10_000_000.0 / rate) <= 2, $"Frame {i} is {step} after the one before.");
                if (whole > 1) gaps++;
            }
            output.WriteLine($"{video.Frames} frames, {gaps} gaps");
            Assert.InRange(video.Frames, rate * 3 / 2, rate * 3 + rate / 10 + 2);
            if (rate <= 60) Assert.Equal(0, gaps);
            Assert.False(video.HasSound);
            var seen = new HashSet<string>();
            foreach ((int index, Bitmap picture) in video.Kept)
            {
                Color middle = picture.GetPixel(60, 200), top = picture.GetPixel(400, 6), right = picture.GetPixel(400, 200);
                Assert.True(Math.Abs(right.R - 128) < 30 && Math.Abs(right.B - 128) < 30, $"Frame {index}: the right half is {right}, not grey.");
                string name = middle.R > 150 && middle.G < 90 ? "red" : middle.G > 150 && middle.R < 90 ? "green" : middle.B > 150 && middle.R < 90 ? "blue" : middle.ToString();
                seen.Add(name);
                Assert.True(top.R > 200 && top.G > 200 && top.B > 200, $"Frame {index}: the white bar isn't at the top ({top}).");
            }
            Assert.Equal(["blue", "green", "red"], seen.Order());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Fill(byte[] frame, Size size, int rgb)
    {
        for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
            {
                int color = y < 16 ? 0xFFFFFF : x >= size.Width / 2 ? 0x808080 : rgb, at = (y * size.Width + x) * 4;
                frame[at] = (byte)color; frame[at + 1] = (byte)(color >> 8); frame[at + 2] = (byte)(color >> 16); frame[at + 3] = 255;
            }
    }
}
