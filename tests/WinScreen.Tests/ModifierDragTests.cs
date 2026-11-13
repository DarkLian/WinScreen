using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class ModifierHookPolicyTests
{
    [Theory]
    [InlineData(true, FullScreenState.None, false, false, false, true)]
    [InlineData(false, FullScreenState.None, false, false, false, false)]
    [InlineData(true, FullScreenState.Busy, false, false, false, false)]
    [InlineData(true, FullScreenState.Exclusive, false, false, false, false)]
    [InlineData(true, FullScreenState.Presentation, false, false, false, false)]
    [InlineData(true, FullScreenState.None, true, false, false, false)]
    [InlineData(true, FullScreenState.None, false, true, false, false)]
    [InlineData(true, FullScreenState.None, false, false, true, false)]
    public void The_hook_exists_only_while_it_could_act(bool on, FullScreenState state, bool fills, bool excluded, bool away, bool expected) =>
        Assert.Equal(expected, ModifierHookPolicy.ShouldRun(on, state, fills, excluded, away));

    private static readonly Rectangle Window = new(100, 100, 300, 300);

    [Theory]
    [InlineData(110, 110, ResizeEdges.Left | ResizeEdges.Top)]
    [InlineData(390, 390, ResizeEdges.Right | ResizeEdges.Bottom)]
    [InlineData(250, 110, ResizeEdges.Top)]
    [InlineData(110, 250, ResizeEdges.Left)]
    [InlineData(250, 250, ResizeEdges.Right | ResizeEdges.Bottom)]
    public void A_resize_grabs_the_nearest_edge_or_corner(int x, int y, ResizeEdges expected) =>
        Assert.Equal(expected, PlacementGeometry.EdgesAt(new Point(x, y), Window));

    [Fact]
    public void Resizing_moves_only_the_grabbed_edges_and_stops_at_the_smallest_size()
    {
        Assert.Equal(new Rectangle(100, 100, 350, 300),
            PlacementGeometry.Resized(Window, ResizeEdges.Right, new Size(50, 40), new Size(120, 60)));
        Assert.Equal(new Rectangle(150, 140, 250, 260),
            PlacementGeometry.Resized(Window, ResizeEdges.Left | ResizeEdges.Top, new Size(50, 40), new Size(120, 60)));
        Assert.Equal(new Rectangle(280, 100, 120, 300),
            PlacementGeometry.Resized(Window, ResizeEdges.Left, new Size(900, 0), new Size(120, 60)));
    }

    private static readonly Rectangle Display = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(0, 500, SnapZone.Left)]
    [InlineData(1919, 500, SnapZone.Right)]
    [InlineData(900, 0, SnapZone.Maximize)]
    [InlineData(0, 20, SnapZone.TopLeft)]
    [InlineData(1919, 1070, SnapZone.BottomRight)]
    [InlineData(900, 1079, SnapZone.None)]
    [InlineData(900, 500, SnapZone.None)]
    [InlineData(5, 500, SnapZone.None)]
    public void A_window_snaps_only_with_the_pointer_at_an_edge(int x, int y, SnapZone expected) =>
        Assert.Equal(expected, PlacementGeometry.SnapZoneAt(new Point(x, y), Display, true, true, true));

    [Fact]
    public void An_edge_with_another_display_past_it_doesnt_snap()
    {
        Assert.Equal(SnapZone.None, PlacementGeometry.SnapZoneAt(new Point(1919, 500), Display, true, true, false));
        Assert.Equal(SnapZone.None, PlacementGeometry.SnapZoneAt(new Point(900, 0), Display, true, false, true));
    }

    [Fact]
    public void Snaps_fill_halves_and_quarters_of_the_work_area()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Rectangle(0, 0, 960, 1040), PlacementGeometry.SnapFrame(SnapZone.Left, area));
        Assert.Equal(new Rectangle(960, 0, 960, 1040), PlacementGeometry.SnapFrame(SnapZone.Right, area));
        Assert.Equal(new Rectangle(960, 520, 960, 520), PlacementGeometry.SnapFrame(SnapZone.BottomRight, area));
        Assert.Equal(area, PlacementGeometry.SnapFrame(SnapZone.Maximize, area));
    }

    [Fact]
    public void A_window_coming_out_of_maximized_keeps_the_grabbed_point_under_the_pointer()
    {
        // Grabbed three quarters across and a tenth down a 1920 by 1000 window, now 800 by 600.
        Rectangle restored = PlacementGeometry.RestoredUnder(new Point(1500, 150), new Point(1440, 100),
            new Rectangle(0, 0, 1920, 1000), new Size(800, 600));
        Assert.Equal(new Rectangle(900, 90, 800, 600), restored);
    }

    [Fact]
    public void It_is_off_until_asked_for_and_damaged_settings_keep_it_safe()
    {
        Assert.False(new AppSettings().ModifierDrag);
        Assert.True(new AppSettings().SnapWhileDragging);
        string path = Path.Combine(Directory.CreateTempSubdirectory("winscreen-modifier").FullName, "settings.json");
        File.WriteAllText(path, "{\"ModifierKey\":9,\"ModifierExclusions\":null}");
        AppSettings loaded = AppSettings.Load(path);
        Assert.Equal(ModifierKey.Alt, loaded.ModifierKey);
        Assert.NotEmpty(loaded.ModifierExclusions);
    }
}

