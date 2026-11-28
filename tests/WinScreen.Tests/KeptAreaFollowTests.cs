using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

// The owner, 28 Nov 2026: an area kept on a video should survive scrolling. An area marked over a control follows it,
// asked of the app through UI Automation; the test target's scrolling page stands in for a web page with a video.
[Trait("Category", "RealWindows")]
public sealed class KeptAreaFollowTests(ITestOutputHelper output)
{
    private static readonly Rectangle Spot = new(300, 220, 420, 300);

    public static IEnumerable<object[]> Browsers() =>
        BrowserInputTests.ToolbarCases().Select(c => (string)c[0]).Distinct().SelectMany(path => new[] { new object[] { path, true }, [path, false] });

    // A video with no file is still the page's video element, red so a person watching can see it. The page scrolls
    // itself by 0, 60 and 120 in turn and says where in its title. Without accessibility forced on, as people run their
    // browser, the marking overlay's highlight is what first asks: the test asks the same way before marking.
    [Theory]
    [MemberData(nameof(Browsers))]
    public void A_video_on_a_web_page_is_followed_as_the_page_scrolls(string path, bool accessibility)
    {
        const string page = "<!doctype html><title>VideoProbe 0</title><style>body{margin:0;background:#fff;height:3000px}" +
            "video{position:absolute;left:40px;top:100px;width:320px;height:180px;background:#e8453c}</style>" +
            "<video class=\"player-video\" muted></video>" +
            "<script>let n=0;setInterval(()=>{n=(n+1)%3;scrollTo(0,n*60);document.title='VideoProbe '+n*60},1500)</script>";
        var (browser, window, root) = BrowserInputTests.Open(page, "VideoProbe", app: false, path, accessibility: accessibility);
        try
        {
            int Scrolled() => int.TryParse(WindowInfo.Title(window).Split(' ').ElementAtOrDefault(1), out int top) ? top : -1;
            Rectangle document = Rectangle.Empty;
            Assert.True(Desktop.WaitFor(() => (document = BrowserAccessibility.Read(window, 24).FirstOrDefault(c => c.Type == 50030)?.Bounds ?? Rectangle.Empty)
                .Width > 0, 10000), "No document.");
            Assert.True(Desktop.WaitFor(() => Scrolled() == 0, 5000), WindowInfo.Title(window));
            var video = new Rectangle(document.Left + 40, document.Top + 100, 320, 180);
            Rectangle? hint = null;
            Assert.True(Desktop.WaitFor(() => (hint = ElementFinder.Find(window, new Point(video.Left + 160, video.Top + 90))) == video, 10000),
                $"The highlight showed {hint?.ToString() ?? "nothing"}, not the video at {video}.");
            Assert.True(Desktop.WaitFor(() => Scrolled() == 0, 5000), WindowInfo.Title(window));
            (KeptArea area, Rectangle frame) = Marked(window, video);
            output.WriteLine($"{Path.GetFileName(path)}: type {area.ControlType}, class '{area.ControlClass}', id '{area.ControlId}'");
            Assert.True(area.Follows, "The video wasn't found as a control.");

            using var follower = new KeptAreaFollower(window, () => { });
            KeptArea[] kept = [area];
            follower.SetAreas(kept);
            var matched = new HashSet<int>();
            Rectangle? last = null;
            Desktop.WaitFor(() =>
            {
                follower.WaitFor(follower.Ask(), 100);
                int top = Scrolled();
                last = follower.PlacesFor(kept)?[0];
                if (top >= 0 && last == video with { X = video.X - frame.X, Y = video.Y - top - frame.Y } && Scrolled() == top) matched.Add(top);
                return matched.Count >= 2 && matched.Any(top => top > 0);
            }, 12000);
            Assert.True(matched.Count >= 2 && matched.Any(top => top > 0),
                $"Followed at scroll {string.Join(", ", matched)} only; last at {last?.ToString() ?? "where it was marked"}, video {video} in frame {frame}.");
        }
        finally { BrowserInputTests.Close(browser, root); }
    }

    public static IEnumerable<object[]> NextPages() =>
        BrowserInputTests.ToolbarCases().Select(c => (string)c[0]).Distinct().SelectMany(path => new[] { new object[] { path, false }, [path, true] });

