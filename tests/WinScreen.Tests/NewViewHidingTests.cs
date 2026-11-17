using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A new view hides its original (Keep hidden), as the tray does for every view a person opens: browsers stop
/// drawing while another window covers them, and a view of a shown original froze with them (the owner, 16 Nov:
/// a video's view stopped once VS Code came forward). What can't be hidden opens its view as before, unsaid, and
/// whatever else the person changed on the window is still there when the view lets it go.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class NewViewHidingTests
{
    private static readonly Rectangle Spot = new(200, 160, 520, 380);

    private const string Page = "<!doctype html><title>CoveredView</title><body style='margin:0'><script>" +
        "const c=['#c81e1e','#1ec81e','#1e1ec8','#c8c81e'];let i=0;setInterval(()=>{document.body.style.background=c[i++%4]},300);</script>";

    private static Color At(Point p)
    {
        using var bitmap = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(p, Point.Empty, new Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }

    /// <summary>How many of ten looks, a third of a second apart, found the view's picture changed.</summary>
    private static int Changes(PipWindow view)
    {
        Point spot = new(view.Bounds.Left + view.Bounds.Width / 2, view.Bounds.Top + view.Bounds.Height * 2 / 3);
        int changes = 0;
        Color last = At(spot);
        for (int look = 0; look < 10; look++)
        {
            TestDesktop.Pump(350);
            Color now = At(spot);
            if (now != last) changes++;
            last = now;
        }
        return changes;
    }

    /// <summary>A view on the right of the screen, clear of the original and of whatever covers it.</summary>
    private static PipWindow OpenView(nint window, WindowController windows)
    {
        var view = new PipWindow(window, windows);
        view.ShowOnTop();
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        view.Bounds = new Rectangle(area.Right - 360, area.Top + 120, 320, 240);
        TestDesktop.Pump(300);
        return view;
    }

    [Fact]
    public void A_view_of_a_covered_browser_froze_with_it_and_keeps_changing_once_the_original_is_hidden()
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        var (browser, window, root) = BrowserInputTests.Open(Page, "CoveredView", app: true, chrome);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                Desktop.GainForegroundRights();
                var windows = new WindowController([]);
                if (User32.IsZoomed(window)) User32.ShowWindow(window, User32.SW_RESTORE);
                User32.SetWindowPos(window, 0, Spot.Left, Spot.Top, Spot.Width, Spot.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                using var view = OpenView(window, windows);
                try
                {
                    TestDesktop.Pump(1500);
                    Assert.True(Changes(view) >= 5, "The view didn't follow the page before anything covered it.");

                    // Another app comes forward over the whole browser window, as VS Code did over the video.
                    Rectangle browserBounds = Desktop.BoundsOf(window);
                    browserBounds.Inflate(40, 40);
                    using var cover = new TargetProcess(browserBounds, "E8E8E8", "Covering window");
                    Desktop.Activate(cover.Handle);
                    TestDesktop.Pump(2000);
                    int covered = Changes(view);
                    Assert.True(covered <= 2, $"The covered browser kept drawing ({covered} changes in 10 looks), so this test shows nothing.");

                    view.HideOriginalByDefault();
                    Assert.True(view.KeepsUpdating, "The view didn't hide its original.");
                    TestDesktop.Pump(1500);
                    int hidden = Changes(view);
                    Assert.True(hidden >= 5, $"The view stayed frozen with its original hidden: {hidden} changes in 10 looks.");
                }
                finally { windows.RestoreAll(); }
            });
        }
        finally { BrowserInputTests.Close(browser, root); }
    }

    [Fact]
    public void A_window_filled_with_fill_display_opens_its_view_shown_and_says_nothing()
    {
        using var target = new TargetProcess(Spot, "C81E1E");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Desktop.Activate(target.Handle);
                Assert.True(windows.ToggleBorderless(target.Handle, fill: true));
                Assert.True(Desktop.WaitFor(() => WindowInfo.FillsMonitor(target.Handle), 2000), "Fill display didn't fill.");
                using var view = new PipWindow(target.Handle, windows);
                var problems = new List<string>();
                view.Problem += problems.Add;
                view.ShowOnTop();
                view.HideOriginalByDefault();
                TestDesktop.Pump(300);
                Assert.Empty(problems);
                Assert.False(view.KeepsUpdating, "Keep hidden reads as on for a window that wasn't hidden.");
                Assert.False(windows.IsHidden(target.Handle));
                Assert.True(windows.IsBorderless(target.Handle), "Fill display was let go of.");

                // Asked for by the person, it still says why it can't.
                view.SetOriginalHidden(true);
                Assert.NotEmpty(problems);
            }
            finally { windows.RestoreAll(); }
        });
    }

    /// <summary>
    /// Minimizing the window in front (the owner: VS Code), or the view itself, makes Windows give the focus to the
    /// next window in line, and a hidden original, lifted above the others, was that window. Windows' hand-off isn't
    /// the person asking for the original: it stays hidden, and Keep hidden stays on.
    /// </summary>
    [Theory]
    [InlineData("another window")]
    [InlineData("the view")]
    public void Minimizing_a_window_leaves_a_hidden_original_hidden(string minimized)
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        using var other = new TargetProcess(new Rectangle(300, 240, 420, 300), "1E64C8", "Window in front");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Desktop.Activate(source.Handle);
                using var view = OpenView(source.Handle, windows);
                view.HideOriginalByDefault();
                Assert.True(view.KeepsUpdating, "The view didn't hide its original.");
                if (minimized == "another window")
                {
                    Desktop.Activate(other.Handle);
                    TestDesktop.Pump(800);
                    const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020; // What its minimize button sends.
                    Desktop.PostMessage(other.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
                }
                else
                {
                    Desktop.Activate(view.Handle);
                    TestDesktop.Pump(800);
                    view.SuspendView();
                }
                TestDesktop.Pump(1500);
                nint front = User32.GetForegroundWindow();
                Assert.True(view.KeepsUpdating && windows.IsHidden(source.Handle),
                    $"Minimizing {minimized} showed the hidden original (foreground: {WindowInfo.Title(front)}).");
                Assert.NotEqual(source.Handle, front);
            }
            finally { windows.RestoreAll(); }
        });
    }

    /// <summary>
    /// Owner, 17 Nov: closing one Chrome window gave the hidden Chrome window the focus and showed it. An app can hand
    /// its own next window the focus a moment before the closing one is gone, so at the moment of arrival the window
    /// before still looked in use. Since 18 Nov the original stays hidden however the focus came to it, unless the
    /// person picked it from the taskbar or switcher: the window before closing, staying, hiding or going out of
    /// sight makes no difference.
    /// </summary>
    [Theory]
    [InlineData("closes")]
    [InlineData("stays")]
    [InlineData("WinScreen's own hides")] // A view's title bar or menu as the view minimizes (owner, 17 Nov).
    [InlineData("a surface is cloaked")] // The taskbar's hidden-icons panel closing as Settings opens (owner, 18 Nov).
    public void A_window_that_hands_the_original_the_focus_as_it_closes_leaves_it_hidden(string what)
    {
        using var source = new TargetProcess(Spot, "C81E1E", "Hidden source");
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            bool closes = what != "stays";
            using var closing = new Form { Text = "Closing window", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(320, 260, 420, 300) };
            try
            {
                Desktop.Activate(source.Handle);
                using var view = OpenView(source.Handle, windows);
                view.HideOriginalByDefault();
                Assert.True(view.KeepsUpdating, "The view didn't hide its original.");
                closing.Show();
                Desktop.Activate(closing.Handle);
                TestDesktop.Pump(800);
                Assert.Equal(closing.Handle, User32.GetForegroundWindow());

                User32.SetForegroundWindow(source.Handle); // The app hands its other window the focus first...
                TestDesktop.Pump(120);
                if (what == "closes") closing.Close(); // ...and closes this one a moment later.
                else if (what == "a surface is cloaked")
                {
                    const int DWMWA_CLOAK = 13; // Windows' panels go out of sight this way rather than hiding.
                    int on = 1;
                    Dwmapi.DwmSetWindowAttribute(closing.Handle, DWMWA_CLOAK, in on, sizeof(int));
                }
                else if (closes) closing.Hide(); // The test runs in WinScreen's place: this form is one of its own.
                TestDesktop.Pump(1200);
                bool hidden = view.KeepsUpdating && windows.IsHidden(source.Handle);
                if (closes) Assert.True(hidden, "The hand-off from a closing window showed the hidden original.");
                else Assert.True(hidden, "Brought forward by a window that stayed, the original was shown: only the person picking it shows it.");
            }
            finally { windows.RestoreAll(); }
        });
    }

    public static TheoryData<string> ChangesMade() => new() { "pin", "opacity", "clickthrough", "crop", "rollup" };

    /// <summary>
    /// What the person changed on a window stays through its view hiding it and letting it go: the tick, and the
    /// window itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChangesMade))]
    public void A_change_on_the_window_holds_through_its_view_hiding_it_and_letting_it_go(string change)
    {
        using var target = new TargetProcess(Spot, "C81E1E");
        target.UseFrame(); // A standard title bar, which Roll up needs.
        // A Windows Forms form with a frame puts its own shape back, so the crop is tried on a window that keeps one.
        nint window = change == "crop" ? target.CreatePlainWindow(Spot, framed: true) : target.Handle;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            try
            {
                Desktop.Activate(window);
                switch (change)
                {
                    case "pin": Assert.True(windows.TogglePin(window)); break;
                    case "opacity": Assert.Equal((byte)128, windows.SetOpacity(window, 128)); break;
                    case "clickthrough": Assert.True(windows.ToggleClickThrough(window)); break;
                    case "crop": windows.Crop(window, new Rectangle(20, 20, 260, 180)); break;
                    case "rollup": Assert.True(windows.ToggleRollUp(window)); break;
                }
                Rectangle before = Desktop.BoundsOf(window);

                var view = OpenView(window, windows);
                view.HideOriginalByDefault();
                Assert.True(view.KeepsUpdating, $"A {change} window wasn't hidden by its new view.");
                Assert.Equal((byte?)0, Desktop.Alpha(window));
                view.Close();
                view.Dispose();
                Assert.True(Desktop.WaitFor(() => !windows.IsHidden(window) && Desktop.Alpha(window) is null or > 1, 2000, Application.DoEvents),
                    "Closing the view didn't show the window again.");
                TestDesktop.Pump(400);

                Assert.Equal(before, Desktop.BoundsOf(window));
                switch (change)
                {
                    case "pin":
                        Assert.True(windows.IsPinned(window));
                        Assert.NotEqual(0, Desktop.ExStyle(window) & User32.WS_EX_TOPMOST);
                        break;
                    case "opacity":
                        Assert.Equal((byte?)128, Desktop.Alpha(window));
                        break;
                    case "clickthrough":
                        Assert.True(windows.IsClickThrough(window));
                        Assert.NotEqual(0, Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT);
                        break;
                    case "crop":
                        Assert.True(windows.IsCropped(window));
                        Assert.NotNull(WindowInfo.ShapeOf(window));
                        break;
                    case "rollup":
                        Assert.True(windows.IsRolledUp(window));
                        break;
                }
                if (change is not ("pin" or "clickthrough"))
                {
                    Assert.Equal(0, Desktop.ExStyle(window) & User32.WS_EX_TOPMOST);
                    Assert.Equal(0, Desktop.ExStyle(window) & User32.WS_EX_TRANSPARENT);
                }
            }
            finally { windows.RestoreAll(); }
        });
    }
}
