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

    [Fact]
    public void It_is_off_until_asked_for_and_damaged_settings_keep_it_safe()
    {
        Assert.False(new AppSettings().ModifierDrag);
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
    public void Turning_it_off_removes_the_hook_threads_and_it_can_come_back()
    {
        using var drag = new ModifierDrag(_ => false, _ => { });
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
        using var drag = new ModifierDrag(window => window == target.Handle, _ => began++);
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

    [Fact]
    public void Without_the_modifier_clicks_reach_the_app_unchanged()
    {
        Desktop.GainForegroundRights();
        using var target = new TargetProcess(new(300, 200, 400, 300));
        Desktop.Activate(target.Handle);
        using var drag = new ModifierDrag(window => window == target.Handle, _ => { });
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
}