[Trait("Category", "RealWindows")]
public sealed class ModifierHookLifetimeTests
{
    private static int HookThreads() => Process.GetCurrentProcess().Threads.Count;

    [Fact]
    public void Menu_only_uses_no_mover_and_can_enable_and_disable_moving_without_another_hook()
    {
        using var gestures = new MouseGestures(_ => false, _ => { }) { MoveEnabled = false, MenuEnabled = true };
        var mover = typeof(MouseGestures).GetField("_mover", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var hook = typeof(MouseGestures).GetField("_hookThread", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        gestures.SetActive(true);
        object? originalHook = hook.GetValue(gestures);
        Assert.NotNull(originalHook);
        Assert.Null(mover.GetValue(gestures));
        gestures.MoveEnabled = true;
        gestures.SetActive(true);
        Assert.Same(originalHook, hook.GetValue(gestures));
        Assert.NotNull(mover.GetValue(gestures));
        gestures.MoveEnabled = false;
        gestures.SetActive(true);
        Assert.Same(originalHook, hook.GetValue(gestures));
        Assert.Null(mover.GetValue(gestures));
        gestures.SetActive(false);
        Assert.Null(hook.GetValue(gestures));
    }

    [Fact]
    public void Turning_it_off_removes_the_hook_threads_and_it_can_come_back()
    {
        using var drag = new MouseGestures(_ => false, _ => { });
        int before = HookThreads();
        Assert.False(drag.Active);
        drag.SetActive(true);
        Assert.True(drag.Active);
        Assert.True(Desktop.WaitFor(() => HookThreads() >= before + 2, 2000));
        drag.SetActive(false);
        Assert.False(drag.Active);
        Assert.True(Desktop.WaitFor(() => HookThreads() <= before + 1, 3000));
        drag.SetActive(true);
        Assert.True(drag.Active);
        drag.SetActive(false);
    }

    [Fact]
    public void A_process_Windows_wont_let_us_inspect_counts_as_elevated()
    {
        Assert.False(ProcessInfo.IsElevated((uint)Environment.ProcessId));
        using Process system = Process.GetProcessesByName("winlogon")[0];
        Assert.True(ProcessInfo.IsElevated((uint)system.Id));
        Assert.True(ProcessInfo.IsElevated(uint.MaxValue));
    }

    [Fact]
    public void A_full_screen_borderless_window_counts_and_a_framed_one_does_not()
    {
        using var game = new TargetProcess(Screen.PrimaryScreen!.Bounds);
        Assert.True(WindowInfo.FillsMonitor(game.Handle));
        using var normal = new TargetProcess(new(200, 160, 400, 300));
        Assert.False(WindowInfo.FillsMonitor(normal.Handle));
        Assert.False(WindowInfo.FillsMonitor(User32.GetShellWindow()));
    }
}

/// <summary>Uses the real pointer and modifier key, so it only runs with the interactive suite.</summary>
[Trait("Category", "Interactive")]
public sealed class ModifierGestureTests
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int x, int y, uint data, nuint extra);
    private const byte Alt = 0x12;
    private const uint LeftDown = 2, LeftUp = 4, RightDown = 8, RightUp = 16;