    // The owner, 28 Nov 2026: an area kept on Bilibili's player wasn't kept once the next video opened, until it was
    // marked again. Chrome went on answering for the player on the page before, where it used to be. Here the next
    // page's video is 40 pixels lower, opened in the same tab or in a new one.
    [Theory]
    [MemberData(nameof(NextPages))]
    public void A_followed_area_goes_to_its_control_on_the_next_page(string path, bool newTab)
    {
        const string style = "body{margin:0;background:#fff}video{position:absolute;left:40px;width:320px;height:180px}" +
            "a{position:absolute;left:40px;top:20px;font:20px sans-serif}";
        string first = $"<!doctype html><title>VideoFirst</title><style>{style}video{{top:100px;background:#e8453c}}</style>" +
            $"<a href=\"next.html\"{(newTab ? " target=\"_blank\"" : "")}>Next video</a><video class=\"player-video\" muted></video>";
        string next = $"<!doctype html><title>VideoNext</title><style>{style}video{{top:140px;background:#3c78e8}}</style>" +
            "<video class=\"player-video\" muted></video>";
        var (browser, window, root) = BrowserInputTests.Open(first, "VideoFirst", app: false, path);
        try
        {
            File.WriteAllText(Path.Combine(root, "next.html"), next);
            Rectangle document = Rectangle.Empty;
            Assert.True(Desktop.WaitFor(() => (document = BrowserAccessibility.Read(window, 24).FirstOrDefault(c => c.Type == 50030)?.Bounds ?? Rectangle.Empty)
                .Width > 0, 10000), "No document.");
            var video = new Rectangle(document.Left + 40, document.Top + 100, 320, 180);
            Assert.True(Desktop.WaitFor(() => ElementFinder.Find(window, new Point(video.Left + 160, video.Top + 90)) == video, 10000),
                "The video wasn't found.");
            (KeptArea area, Rectangle frame) = Marked(window, video);
            Assert.True(area.Follows, "The video wasn't found as a control.");
            Rectangle InFrame(Rectangle screen) => screen with { X = screen.X - frame.X, Y = screen.Y - frame.Y };

            using var follower = new KeptAreaFollower(window, () => { });
            KeptArea[] kept = [area];
            follower.SetAreas(kept);
            Rectangle? last = null;
            bool Followed(Rectangle at) => Desktop.WaitFor(() =>
            {
                follower.WaitFor(follower.Ask(), 100);
                return (last = follower.PlacesFor(kept)?[0]) == InFrame(at);
            }, 6000);
            Assert.True(Followed(video), $"Not followed on the first page: {last?.ToString() ?? "where it was marked"}.");

            Desktop.GainForegroundRights();
            Desktop.Activate(window);
            Desktop.ClickOn(new Point(document.Left + 60, document.Top + 32), window);
            Assert.True(Desktop.WaitFor(() => WindowInfo.Title(window).StartsWith("VideoNext"), 10000),
                $"The next page didn't open: {WindowInfo.Title(window)}.");
            Rectangle nextVideo = video with { Y = video.Y + 40 };
            Assert.True(Followed(nextVideo), $"The area stayed at {last?.ToString() ?? "where it was marked"}, not on the next video at {InFrame(nextVideo)}.");
        }
        finally { BrowserInputTests.Close(browser, root); }
    }

    private static Rectangle PictureOf(TargetProcess target)
    {
        Rectangle bounds = Desktop.BoundsOf(target.Handle);
        Rectangle? found = null;
        Assert.True(Desktop.WaitFor(() =>
            (found = ElementFinder.Find(target.Handle, new Point(bounds.Left + 140, bounds.Top + 120)))?.Size == new Size(200, 120), 3000),
            $"The picture control wasn't found: {found?.ToString() ?? "no answer"}.");
        return found!.Value;
    }

    private static (KeptArea Area, Rectangle Frame) Marked(TargetProcess target, Rectangle onScreen) => Marked(target.Handle, onScreen);

    private static (KeptArea Area, Rectangle Frame) Marked(nint window, Rectangle onScreen)
    {
        Assert.True(WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _));
        float scale = User32.GetDpiForWindow(window) / 96f;
        var part = new Rectangle(onScreen.Location - (Size)frame.Location, onScreen.Size);
        return (KeptAreaFollower.Described(KeptArea.Of(part, frame.Size, scale), window, onScreen), frame);
    }

    [Fact]
    public void An_area_marked_over_a_control_follows_it_and_a_dragged_part_of_the_page_does_not()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.AddScrollingPage();
        Rectangle picture = PictureOf(target);

        KeptArea follows = Marked(target, picture).Area;
        Assert.True(follows.Follows);
        Assert.Equal("picture", follows.ControlId);

        // Half on the picture and half on the page: no control covers it nearly enough.
        KeptArea fixedArea = Marked(target, picture with { Width = picture.Width * 2 }).Area;
        Assert.False(fixedArea.Follows);
    }

    [Fact]
    public void A_followed_area_moves_with_its_control_as_the_page_scrolls_and_goes_when_out_of_sight()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.AddScrollingPage();
        Rectangle picture = PictureOf(target);
        (KeptArea area, Rectangle frame) = Marked(target, picture);
        Assert.True(area.Follows);
        var inFrame = new Rectangle(picture.Location - (Size)frame.Location, picture.Size);

        using var follower = new KeptAreaFollower(target.Handle, () => { });
        KeptArea[] kept = [area];
        follower.SetAreas(kept);
        Rectangle? Place() => follower.PlacesFor(kept)?[0];
        void Expect(Rectangle wanted, string when)
        {
            Assert.True(Desktop.WaitFor(() =>
            {
                follower.Ask();
                return Place() == wanted;
            }, 3000), $"{when}: at {Place()?.ToString() ?? "where it was marked"}, not {wanted}.");
        }

        Expect(inFrame, "Before scrolling");
        target.ScrollPageTo(40);
        Expect(inFrame with { Y = inFrame.Y - 40 }, "Scrolled by 40");
        // Partly scrolled off: only what the page still shows.
        target.ScrollPageTo(100);
        Expect(Rectangle.FromLTRB(inFrame.Left, inFrame.Top - 60, inFrame.Right, inFrame.Bottom - 100), "Scrolled by 100");
        target.ScrollPageTo(600);
        Expect(Rectangle.Empty, "Scrolled out of sight");
        target.ScrollPageTo(0);
        Expect(inFrame, "Scrolled back");
    }

    [GraphicsCardFact]
    public void Dark_leaves_a_followed_area_as_it_is_where_its_control_scrolled_to()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        target.AddScrollingPage("E8453C");
        Rectangle picture = PictureOf(target);
        KeptArea area = Marked(target, picture).Area;
        Assert.True(area.Follows);
        Color red = Color.FromArgb(0xE8, 0x45, 0x3C);
        Rectangle Sample(Rectangle part, int fromTop) => new(part.Left + part.Width / 2 - 5, part.Top + fromTop - 5, 10, 10);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            effects.SetKeptAreas(_ => [area]);
            try
            {
                Assert.True(effects.Toggle(target.Handle, ContentLook.Dark));
                bool Kept(Rectangle sample) => Desktop.IsNear(Desktop.ColorOnScreen(sample), red, 16);
                Pump(() => Kept(Sample(picture, 60)));
                Assert.True(Kept(Sample(picture, 60)), $"The picture before scrolling: {Desktop.ColorOnScreen(Sample(picture, 60))}.");

                target.ScrollPageTo(50);
                Rectangle moved = picture with { Y = picture.Y - 50 };
                // The bottom of where the picture was is page now, and dark; the picture is red where it went.
                bool Followed() => Kept(Sample(moved, 20)) && Desktop.ColorOnScreen(Sample(picture, 110)).GetBrightness() < 0.3f;
                Pump(Followed);
                Assert.True(Followed(), $"After scrolling: picture {Desktop.ColorOnScreen(Sample(moved, 20))}, " +
                    $"where it was {Desktop.ColorOnScreen(Sample(picture, 110))}.");
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }

    private static void Pump(Func<bool> until, int milliseconds = 3000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end && !until())
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
