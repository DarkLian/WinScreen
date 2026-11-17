using System.Runtime.InteropServices;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>Where the input method's list goes when typing through a view (10-1): the arithmetic.</summary>
public sealed class InputMethodPlacementTests
{
    [Fact]
    public void The_list_goes_under_the_caret_line_or_over_it_where_there_is_no_room()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Point(100, 122), InputMethodWindows.Below(new Rectangle(100, 100, 1, 20), new Size(160, 300), area));
        Assert.Equal(new Point(100, 598), InputMethodWindows.Below(new Rectangle(100, 900, 1, 20), new Size(160, 300), area));
        // Kept on the screen sideways, and at the bottom when it fits neither way.
        Assert.Equal(new Point(1760, 122), InputMethodWindows.Below(new Rectangle(1900, 100, 1, 20), new Size(160, 300), area));
        Assert.Equal(new Point(100, 0), InputMethodWindows.Below(new Rectangle(100, 10, 1, 20), new Size(160, 1100), area));
    }

    [Fact]
    public void The_caret_is_shown_where_the_view_shows_it_and_kept_inside_the_picture()
    {
        var view = new Rectangle(1000, 200, 480, 375);
        var shown = new Rectangle(7, 0, 886, 693);
        Rectangle line = InputMethodWindows.Mapped(new Rectangle(51, 153, 1, 22), view, shown);
        Assert.Equal(new Rectangle(1024, 283, 1, 12), line);
        // Cropped out of the picture: at its edge, never outside the view.
        Rectangle away = InputMethodWindows.Mapped(new Rectangle(2000, 900, 1, 22), view, shown);
        Assert.True(view.Contains(away.Location), away.ToString());
        Assert.True(away.Bottom <= view.Bottom, away.ToString());
    }

    [Fact]
    public void Only_a_list_beside_the_caret_is_the_one_the_keys_opened()
    {
        Rectangle display = Screen.PrimaryScreen!.Bounds;
        // The original parked past the display's right edge: its caret is off the screen, and Windows pulled the list on.
        var caret = new Rectangle(display.Right + 200, display.Top + 150, 1, 22);
        Assert.True(InputMethodWindows.BesideCaret(new Rectangle(display.Right - 162, display.Top + 157, 162, 359), caret));
        // The touch keyboard along the bottom, and anything across the display, isn't.
        Assert.False(InputMethodWindows.BesideCaret(new Rectangle(display.Left, display.Bottom - 300, display.Width / 2, 300), caret));
        Assert.False(InputMethodWindows.BesideCaret(new Rectangle(display.Left, display.Top + 157, 162, 359), caret));
    }
}

