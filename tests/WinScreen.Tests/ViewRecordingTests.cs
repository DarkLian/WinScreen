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
    public void The_clock_writes_every_tick_unless_more_than_a_second_behind()
    {
        Assert.Equal(10, RecordingGeometry.NextTick(10, 10 / 30.0, 30));
        Assert.Equal(10, RecordingGeometry.NextTick(10, 1.2, 30));
        Assert.Equal(60, RecordingGeometry.NextTick(10, 2.0, 30));
        Assert.InRange(RecordingGeometry.Bitrate(new Size(1920, 1080), 30), 7_000_000, 8_000_000);
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
        var settings = new AppSettings { PipViews = new PipViewPreferences { RecordingFolder = @" D:\Clips ", RecordSound = true } };
        AppSettings read = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.Equal(@"D:\Clips", read.PipViews.Validated().RecordingFolder);
        Assert.True(read.PipViews.RecordSound);
        AppSettings fresh = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(new AppSettings()));
        Assert.Null(fresh.PipViews.Validated().RecordingFolder);
        Assert.False(fresh.PipViews.RecordSound);
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Frames_handed_in_become_a_video_at_a_steady_rate_that_Windows_reads_back(bool softwareOnly)
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
            Size size = recording.FrameSize(new Size(421, 301));
            Assert.Equal(new Size(420, 300), size);
            // Red on the left, grey on the right, a white bar at the top (so a turned or flipped frame shows), then green,
            // then blue, a second each,
            // handed in faster and slower than the recording's rate; the recording repeats or skips as needed.
            var clock = Stopwatch.StartNew();
            int handed = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                int phase = (int)clock.Elapsed.TotalSeconds;
                if (recording.Rent() is { } frame)
                {
                    Fill(frame, size, phase switch { 0 => 0xC81E1E, 1 => 0x1EC81E, _ => 0x1E1EC8 });
                    recording.Deliver(frame);
                    handed++;
                }
                Thread.Sleep(phase == 1 ? 100 : 8);
            }
            recording.Finish();
            RecordingResult result = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            output.WriteLine($"{result}, {handed} handed in");
            Assert.Equal(path, result.File);
            Assert.Equal(hardware, result.Hardware);
            using VideoFile video = VideoFile.Read(path, index => index % 10 == 5);
            Assert.Equal(size, video.Size);
            Assert.InRange(video.Frames, 80, 100);
            Assert.Equal(result.Frames, video.Frames);
            // Steady: one frame every 1/30 s.
            for (int i = 1; i < video.Times.Count; i++)
                Assert.InRange(video.Times[i] - video.Times[i - 1], 333_000, 334_000);
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
