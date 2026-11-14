using System.Diagnostics;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Fill display, then the app's own maximize or restore button: the fill is over, so WinScreen gives the frame back
/// and leaves the app as it chose to be. Before, a browser came out of it as a window that couldn't be moved (the
/// owner's report on 1.1.0-beta, F6).
/// </summary>
[Trait("Category", "Interactive")]
public sealed class FillDisplayDriftTests
{
    private const int HTMAXBUTTON = 9, HTCAPTION = 2, WM_NCHITTEST = 0x0084;

    private static int Hit(nint window, int x, int y)
    {
        User32.SendMessageTimeout(window, WM_NCHITTEST, 0, (nint)((y << 16) | (x & 0xFFFF)), 2, 500, out nint answer);
        return (int)answer;
    }

    private static Point? Find(nint window, int hit, bool fromRight)
    {
        Rectangle bounds = Desktop.BoundsOf(window);
        for (int y = bounds.Top + 6; y < bounds.Top + 50; y += 2)
            for (int i = 0; i < bounds.Width - 12; i += 4)
            {
                int x = fromRight ? bounds.Right - 6 - i : bounds.Left + bounds.Width / 3 + i;
                if (!fromRight && x >= bounds.Right - 6) break;
                if (Hit(window, x, y) == hit && (hit != HTCAPTION || (Hit(window, x + 30, y) == HTCAPTION && Hit(window, x, y + 6) == HTCAPTION)))
                    return new Point(hit == HTCAPTION ? x + 15 : x - 6, y + (hit == HTCAPTION ? 3 : 0));
            }
        return null;
    }

    private static void Click(Point point, nint window)
    {
        Desktop.RequireWindowAt(point, window);
        Desktop.MoveMouse(point);
        Thread.Sleep(80);
        Desktop.MouseButton(true);
        Desktop.MouseButton(false);
    }

    [Fact]
    public void A_browser_filled_then_maximized_and_restored_with_its_own_button_can_be_moved()
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        var (browser, window, root) = BrowserInputTests.Open("<!doctype html><title>FillProbe</title><body style='background:#fff'>", "FillProbe", app: false, chrome);
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                Desktop.GainForegroundRights();
                Desktop.Activate(window);
                var windows = new WindowController([]);
                try
                {
                    Assert.True(windows.ToggleBorderless(window, fill: true));
                    Rectangle display = Screen.FromHandle(window).Bounds;
                    Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window) == display, 3000, Application.DoEvents));

                    // The browser's own maximize button, twice: maximized, then restored.
                    for (int press = 0; press < 2; press++)
                    {
                        Point? button = Find(window, HTMAXBUTTON, fromRight: true);
                        Assert.True(button is not null, $"The browser shows no maximize button (press {press}).");
                        bool zoomed = User32.IsZoomed(window);
                        Click(button!.Value, window);
                        Desktop.WaitFor(() => User32.IsZoomed(window) != zoomed, 2000, Application.DoEvents);
                        long end = Environment.TickCount64 + 600;
                        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                        windows.Refresh(Cursor.Position, false, false);
                    }
                    if (User32.IsZoomed(window)) User32.ShowWindow(window, User32.SW_RESTORE);
                    Assert.False(windows.IsBorderless(window), "WinScreen still thinks the window fills its display.");

                    // Smaller than the display, so it can be dragged by its title bar.
                    User32.SetWindowPos(window, 0, 160, 100, 900, 640, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                    Thread.Sleep(400);
                    Rectangle before = Desktop.BoundsOf(window);
                    Point? title = Find(window, HTCAPTION, fromRight: false);
                    Assert.True(title is not null, "The browser shows no title bar to drag.");
                    Desktop.RequireWindowAt(title!.Value, window);
                    Desktop.MoveMouse(title.Value);
                    Thread.Sleep(50);
                    Desktop.MouseButton(true);
                    for (int i = 1; i <= 8; i++)
                    {
                        Desktop.MoveMouse(new Point(title.Value.X + 80 * i / 8, title.Value.Y + 50 * i / 8));
                        Thread.Sleep(20);
                        Application.DoEvents();
                    }
                    Thread.Sleep(150);
                    Desktop.MouseButton(false);
                    Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(window).Location != before.Location, 2000, Application.DoEvents),
                        $"The browser couldn't be moved by its title bar (style 0x{User32.GetWindowLongPtr(window, User32.GWL_STYLE):X}).");
                }
                finally { windows.RestoreAll(); }
            });
        }
        finally { BrowserInputTests.Close(browser, root); }
    }
}