/// <summary>
/// Typing Chinese through a view (10-1, Windows 11): the input method's list comes up beside the caret in the view,
/// not at the edge of the screen where the hidden original is, and can be used there. Needs Microsoft Bopomofo
/// (zh-TW), which the owner's PC has; the language is switched on the test browser only.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class InputMethodViewTests(ITestOutputHelper output)
{
    [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, nint[]? layouts);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);

    private const string Page =
        "<!doctype html><title>Ime</title><body style='margin:0'><textarea id=t style='position:absolute;left:40px;top:60px;width:500px;height:300px;font:20px sans-serif'></textarea>" +
        "<script>t.oninput=()=>document.title='Ime v='+t.value</script>";

    private static bool Cloaked(nint window) => Dwmapi.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool HasBopomofo()
    {
        int count = GetKeyboardLayoutList(0, null);
        var layouts = new nint[Math.Max(count, 1)];
        GetKeyboardLayoutList(layouts.Length, layouts);
        return layouts.Take(count).Any(layout => (layout & 0xFFFF) == 0x0404);
    }

    private static string? Chrome()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The value typed so far, from the page's title.</summary>
    private static string Typed(nint window) => WindowInfo.Title(window) is var title && title.IndexOf("v=", StringComparison.Ordinal) is var at and >= 0 ? title[(at + 2)..] : "";

    private static bool BesideView(Rectangle list, Rectangle view) =>
        list.Left >= view.Left - 4 && list.Left <= view.Right && list.Top >= view.Top && list.Top <= view.Bottom + 4;

    /// <summary>Opens the page in a view, clicks the text box through it and runs <paramref name="steps"/> during the visit.</summary>
    private void Typing(Action<PipWindow, nint, StringBuilder> steps, bool bopomofo)
    {
        if (Environment.OSVersion.Version.Build < 22000) { Skipped.Because("Windows 11's input method list is the one WinScreen moves."); return; }
        if (bopomofo && !HasBopomofo()) { Skipped.Because("Microsoft Bopomofo (zh-TW) isn't installed."); return; }
        if (Chrome() is not { } chrome) { Skipped.Because("The tests' Chrome isn't in test-apps."); return; }
        var log = new StringBuilder();
        var (browser, window, root) = BrowserInputTests.Open(Page, "Ime", app: true, chrome);
        try
        {
            User32.PostMessage(window, 0x112, 0xF120, 0); // Not maximized, so the view is a usable size.
            Thread.Sleep(400);
            User32.SetWindowPos(window, 0, 160, 100, 900, 700, 0x14);
            Thread.Sleep(400);
            Rectangle home = Desktop.BoundsOf(window);
            var client = new NativePoint();
            ClientToScreen(window, ref client);
            Point box = new(client.X - home.Left + 200, client.Y - home.Top + 160);
            if (bopomofo) User32.PostMessage(window, 0x50, 0, LoadKeyboardLayout("00000404", 0)); // This browser only.
            Thread.Sleep(600);
            Exception? failure = null;
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                using var view = new PipWindow(window, new WindowController([])) { AllowInteractive = false };
                view.ShowOnTop();
                view.Location = new Point(1000, 220);
                view.SetInteractive(true);
                Point mouseHome = Desktop.CursorPosition();
                var driver = new Thread(() =>
                {
                    try
                    {
                        Assert.True(view.KeepsUpdating, "Not used directly: " + view.StateDescription);
                        Desktop.SlideMouse(new Point(view.Left + view.Width / 2, view.Top + view.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null), "No visit started: " + view.StateDescription);
                        Thread.Sleep(300);
                        var (area, shown) = view.LiveInteraction!.CurrentGeometry;
                        Point at = new(area.Left + (int)Math.Round((box.X - shown.Left) * (double)area.Width / shown.Width),
                            area.Top + (int)Math.Round((box.Y - shown.Top) * (double)area.Height / shown.Height));
                        Desktop.SlideMouse(at);
                        Thread.Sleep(150);
                        Desktop.MouseButton(true); Thread.Sleep(60); Desktop.MouseButton(false);
                        Thread.Sleep(500);
                        Assert.Equal(window, User32.GetForegroundWindow());
                        log.AppendLine($"parked {Desktop.BoundsOf(window)} view {view.Bounds} picture {area} shown {shown}");
                        steps(view, window, log);
                    }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        Desktop.MouseButton(false);
                        Desktop.Press(Keys.Escape); Thread.Sleep(100); Desktop.Press(Keys.Escape);
                        Desktop.MoveMouse(mouseHome);
                    }
                });
                driver.Start();
                while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
                view.Close();
            });
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure + "\n" + log);
        }
        finally
        {
            if (bopomofo) User32.PostMessage(window, 0x50, 0, LoadKeyboardLayout("00000409", 0));
            BrowserInputTests.Close(browser, root);
            output.WriteLine(log.ToString());
        }
    }

    /// <summary>Waits for the list to be kept beside the caret in the view; its bounds then.</summary>
    private static Rectangle ListBesideView(PipWindow view, StringBuilder log, string step)
    {
        Rectangle list = Rectangle.Empty;
        bool beside = Desktop.WaitFor(() =>
        {
            nint held = view.LiveInteraction?.InputMethod?.HeldWindow ?? 0;
            if (held == 0) return false;
            list = Desktop.BoundsOf(held);
            return BesideView(list, view.Bounds);
        }, 3000);
        log.AppendLine($"{step}: list {list} held 0x{view.LiveInteraction?.InputMethod?.HeldWindow ?? 0:X} moves {view.LiveInteraction?.InputMethod?.Moves} refused '{view.LiveInteraction?.InputMethod?.LastRefusal}'");
        Assert.True(beside, $"{step}: the list isn't beside the caret in the view {view.Bounds}: {list}.");
        return list;
    }

    [Fact]
    public void Typing_chinese_through_a_view_brings_the_list_beside_its_caret_and_a_click_there_picks()
    {
        Typing((view, window, log) =>
        {
            Desktop.Press(Keys.S); Thread.Sleep(80); Desktop.Press(Keys.U); Thread.Sleep(80); Desktop.Press(Keys.D3);
            Assert.True(Desktop.WaitFor(() => Typed(window).Length > 0, 3000), "Nothing typed: " + WindowInfo.Title(window));
            Desktop.Press(Keys.Down); // The list of choices for the character.
            Rectangle list = ListBesideView(view, log, "opened");
            // Windows puts it back at the edge each time it opens again; it comes back beside the caret.
            Desktop.Press(Keys.Escape); Thread.Sleep(300);
            Desktop.Press(Keys.Down);
            list = ListBesideView(view, log, "opened again");
            // The pointer going onto it is still the visit, and a click on the third choice picks it. (Its frame has a
            // shadow round it: the first choice is 36 pixels down a 359-pixel frame at 100%, each one 32 more.)
            string before = Typed(window);
            Point third = new(list.Left + list.Width / 2, list.Top + (int)Math.Round(list.Height * 100.0 / 359));
            Desktop.SlideMouse(third);
            Thread.Sleep(400);
            log.AppendLine($"at {third}: 0x{Desktop.TopWindowAt(third):X} {WindowInfo.ClassName(Desktop.TopWindowAt(third))}");
            Assert.NotNull(view.LiveInteraction);
            Desktop.MouseButton(true); Thread.Sleep(60); Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => Typed(window) != before, 2000), $"The click picked nothing: '{before}' -> '{Typed(window)}'.");
            Assert.NotNull(view.LiveInteraction);
            Assert.Equal(window, User32.GetForegroundWindow());
            log.AppendLine($"picked: '{before}' -> '{Typed(window)}'");
            Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
        }, bopomofo: true);
    }

    [Fact]
    public void The_emoji_panel_opened_through_a_view_comes_up_beside_its_caret()
    {
        Typing((view, window, log) =>
        {
            Desktop.Press(Keys.A);
            Assert.True(Desktop.WaitFor(() => Typed(window) == "a", 2000), "Nothing typed: " + WindowInfo.Title(window));
            Desktop.Press(Keys.OemPeriod, Keys.LWin);
            Rectangle panel = ListBesideView(view, log, "emoji panel");
            nint held = view.LiveInteraction!.InputMethod!.HeldWindow;
            // A press on the page beside it closes it, as anywhere else on the screen, and the visit goes on.
            Point away = new(view.Right - 20, view.Bottom - 20);
            Assert.False(panel.Contains(away), $"The panel {panel} covers the corner of the view {view.Bounds}.");
            Desktop.SlideMouse(away);
            Thread.Sleep(200);
            Desktop.MouseButton(true); Thread.Sleep(60); Desktop.MouseButton(false);
            Assert.True(Desktop.WaitFor(() => !User32.IsWindowVisible(held) || Cloaked(held), 2000), "The emoji panel stayed open.");
            Assert.NotNull(view.LiveInteraction);
            Assert.Equal("a", Typed(window));
        }, bopomofo: false);
    }
}
