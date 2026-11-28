using System.Net;
using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

// Where Glass backgrounds come from, going wrong and coming back: files moved, deleted and put back, links that are dead,
// wrong, too big or stop sending, and the internet returning (the owner, 24 Nov).
[Trait("Category", "RealWindows")]
public sealed class BackgroundSourceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Rectangle Spot = new(300, 220, 420, 320);
    private static readonly Color Green = Color.FromArgb(0x20, 0xB0, 0x40), Red = Color.FromArgb(0xC8, 0x1E, 0x1E);
    private readonly string _folder = Directory.CreateTempSubdirectory("WinScreen background ").FullName;

    public void Dispose()
    {
        for (int i = 0; ; i++)
        {
            try { Directory.Delete(_folder, true); return; }
            catch (Exception busy) when (busy is IOException or UnauthorizedAccessException && i < 30) { Thread.Sleep(100); }
        }
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Action<HttpListenerContext> _answer;
        public int Asked;

        public Server(int port, Action<HttpListenerContext> answer)
        {
            _answer = answer;
            _listener.Prefixes.Add(At(port));
            _listener.Start();
            _ = Serve();
        }

        public static string At(int port) => $"http://localhost:{port}/";

        public static int FreePort()
        {
            using var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            free.Start();
            return ((IPEndPoint)free.LocalEndpoint).Port;
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext asked;
                try { asked = await _listener.GetContextAsync(); }
                catch (Exception stopped) when (stopped is HttpListenerException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref Asked);
                _ = Task.Run(() =>
                {
                    // The player and WinScreen hang up whenever they have enough.
                    try { _answer(asked); }
                    catch (Exception gone) when (gone is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException) { asked.Response.Abort(); }
                });
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static byte[] Png(Color colour)
    {
        using var picture = new Bitmap(64, 64);
        using (Graphics g = Graphics.FromImage(picture)) g.Clear(colour);
        using var bytes = new MemoryStream();
        picture.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
        return bytes.ToArray();
    }

    private static void Send(HttpListenerContext asked, byte[] body, string kind)
    {
        asked.Response.ContentType = kind;
        asked.Response.AddHeader("Accept-Ranges", "bytes");
        int from = 0, to = body.Length - 1;
        if (asked.Request.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            string[] ends = range[6..].Split('-');
            from = int.Parse(ends[0]);
            if (ends[1].Length > 0) to = Math.Min(to, int.Parse(ends[1]));
            asked.Response.StatusCode = 206;
            asked.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{body.Length}");
        }
        asked.Response.ContentLength64 = to - from + 1;
        asked.Response.OutputStream.Write(body, from, to - from + 1);
        asked.Response.Close();
    }

    private string Video(string name, Color colour)
    {
        string path = Path.Combine(_folder, name);
        var size = new Size(320, 240);
        var random = new Random(5);
        byte[] Frame()
        {
            // Noise at the edge keeps the file big enough that half of it isn't the whole video.
            var frame = new byte[size.Width * size.Height * 4];
            for (int i = 0; i < frame.Length; i += 4)
                (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = i < size.Width * 4 * 40
                    ? ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255) : (colour.B, colour.G, colour.R, (byte)255);
            return frame;
        }
        using var writer = Pip.VideoFileWriter.Open(path, size, 30, sound: false, softwareOnly: true, Frame());
        for (int tick = 1; tick < 90; tick++)
        {
            writer.Frame(Frame());
            writer.Write(tick);
        }
        writer.Finish();
        return path;
    }

    private static Color Middle(Rectangle bounds) =>
        Desktop.ColorOnScreen(new Rectangle(bounds.Left + bounds.Width / 2 - 5, bounds.Top + bounds.Height / 2 - 5, 10, 10));

    private static bool Wait(Func<bool> until, int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            if (until()) return true;
            Application.DoEvents();
            Thread.Sleep(20);
        }
        return until();
    }

    private void WithGlass(BackdropSet set, Action<ContentEffects, nint, Func<Color>, List<string>> test)
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        User32.SetWindowPos(target.Handle, 0, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            var problems = new List<string>();
            effects.Problem += problems.Add;
            effects.SetLevels(new ContentLevels(0, 1, Shadow: 0));
            effects.SetBackgrounds(_ => set);
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Glass));
                test(effects, target.Handle, () => Middle(bounds), problems);
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    private static BackdropChoice Item(string file) => new(file, BackdropFit.Fill);

    [GraphicsCardFact]
    public void A_file_put_back_shows_by_itself_and_turning_Glass_on_again_names_a_missing_one_again()
    {
        string file = Path.Combine(_folder, "green.png");
        WithGlass(new([Item(file)]), (effects, window, colour, problems) =>
        {
            Assert.True(Wait(() => problems.Count == 1, 3000));
            Assert.Contains("can't be found", problems[0]);
            effects.Toggle(window, ContentLook.Glass);
            effects.Toggle(window, ContentLook.Glass);
            Assert.True(Wait(() => problems.Count == 2, 3000), "Turned on again, the missing file wasn't named.");
            File.WriteAllBytes(file, Png(Green));
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 9000), $"{colour()}: the file is back, but isn't shown.");
            Assert.Equal(2, problems.Count);
        });
    }

    // Every item missing used to stop the set for good.
    [GraphicsCardFact]
    public void A_set_where_nothing_opens_tries_again()
    {
        string file = Path.Combine(_folder, "green.png");
        WithGlass(new([Item(Path.Combine(_folder, "gone.mp4")), Item(file)]), (_, _, colour, problems) =>
        {
            Assert.True(Wait(() => problems.Count == 2, 3000));
            File.WriteAllBytes(file, Png(Green));
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 9000), $"{colour()}: the file is there now, but isn't shown.");
        });
    }

    // Wherever the person takes it: deleted or moved, what shows goes on or the next shows, and nothing stops.
    [GraphicsCardFact]
    public void A_video_taken_away_while_it_plays_stops_nothing()
    {
        if (!Pip.ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        string red = Video("red.mp4", Red), green = Path.Combine(_folder, "green.png");
        File.WriteAllBytes(green, Png(Green));
        WithGlass(new([Item(red), Item(green)]), (effects, window, colour, _) =>
        {
            Assert.True(Wait(() => Desktop.IsNear(colour(), Red, 40), 5000), colour().ToString());
            string did;
            try { File.Move(red, red + ".moved"); did = "moved"; }
            catch (IOException) { did = "in use, so Windows kept it"; }
            output.WriteLine(did);
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 9000), $"{colour()}: the next didn't show after the video ({did}).");
            Assert.True(effects.Has(window, ContentLook.Glass));
        });
    }

    // Too long to keep whole, so it is decoded as it plays: every frame in turn, round again, and on after its file is
    // deleted. Longer than the 600 frames that used to be the end of it.
    [GraphicsCardFact]
    public void A_long_animation_plays_in_order_and_round_again_with_its_file_gone()
    {
        string file = Path.Combine(_folder, "long.gif");
        const int Frames = 700, Wide = 160, High = 160;
        WriteGif(file, Wide, High, Frames);
        Assert.True((long)Wide * High * 4 * Frames > AnimationBackdrop.MostKept);
        WithGlass(new([new(file, BackdropFit.Fill, Speed: 400)]), (_, _, colour, problems) =>
        {
            int Frame(Color at) => at.R < 200 && at.B > 200 ? at.G * 4 : -1; // Frame n is (0, n / 4, 255); round again it starts low.
            Assert.True(Wait(() => Frame(colour()) >= 0, 5000), colour().ToString());
            File.Delete(file);
            int last = Frame(colour()), most = last, rounds = 0;
            long end = Environment.TickCount64 + 30_000;
            while (rounds < 1 && Environment.TickCount64 < end)
            {
                int now = Frame(colour());
                if (now >= 0)
                {
                    if (now < last - 100) rounds++;
                    else Assert.True(now >= last - 8, $"Went back from {last} to {now}.");
                    last = now;
                    most = Math.Max(most, now);
                }
                Application.DoEvents();
                Thread.Sleep(5);
            }
            output.WriteLine($"last {last}, most {most}, rounds {rounds}");
            Assert.True(most > 620, $"Only got to about frame {most}.");
            Assert.Equal(1, rounds);
            Assert.Empty(problems);
            Assert.False(File.Exists(file));
        });
    }

    // Uncompressed blocks of one colour each, 20 ms a frame.
    private static void WriteGif(string path, int wide, int high, int frames)
    {
        using var writer = new BinaryWriter(File.Create(path));
        void U16(int value) => writer.Write((ushort)value);
        writer.Write("GIF89a"u8);
        U16(wide); U16(high);
        writer.Write((byte)0xF7); writer.Write((byte)0); writer.Write((byte)0);
        for (int i = 0; i < 256; i++) { writer.Write((byte)0); writer.Write((byte)i); writer.Write((byte)255); }
        writer.Write([0x21, 0xFF, 11]); writer.Write("NETSCAPE2.0"u8); writer.Write([3, 1, 0, 0, 0]);
        for (int frame = 0; frame < frames; frame++)
        {
            writer.Write([0x21, 0xF9, 4, 4]); U16(2); writer.Write([0, 0]);
            writer.Write((byte)0x2C); U16(0); U16(0); U16(wide); U16(high); writer.Write((byte)0);
            writer.Write((byte)8);
            // Nine-bit codes with a clear often enough that they never grow.
            var bits = new List<byte>();
            int held = 0, count = 0;
            void Code(int code)
            {
                held |= code << count;
                for (count += 9; count >= 8; count -= 8, held >>= 8) bits.Add((byte)held);
            }
            for (int i = 0; i < wide * high; i++) { if (i % 250 == 0) Code(256); Code(frame / 4); }
            Code(257);
            if (count > 0) bits.Add((byte)held);
            for (int at = 0; at < bits.Count; at += 255)
            {
                int part = Math.Min(255, bits.Count - at);
                writer.Write((byte)part);
                writer.Write([.. bits.Skip(at).Take(part)]);
            }
            writer.Write((byte)0);
        }
        writer.Write((byte)0x3B);
    }

    // A picture deleted or moved while it shows stays; with a fit that needs its own pixels it is read again, and stays
    // as it is when it can't be.
    [GraphicsCardFact]
    public void A_picture_whose_fit_changes_is_read_again_and_stays_when_it_is_gone()
    {
        string file = Path.Combine(_folder, "large.png");
        using (var picture = new Bitmap(SystemInformation.VirtualScreen.Width + 2000, SystemInformation.VirtualScreen.Height + 1200))
        {
            using (Graphics g = Graphics.FromImage(picture)) g.Clear(Green);
            picture.Save(file);
        }
        WithGlass(new([Item(file)]), (effects, _, colour, problems) =>
        {
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 9000), colour().ToString());
            effects.SetBackgrounds(_ => new BackdropSet([Item(file) with { Fit = BackdropFit.Center }]));
            for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(20); Assert.True(Desktop.IsNear(colour(), Green, 24), colour().ToString()); }
            File.Delete(file);
            effects.SetBackgrounds(_ => new BackdropSet([Item(file)]));
            for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(20); Assert.True(Desktop.IsNear(colour(), Green, 24), colour().ToString()); }
            Assert.Empty(problems);
        });
    }

    [GraphicsCardTheory]
    [InlineData("text")]
    [InlineData("big")]
    [InlineData("refused")]
    public void A_link_that_isnt_a_picture_is_named_once_and_passed_over(string wrong)
    {
        int port = Server.FreePort();
        byte[] green = Png(Green);
        using var server = new Server(port, asked =>
        {
            if (asked.Request.Url!.AbsolutePath == "/green.png") { Send(asked, green, "image/png"); return; }
            if (wrong == "text") { Send(asked, "Not a picture."u8.ToArray(), "text/plain"); return; }
            if (wrong == "refused") { asked.Response.StatusCode = 403; asked.Response.Close(); return; }
            asked.Response.ContentLength64 = 80L * 1024 * 1024; // Said, never sent.
            asked.Response.OutputStream.Write(new byte[1024]);
        });
        WithGlass(new([Item(Server.At(port) + "wrong.png"), Item(Server.At(port) + "green.png")]), (_, _, colour, problems) =>
        {
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24) && problems.Count > 0, 9000), $"{colour()}, {problems.Count} messages");
            Assert.Contains("wrong.png", Assert.Single(problems));
        });
    }

    [GraphicsCardFact]
    public void A_link_shows_once_its_server_answers_again()
    {
        int port = Server.FreePort();
        WithGlass(new([Item(Server.At(port) + "green.png")]), (_, _, colour, problems) =>
        {
            Assert.True(Wait(() => problems.Count == 1, 9000));
            byte[] green = Png(Green);
            using var server = new Server(port, asked => Send(asked, green, "image/png"));
            Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 15000), $"{colour()}: the server is back, but nothing shows.");
            Assert.Single(problems);
        });
    }

    [GraphicsCardFact]
    public void A_video_from_a_link_plays()
    {
        if (!Pip.ViewRecording.Supported) { Skipped.Because("Windows' video encoder isn't on this PC (an N edition)."); return; }
        byte[] video = File.ReadAllBytes(Video("red.mp4", Red));
        int port = Server.FreePort();
        using var server = new Server(port, asked => Send(asked, video, "video/mp4"));
        WithGlass(new([Item(Server.At(port) + "red.mp4")]), (_, _, colour, problems) =>
        {
            Assert.True(Wait(() => Desktop.IsNear(colour(), Red, 40), 10000), $"{colour()}, asked {server.Asked} times, {string.Join("; ", problems)}");
            Assert.Empty(problems);
        });
    }

    // A server that stops sending reports no error, so the player would wait for ever.
    [GraphicsCardFact]
    public void A_video_link_that_stops_sending_gives_way_to_the_next()
    {
        int port = Server.FreePort();
        byte[] green = Png(Green);
        using var stop = new ManualResetEventSlim();
        using var server = new Server(port, asked =>
        {
            if (asked.Request.Url!.AbsolutePath == "/green.png") { Send(asked, green, "image/png"); return; }
            asked.Response.ContentType = "video/mp4";
            asked.Response.ContentLength64 = 5_000_000;
            asked.Response.OutputStream.Write(new byte[16]);
            asked.Response.OutputStream.Flush();
            stop.Wait(60_000);
        });
        try
        {
            WithGlass(new([Item(Server.At(port) + "stuck.mp4"), Item(Server.At(port) + "green.png")]), (_, _, colour, problems) =>
            {
                long start = Environment.TickCount64;
                Assert.True(Wait(() => Desktop.IsNear(colour(), Green, 24), 40000), $"{colour()}: still waiting for the video.");
                output.WriteLine($"after {Environment.TickCount64 - start} ms: {string.Join("; ", problems)}");
                Assert.Contains("stuck.mp4", Assert.Single(problems));
            });
        }
        finally { stop.Set(); }
    }
}
