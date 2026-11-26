using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// The owner, 26 Nov 2026: with Keep hidden on, Cover parts couldn't be used once the pointer was over the view, and
// Copy text's picker closed during a drag or with nothing done. Real pointer moves, as a person makes them.
[Trait("Category", "RealWindows")]
public sealed class ViewPickerPointerTests
{
    private static void Drive(nint source, Action<PipWindow, Func<Action, bool>> steps)
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            using var view = new PipWindow(source, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 300);
            view.SetInteractive(true);
            view.SetEnhancedHover(true);
            Assert.True(view.KeepsUpdating, "The original wasn't hidden.");
            Assert.True(Desktop.WaitFor(() => { Application.DoEvents(); return view.CanTurn || view.BackendKind == PipBackendKind.DwmThumbnail; }, 15000));
            bool Ui(Action action)
            {
                view.Invoke(action);
                return true;
            }
            Exception? failure = null;
            Point home = Desktop.CursorPosition();
            var driver = new Thread(() =>
            {
                try { steps(view, Ui); }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    Desktop.MouseButton(false);
                    Desktop.MoveMouse(home);
                }
            });
            driver.Start();
            while (driver.IsAlive)
            {
                Application.DoEvents();
                Thread.Sleep(2);
            }
            if (!view.IsDisposed) view.Close();
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        });
    }

    private static Rectangle PictureOnScreen(PipWindow view) => view.Invoke(() =>
        view.RectangleToScreen((Rectangle)typeof(PipWindow).GetProperty("PictureArea",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!));

    private static nint RootAt(Point point) =>
        User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);

    // Over the picture for a while, as a person looking for where to start; the overlay must stay open and on top.
    private static void Wander(PipWindow view, Form overlay, Rectangle picture)
    {
        var spots = new[]
        {
            new Point(picture.Left + picture.Width / 4, picture.Top + picture.Height / 4),
            new Point(picture.Right - picture.Width / 4, picture.Top + picture.Height / 3),
            new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2),
            new Point(picture.Left + picture.Width / 3, picture.Bottom - picture.Height / 4),
        };
        nint handle = view.Invoke(() => overlay.Handle);
        Rectangle frame = view.Invoke(() => view.Bounds);
        foreach (Point spot in spots.Prepend(new Point(frame.Left + frame.Width / 2, frame.Top + 6)))
        {
            // Out of the view for long enough that its title bar goes, then back in.
            Desktop.SlideMouse(new Point(frame.Left - 60, frame.Top + frame.Height / 2));
            Thread.Sleep(1500);
            Desktop.SlideMouse(spot);
            Thread.Sleep(700);
            bool open = view.Invoke(() => !overlay.IsDisposed && overlay.Visible);
            Assert.True(open, $"The overlay closed with the pointer at {spot}; in front {Desktop.FrontDescription()}; " +
                $"visit {view.LiveInteraction is not null}; last visit ended {view.LastVisitEnd}.");
            nint under = RootAt(spot);
            Assert.True(under == handle || WindowInfo.IsOwnedBy(under, handle), $"At {spot} the pointer is over {WindowInfo.ClassName(under)} '{WindowInfo.Title(under)}' " +
                $"(0x{under:X}), not the overlay (0x{handle:X}); view 0x{view.Invoke(() => view.Handle):X}; " +
                $"visit {view.LiveInteraction is not null}; in front {Desktop.FrontDescription()}.");
            Assert.Null(view.LiveInteraction);
            Assert.True(User32.GetForegroundWindow() == handle, $"In front: {Desktop.FrontDescription()}.");
        }
    }

    private static void Drag(Point from, Point to, Action whileHeld)
    {
        Desktop.MoveMouse(from);
        Thread.Sleep(80);
        Desktop.MouseButton(true);
        Thread.Sleep(80);
        Desktop.SlideMouse(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Thread.Sleep(700); // Held still part way, as a person lining up the far corner.
        whileHeld();
        Desktop.SlideMouse(to);
        Thread.Sleep(80);
        Desktop.MouseButton(false);
        Thread.Sleep(200);
    }

    private static void WithSource(bool browser, string title, Action<nint> test)
    {
        if (browser && !File.Exists(@"C:\Program Files\Google\Chrome\Application\chrome.exe") &&
            !File.Exists(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"))
        {
            Skipped.Because("No Chrome or Edge.");
            return;
        }
        if (!browser)
        {
            using var target = new TargetProcess(new Rectangle(160, 160, 460, 340), "C81E1E,1E64C8", title);
            test(target.Handle);
            return;
        }
        (System.Diagnostics.Process process, nint window, string root) = BrowserInputTests.Open(
            $"<!doctype html><title>{title}</title><body style='margin:0;background:#c81e1e'><p style='font:28px sans-serif;color:white'>Some words to copy</p></body>", title, app: false,
            path: new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }
                .FirstOrDefault(File.Exists));
        try
        {
            User32.SetWindowPos(window, 0, 160, 160, 700, 500, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            test(window);
        }
        finally { BrowserInputTests.Close(process, root); }
    }

    [WgcTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cover_parts_stays_usable_with_the_pointer_over_a_view_that_keeps_its_window_hidden(bool browser) =>
        WithSource(browser, "Cover pointer source", source => Drive(source, (view, ui) =>
        {
            Rectangle picture = PictureOnScreen(view);
            Desktop.SlideMouse(new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2));
            Thread.Sleep(500); // A visit, as when the person goes to the view's menu.
            ui(() => view.Run(AppCommand.ViewCover));
            Thread.Sleep(300);
            KeptAreaOverlay editor = view.CoverEditor ?? throw new InvalidOperationException("No cover editor.");
            Wander(view, editor, picture);
            Drag(new Point(picture.Left + 30, picture.Top + 30), new Point(picture.Left + 140, picture.Top + 110), () =>
                Assert.True(view.Invoke(() => !editor.IsDisposed && editor.Visible), $"Closed during the drag; in front {Desktop.FrontDescription()}."));
            Assert.Single(view.Invoke(() => editor.Areas.ToArray()));
            Desktop.Press(Keys.Enter);
            Assert.True(Desktop.WaitFor(() => view.Covers.Count == 1, 2000), $"Covers: {view.Covers.Count}.");
        }));

    [WgcTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copy_text_stays_open_until_a_part_is_chosen_with_the_pointer_over_a_view_that_keeps_its_window_hidden(bool browser) =>
        WithSource(browser, "Copy text pointer source", source => Drive(source, (view, ui) =>
        {
            Rectangle picture = PictureOnScreen(view);
            Desktop.SlideMouse(new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2));
            Thread.Sleep(500);
            ui(() => view.Run(AppCommand.ViewCopyText));
            Thread.Sleep(300);
            ViewAreaPicker picker = view.AreaPicker ?? throw new InvalidOperationException("No area picker.");
            Wander(view, picker, picture);
            Thread.Sleep(2500); // Doing nothing.
            Assert.True(view.Invoke(() => !picker.IsDisposed && picker.Visible), $"Closed while left alone; in front {Desktop.FrontDescription()}.");
            Drag(new Point(picture.Left + 30, picture.Top + 30), new Point(picture.Left + 140, picture.Top + 110), () =>
                Assert.True(view.Invoke(() => !picker.IsDisposed && picker.Visible), $"Closed during the drag; in front {Desktop.FrontDescription()}."));
        }));
}
