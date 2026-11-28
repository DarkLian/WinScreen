using System.Diagnostics;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The real WinScreen.exe with two views of browser pages that don't change, opened from the command line as the
/// owner had them (29 Nov, 1.5.3-beta.9): with the pointer gliding between them, or one dragged by its title bar onto
/// the other, they kept taking turns on top. The pointer on a view of a page that doesn't change shows its chip ("No new
/// picture"); the chip came up at the very top and Windows brought its view up with it. Watched every 5 ms on another
/// thread, as a turn on top can be put right again a moment later.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class ViewOrderEndToEndTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "WinScreenTest-" + Path.GetRandomFileName());
    private Process? _app;

    private static string Program =>
        Environment.GetEnvironmentVariable("WINSCREEN_EXE") ?? Path.Combine(AppContext.BaseDirectory, "WinScreen.exe");

    private int Run(string command, string window)
    {
        var start = new ProcessStartInfo(Program) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment[AppRoles.DataFolderVariable] = _data;
        foreach (string argument in new[] { "--run", command, "--window", window }) start.ArgumentList.Add(argument);
        using Process cli = Process.Start(start)!;
        Assert.True(cli.WaitForExit(15000), "The command line didn't finish.");
        return cli.ExitCode;
    }

    private string AppLog() => File.Exists(Path.Combine(_data, "winscreen.log"))
        ? string.Join(Environment.NewLine, ReadShared(Path.Combine(_data, "winscreen.log")).Split('\n').Where(line => line.Contains("view.restacked")).TakeLast(12))
        : "";

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void Two_views_keep_their_order_under_the_pointer_and_while_one_is_dragged_onto_the_other()
    {
        string chrome = Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe");
        if (!File.Exists(chrome)) { Skipped.Because("The portable Chrome isn't in the test output."); return; }
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0, "Close the running WinScreen before these tests.");
        Directory.CreateDirectory(_data);
        new AppSettings { WelcomeShown = true, ShowToolbar = false }.Save(Path.Combine(_data, "settings.json"));
        Setup.QuietNotices(_data);
        var (firstBrowser, firstPage, firstRoot) = BrowserInputTests.Open("<!doctype html><title>OrderA</title><body style='background:#1e6'>", "OrderA", app: false, chrome);
        var (secondBrowser, secondPage, secondRoot) = BrowserInputTests.Open("<!doctype html><title>OrderB</title><body style='background:#16e'>", "OrderB", app: false, chrome);
        try
        {
            var start = new ProcessStartInfo(Program) { UseShellExecute = false };
            start.Environment[AppRoles.DataFolderVariable] = _data;
            _app = Process.Start(start)!;
            Assert.True(Desktop.WaitFor(() => Desktop.FindWindow(null, HotkeyHost.WindowTitle) != 0, 10000), "WinScreen didn't start.");
            Thread.Sleep(1000);
            Assert.Equal(0, Run("OpenPip", "title:OrderA"));
            Thread.Sleep(1000);
            Assert.Equal(0, Run("OpenPip", "title:OrderB"));
            Thread.Sleep(2000);
            var views = new List<nint>();
            Desktop.EachProcessWindow(_app.Id, window =>
            {
                if (Desktop.IsWindowVisible(window) && Desktop.BoundsOf(window) is { Width: > 200, Height: > 150 }) views.Add(window);
            });
            Assert.True(views.Count == 2, $"{views.Count} views open, not two.");
            // a lower and overlapping, b over it: b was opened last.
            nint a = views[0], b = views[1];
            if (Desktop.IsAbove(a, b)) (a, b) = (b, a);
            User32.SetWindowPos(a, 0, 200, 150, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            User32.SetWindowPos(b, 0, 380, 300, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Thread.Sleep(800);
            Rectangle ra = Desktop.BoundsOf(a), rb = Desktop.BoundsOf(b);
            Assert.True(Desktop.IsAbove(b, a), "The view opened last isn't on top.");

            bool stop = false;
            var turns = new List<string>();
            var watch = Stopwatch.StartNew();
            var watcher = new Thread(() =>
            {
                bool? last = null;
                while (!Volatile.Read(ref stop))
                {
                    bool bOver = Desktop.IsAbove(b, a);
                    if (bOver != last)
                    {
                        if (last is not null) lock (turns) turns.Add($"{watch.ElapsedMilliseconds} ms: {(bOver ? "b" : "a")} on top");
                        last = bOver;
                    }
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            watcher.Start();
            Point aOwn = new(ra.Left + 40, ra.Top + 40), bOwn = new(rb.Right - 40, rb.Bottom - 40);
            Point away = new(Screen.PrimaryScreen!.Bounds.Right - 60, Screen.PrimaryScreen.Bounds.Bottom - 200);
            void Glide(Point from, Point to)
            {
                for (int i = 0; i <= 20; i++)
                {
                    Desktop.MoveMouse(new Point(from.X + (to.X - from.X) * i / 20, from.Y + (to.Y - from.Y) * i / 20));
                    Thread.Sleep(40);
                }
            }
            for (int round = 0; round < 2; round++)
            {
                Glide(away, aOwn); Thread.Sleep(700);
                Glide(aOwn, bOwn); Thread.Sleep(700);
                Glide(bOwn, aOwn); Thread.Sleep(700);
                Glide(aOwn, away); Thread.Sleep(1200);
            }
            lock (turns) Assert.True(turns.Count == 0, "The pointer changed which view is on top:" + Environment.NewLine +
                string.Join(Environment.NewLine, turns) + Environment.NewLine + AppLog());

            // The lower view dragged by its title bar onto the other: pressed, it comes up, and stays up all the way.
            Glide(away, aOwn);
            nint bar = 0;
            Assert.True(Desktop.WaitFor(() =>
            {
                Desktop.EachProcessWindow(_app.Id, window =>
                {
                    if (Desktop.IsWindowVisible(window) && Desktop.BoundsOf(window) is var r && r.Bottom == ra.Top && r.Left == ra.Left && r.Height < 60) bar = window;
                });
                return bar != 0;
            }, 3000), "The title bar didn't come up.");
            Rectangle barBounds = Desktop.BoundsOf(bar);
            Point grip = new(barBounds.Left + 60, barBounds.Top + barBounds.Height / 2);
            Glide(aOwn, grip);
            Thread.Sleep(300);
            Desktop.MouseButton(true);
            Assert.True(Desktop.WaitFor(() => Desktop.IsAbove(a, b), 1500), "The view pressed by its title bar didn't come up.");
            lock (turns) turns.Clear();
            Glide(grip, new Point(grip.X + 300, grip.Y + 240));
            Thread.Sleep(700);
            Desktop.MouseButton(false);
            Thread.Sleep(500);
            Volatile.Write(ref stop, true);
            watcher.Join();
            Assert.True(turns.Count == 0, "Dragging one view onto the other changed which is on top:" + Environment.NewLine +
                string.Join(Environment.NewLine, turns) + Environment.NewLine + AppLog());
            Assert.True(Desktop.IsAbove(a, b), "The view dragged went under the other once let go.");
        }
        finally
        {
            Desktop.MouseButton(false);
            BrowserInputTests.Close(firstBrowser, firstRoot);
            BrowserInputTests.Close(secondBrowser, secondRoot);
        }
    }

    public void Dispose()
    {
        if (_app is { HasExited: false })
        {
            nint host = Desktop.FindWindow(null, HotkeyHost.WindowTitle);
            if (host != 0) Desktop.PostMessage(host, 0x0010, 0, 0);
            if (!_app.WaitForExit(8000)) _app.Kill();
        }
        _app?.Dispose();
        try { Directory.Delete(_data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