    private static void Pump(int ms) => Thread.Sleep(ms);

    /// <summary>The injected pointer can land a pixel out, which isn't what these tests are about.</summary>
    private static bool Near(Point actual, Point expected) => Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2;

    private static bool Near(Size actual, Size expected) => Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2;

    private static Rectangle RectOf(nint window)
    {
        User32.GetWindowRect(window, out Rect r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>A real mouse event, which the hook sees the way it sees a real mouse.</summary>
    private static void MoveTo(Point point)
    {
        const uint move = 0x1, absolute = 0x8000;
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        mouse_event(move | absolute, (point.X * 65535 + screen.Width / 2) / (screen.Width - 1),
            (point.Y * 65535 + screen.Height / 2) / (screen.Height - 1), 0, 0);
    }

    private static void Drag(Point from, Point to, uint down, uint up)
    {
        MoveTo(from);
        Pump(60);
        mouse_event(down, 0, 0, 0, 0);
        Pump(60);
        for (int i = 1; i <= 5; i++)
        {
            MoveTo(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5));
            Pump(30);
        }

        Pump(60);
        mouse_event(up, 0, 0, 0, 0);
        Pump(120);
    }

    [Fact]
    public void Alt_drag_moves_alt_right_drag_resizes_and_the_app_never_sees_the_clicks()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        Desktop.Activate(target.Handle);
        int began = 0;
        // Only ever the test's own window, so a stray press can't drag anything of the person's.
        using var drag = new MouseGestures(window => window == target.Handle, _ => began++);
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            keybd_event(Alt, 0, 0, 0);
            Drag(new Point(450, 300), new Point(550, 360), LeftDown, LeftUp);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Location, new Point(400, 260)), 1000), $"{RectOf(target.Handle)} gestures {began} slowest {drag.LongestCallback.TotalMilliseconds} ms");
            Assert.Equal(1, began);
            Drag(new Point(780, 540), new Point(830, 580), RightDown, RightUp);
            keybd_event(Alt, 0, 2, 0);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Size, new Size(450, 340)), 1000), RectOf(target.Handle).ToString());
            Assert.Null(target.NextReport(100));
            Assert.InRange(drag.LongestCallback.TotalMilliseconds, 0, 100);
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    private static void Press(uint down)
    {
        mouse_event(down, 0, 0, 0, 0);
        Pump(60);
    }

    private static void Glide(Point from, Point to, int steps, int pause)
    {
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Pump(pause);
        }
    }

    private static MouseGestures Gestures(TargetProcess target) => new(window => window == target.Handle, _ => { });

    [Fact]
    public void A_fast_drag_of_a_slow_app_catches_up_as_soon_as_its_let_go_and_then_stays()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        target.BeSlow(30);
        Desktop.Activate(target.Handle);
        using var drag = Gestures(target);
        drag.SnapEnabled = false;
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            keybd_event(Alt, 0, 0, 0);
            MoveTo(new Point(450, 300));
            Pump(60);
            Press(LeftDown);
            Glide(new Point(450, 300), new Point(850, 500), 60, 4); // About 60 moves in a quarter of a second.
            mouse_event(LeftUp, 0, 0, 0, 0);
            keybd_event(Alt, 0, 2, 0);
            var expected = new Point(700, 400);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Location, expected), 400), RectOf(target.Handle).ToString());
            Pump(500);
            Assert.True(Near(RectOf(target.Handle).Location, expected), "It moved again after settling: " + RectOf(target.Handle));
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    [Fact]
    public void Alt_drag_brings_a_maximized_window_out_and_esc_puts_it_back()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        target.UseFrame();
        Desktop.Activate(target.Handle);
        Size normal = RectOf(target.Handle).Size;
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => IsZoomed(target.Handle), 2000));
        using var drag = Gestures(target);
        drag.SnapEnabled = false;
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            keybd_event(Alt, 0, 0, 0);
            // A click with the key held leaves it maximized.
            MoveTo(new Point(600, 400));
            Pump(60);
            Press(LeftDown);
            mouse_event(LeftUp, 0, 0, 0, 0);
            Pump(200);
            Assert.True(IsZoomed(target.Handle));

            // Esc partway through a drag puts it back to maximized.
            Press(LeftDown);
            Glide(new Point(600, 400), new Point(700, 450), 10, 20);
            Assert.True(Desktop.WaitFor(() => !IsZoomed(target.Handle), 1000), "It didn't come out of maximized.");
            Desktop.Press(Keys.Escape);
            mouse_event(LeftUp, 0, 0, 0, 0);
            Assert.True(Desktop.WaitFor(() => IsZoomed(target.Handle), 1000), "Esc didn't put it back to maximized.");

            // A real drag leaves it at its own size, under the pointer.
            MoveTo(new Point(600, 400));
            Pump(60);
            Press(LeftDown);
            Glide(new Point(600, 400), new Point(700, 450), 10, 20);
            mouse_event(LeftUp, 0, 0, 0, 0);
            keybd_event(Alt, 0, 2, 0);
            Assert.True(Desktop.WaitFor(() => !IsZoomed(target.Handle) && Near(RectOf(target.Handle).Size, normal), 1000),
                RectOf(target.Handle).ToString());
            Assert.True(RectOf(target.Handle).Contains(new Point(700, 450)), RectOf(target.Handle).ToString());
            Assert.Null(target.NextReport(100));
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    /// <summary>
    /// Alt+right drag on a maximized window brings it out at the size it had on screen, then moves only the edges
    /// nearest the press, so nothing jumps.
    /// </summary>
    [Fact]
    public void Alt_right_drag_brings_a_maximized_window_out_at_its_size_and_resizes_from_the_nearest_corner()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        target.UseFrame();
        Desktop.Activate(target.Handle);
        User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
        Assert.True(Desktop.WaitFor(() => IsZoomed(target.Handle), 2000));
        Assert.True(WindowInfo.TryGetFrameBounds(target.Handle, out Rectangle maximized, out _));
        using var drag = Gestures(target);
        drag.SnapEnabled = false;
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            var from = new Point(maximized.Right - 60, maximized.Bottom - 60);
            keybd_event(Alt, 0, 0, 0);
            Drag(from, new Point(from.X - 200, from.Y - 150), RightDown, RightUp);
            keybd_event(Alt, 0, 2, 0);
            Rectangle frame = Rectangle.Empty;
            Assert.True(Desktop.WaitFor(() => !IsZoomed(target.Handle) &&
                WindowInfo.TryGetFrameBounds(target.Handle, out frame, out _) &&
                Near(frame.Location, maximized.Location) &&
                Near(frame.Size, new Size(maximized.Width - 200, maximized.Height - 150)), 1500),
                $"Maximized frame {maximized}, now {frame}, zoomed {IsZoomed(target.Handle)}.");
            Assert.Null(target.NextReport(100));
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    /// <summary>
    /// A pinned window paused while maximized is back on top at the next check after Alt+drag brings it out,
    /// while it's the window in use.
    /// </summary>
    [Fact]
    public void A_pinned_window_alt_dragged_out_of_maximized_is_back_on_top_at_the_next_check()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        target.UseFrame();
        Desktop.Activate(target.Handle);
        var windows = new WindowController([]);
        bool Topmost() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0;
        using var drag = Gestures(target);
        drag.SnapEnabled = false;
        Point original = Cursor.Position;
        try
        {
            Assert.True(windows.TogglePin(target.Handle));
            User32.ShowWindow(target.Handle, User32.SW_MAXIMIZE);
            Assert.True(Desktop.WaitFor(() => IsZoomed(target.Handle), 2000));
            windows.Refresh(new Point(-30000, -30000), false, pausePinWhenMaximized: true);
            Assert.False(Topmost(), "Still on top while maximized.");
            drag.SetActive(true);
            Pump(150);
            keybd_event(Alt, 0, 0, 0);
            Drag(new Point(600, 400), new Point(700, 450), LeftDown, LeftUp);
            keybd_event(Alt, 0, 2, 0);
            Assert.True(Desktop.WaitFor(() => !IsZoomed(target.Handle), 1000), "It didn't come out of maximized.");
            Assert.Equal(target.Handle, User32.GetForegroundWindow());
            windows.Refresh(new Point(-30000, -30000), false, pausePinWhenMaximized: true);
            Assert.True(Topmost(), "Not back on top.");
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
            windows.RestoreAll();
        }
    }

    /// <summary>
    /// A window in WinScreen's Borderless moves with Alt+drag and resizes with Alt+right drag. It has no resize frame
    /// any more, so it doesn't snap at a screen edge: it stays where it was let go.
    /// </summary>
    [Fact]
    public void A_borderless_window_moves_and_resizes_with_alt_drag_and_does_not_snap()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(500, 200, 400, 300));
        target.UseFrame();
        var windows = new WindowController([]);
        using var drag = Gestures(target);
        Point original = Cursor.Position;
        try
        {
            Assert.True(windows.ToggleBorderless(target.Handle));
            Desktop.Activate(target.Handle);
            Rectangle start = RectOf(target.Handle);
            drag.SetActive(true);
            Pump(150);
            keybd_event(Alt, 0, 0, 0);
            var grab = new Point(start.Left + 150, start.Top + 120);
            Drag(grab, new Point(grab.X + 100, grab.Y + 60), LeftDown, LeftUp);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Location, new Point(start.Left + 100, start.Top + 60)), 1000),
                "Not moved: " + RectOf(target.Handle));
            Rectangle moved = RectOf(target.Handle);
            var corner = new Point(moved.Right - 30, moved.Bottom - 30);
            Drag(corner, new Point(corner.X + 60, corner.Y + 40), RightDown, RightUp);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Size, new Size(moved.Width + 60, moved.Height + 40)), 1000),
                "Not resized: " + RectOf(target.Handle));

            // To the left edge of the screen: no snap to half.
            Screen screen = Screen.FromHandle(target.Handle);
            Rectangle before = RectOf(target.Handle);
            var from = new Point(before.Left + 150, before.Top + 120);
            MoveTo(from);
            Pump(60);
            Press(LeftDown);
            Glide(from, new Point(screen.Bounds.Left, from.Y), 15, 20);
            Pump(150);
            mouse_event(LeftUp, 0, 0, 0, 0);
            keybd_event(Alt, 0, 2, 0);
            Pump(400);
            Rectangle half = PlacementGeometry.SnapFrame(SnapZone.Left, screen.WorkingArea);
            Rectangle after = RectOf(target.Handle);
            Assert.Equal(before.Size, after.Size);
            Assert.False(Near(after.Size, half.Size), "It snapped: " + after);
            Assert.True(windows.IsBorderless(target.Handle));
            Assert.Null(target.NextReport(100));
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
            windows.RestoreAll();
        }
    }

    /// <summary>A real mouse event anywhere on the desktop, other displays included.</summary>
    private static void MoveToAnywhere(Point point)
    {
        const uint move = 0x1, absolute = 0x8000, wholeDesktop = 0x4000;
        Rectangle all = SystemInformation.VirtualScreen;
        mouse_event(move | absolute | wholeDesktop, (int)Math.Round((point.X - all.Left) * 65535.0 / (all.Width - 1)),
            (int)Math.Round((point.Y - all.Top) * 65535.0 / (all.Height - 1)), 0, 0);
    }

    /// <summary>
    /// Alt+drag to the outer edge of a second display at another scale snaps the window to half of that display's work
    /// area: the app takes the new scale on the way, and the second pass puts its frame where it belongs. Only in the
    /// test VM's two-display setup, the only place a scale is ever changed.
    /// </summary>
    [MultiDisplayFact]
    public void Alt_drag_to_the_edge_of_a_display_at_another_scale_snaps_to_half_of_it()
    {
        if (!DisplayScale.Allowed) { Skipped.Because("Changes the display scale, which only the test VM allows."); return; } // Never on someone's own screen.
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        target.UseFrame();
        Desktop.Activate(target.Handle);
        string home = Screen.FromHandle(target.Handle).DeviceName;
        string otherName = Screen.AllScreens.First(screen => screen.DeviceName != home).DeviceName;
        int scale = DisplayScale.CurrentOf(otherName);
        using var drag = Gestures(target);
        Point original = Cursor.Position;
        try
        {
            DisplayScale.Set(scale == 150 ? 100 : 150, otherName);
            Pump(1500);
            Screen homeScreen = Screen.AllScreens.First(screen => screen.DeviceName == home);
            Screen other = Screen.AllScreens.First(screen => screen.DeviceName == otherName);
            bool leftOfHome = other.Bounds.Right <= homeScreen.Bounds.Left;
            SnapZone zone = leftOfHome ? SnapZone.Left : SnapZone.Right;
            var edge = new Point(leftOfHome ? other.Bounds.Left : other.Bounds.Right - 1, other.Bounds.Top + other.Bounds.Height / 2);
            Rectangle expected = PlacementGeometry.SnapFrame(zone, other.WorkingArea);
            drag.SetActive(true);
            Pump(150);
            keybd_event(Alt, 0, 0, 0);
            var from = new Point(500, 350);
            MoveToAnywhere(from);
            Pump(60);
            Press(LeftDown);
            for (int i = 1; i <= 25; i++)
            {
                MoveToAnywhere(new Point(from.X + (edge.X - from.X) * i / 25, from.Y + (edge.Y - from.Y) * i / 25));
                Pump(25);
            }
            Pump(150);
            mouse_event(LeftUp, 0, 0, 0, 0);
            keybd_event(Alt, 0, 2, 0);
            Rectangle frame = Rectangle.Empty;
            Assert.True(Desktop.WaitFor(() => WindowInfo.TryGetFrameBounds(target.Handle, out frame, out _) &&
                Near(frame.Location, expected.Location) && Near(frame.Size, expected.Size), 2000),
                $"Expected {expected} on {otherName} at {DisplayScale.CurrentOf(otherName)}%, got {frame}.");
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
            DisplayScale.Set(scale, otherName);
        }
    }

    [Fact]
    public void Alt_drag_to_the_left_edge_snaps_to_half_and_dragging_out_brings_back_the_size()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(500, 200, 400, 300));
        target.UseFrame();
        Desktop.Activate(target.Handle);
        Size before = RectOf(target.Handle).Size;
        using var drag = Gestures(target);
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            Screen screen = Screen.FromHandle(target.Handle);
            Rectangle half = PlacementGeometry.SnapFrame(SnapZone.Left, screen.WorkingArea);
            keybd_event(Alt, 0, 0, 0);
            MoveTo(new Point(650, 300));
            Pump(60);
            Press(LeftDown);
            Glide(new Point(650, 300), new Point(screen.Bounds.Left, 400), 15, 20);
            Pump(100);
            mouse_event(LeftUp, 0, 0, 0, 0);
            Assert.True(Desktop.WaitFor(() => WindowInfo.TryGetFrameBounds(target.Handle, out Rectangle frame, out _) &&
                Near(frame.Location, half.Location) && Near(frame.Size, half.Size), 1500), RectOf(target.Handle).ToString());

            MoveTo(new Point(200, 400));
            Pump(60);
            Press(LeftDown);
            Glide(new Point(200, 400), new Point(600, 450), 15, 20);
            mouse_event(LeftUp, 0, 0, 0, 0);
            keybd_event(Alt, 0, 2, 0);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Size, before), 1000), RectOf(target.Handle).ToString());
            Assert.True(RectOf(target.Handle).Contains(new Point(600, 450)), RectOf(target.Handle).ToString());
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    [Fact]
    public void Without_the_modifier_clicks_reach_the_app_unchanged()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        Desktop.Activate(target.Handle);
        using var drag = new MouseGestures(window => window == target.Handle, _ => { });
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            Drag(new Point(450, 300), new Point(460, 310), LeftDown, LeftUp);
            Assert.Equal(new Point(300, 200), RectOf(target.Handle).Location);
            Assert.NotNull(target.NextReport(800));
        }
        finally
        {
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }

    /// <summary>
    /// Windows removes a hook it finds slow without a word. Lost in the middle of a drag, the release was never seen,
    /// and the claimed press used to keep the dead hook in place for good: no gesture worked again until a restart.
    /// </summary>
    [Fact]
    public void A_hook_lost_in_the_middle_of_a_drag_is_put_back_and_the_next_drag_works()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        Desktop.Activate(target.Handle);
        using var drag = Gestures(target);
        Point original = Cursor.Position;
        drag.SetActive(true);
        Pump(150);
        try
        {
            keybd_event(Alt, 0, 0, 0);
            MoveTo(new Point(450, 300));
            Pump(60);
            Press(LeftDown);
            Glide(new Point(450, 300), new Point(500, 330), 5, 30);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Location, new Point(350, 230)), 1000), RectOf(target.Handle).ToString());

            bool told = false;
            drag.HookSilent = () => told = true;
            drag.LoseHookForTest();
            Pump(150);
            MoveTo(new Point(520, 340)); // The pointer moves; the hook hears nothing.
            var noticed = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(Desktop.WaitFor(() => told, 1000), "The lost hook wasn't noticed during the drag.");
            Assert.True(noticed.ElapsedMilliseconds < 1000, $"Noticed after {noticed.ElapsedMilliseconds} ms.");
            Press(LeftUp); // Never seen.
            keybd_event(Alt, 0, 2, 0);
            Pump(150);
            Rectangle dropped = RectOf(target.Handle);

            // What the tray app does once input arrives with no callback. Turning it off works again too.
            drag.Reinstall();
            Assert.True(drag.Active);
            Assert.Equal(dropped, RectOf(target.Handle));

            keybd_event(Alt, 0, 0, 0);
            Drag(new Point(450, 300), new Point(530, 350), LeftDown, LeftUp);
            keybd_event(Alt, 0, 2, 0);
            Assert.True(Desktop.WaitFor(() => Near(RectOf(target.Handle).Location, new Point(dropped.X + 80, dropped.Y + 50)), 1000),
                $"{RectOf(target.Handle)} after a drop at {dropped}");
            drag.SetActive(false);
            Assert.False(drag.Active);
        }
        finally
        {
            keybd_event(Alt, 0, 2, 0);
            drag.SetActive(false);
            Cursor.Position = original;
        }
    }
}
