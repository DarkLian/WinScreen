using System.Diagnostics;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Runs the real WinScreen.exe and drives it the way a person does: with the real hotkeys and
/// mouse on a test window. It takes over the mouse and keyboard for a few seconds.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class AppEndToEndTests : IDisposable
{
    private static readonly Rectangle Spot = new(120, 120, 400, 300);
    private static readonly Color Red = Color.FromArgb(0xC8, 0x1E, 0x1E);
    private static readonly Color Blue = Color.FromArgb(0x1E, 0x64, 0xC8);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "WinScreenTest-" + Path.GetRandomFileName());
    private Process _app;
    private nint _appWindow;
    private readonly Point _cursor = Desktop.CursorPosition();

    private static string Program =>
        Environment.GetEnvironmentVariable("WINSCREEN_EXE") ?? Path.Combine(AppContext.BaseDirectory, "WinScreen.exe");

    /// <summary>Starts the program with its own data folder, so tests never touch real settings.</summary>
    private Process Start()
    {
        var start = new ProcessStartInfo(Program) { UseShellExecute = false };
        start.Environment["WINSCREEN_DATA"] = _data;
        return Process.Start(start)!;
    }

    public AppEndToEndTests()
    {
        Assert.True(Desktop.FindWindow(null, HotkeyHost.WindowTitle) == 0,
            "Close the running WinScreen before these tests.");
        var settings = new AppSettings { ExperimentalFeatures = true, PipClickForwarding = true };
        settings.Hotkeys[AppCommand.SaveWindowSetup] = "Ctrl+Alt+S"; // The Hotkey helper presses Ctrl+Alt+key.
        settings.Save(Path.Combine(_data, "settings.json"));
        _app = Start();
        nint window = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            window = Desktop.FindWindow(null, HotkeyHost.WindowTitle);
            return window != 0;
        }, 8000), "WinScreen.exe didn't start.");
        _appWindow = window;
        Thread.Sleep(300);
    }

    private void RestartWith(AppSettings settings)
    {
        Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);
        Assert.True(_app.WaitForExit(4000));
        _app.Dispose();

        settings.ExperimentalFeatures = true;
        settings.PipClickForwarding = true;
        settings.Save(Path.Combine(_data, "settings.json"));
        _app = Start();
        _appWindow = WaitForAppWindow();
        Thread.Sleep(300);
    }

    private nint WaitForAppWindow()
    {
        nint window = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            window = Desktop.FindWindow(null, HotkeyHost.WindowTitle);
            return window != 0;
        }, 8000), "WinScreen.exe didn't start.");
        return window;
    }

    public void Dispose()
    {
        Desktop.MoveMouse(_cursor);
        if (!_app.HasExited)
        {
            Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);
            if (!_app.WaitForExit(4000))
                _app.Kill();
        }

        _app.WaitForExit(4000);
        _app.Dispose();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }

    /// <summary>Presses a WinScreen hotkey, but only while the test window is the one it would act on.</summary>
    private static void Hotkey(Keys key, TargetProcess target)
    {
        Assert.Equal(target.Handle, User32.GetForegroundWindow());
        Desktop.Press(key, Keys.ControlKey, Keys.Menu);
    }

    private static void Focus(TargetProcess target)
    {
        Desktop.GainForegroundRights();
        Desktop.Activate(target.Handle);
    }

    private static Rectangle Near(Rectangle window, int partsOfFour)
    {
        return new Rectangle(window.Left + window.Width * partsOfFour / 4 - 10, window.Top + window.Height / 2 - 10, 20,
            20);
    }

    [Fact]
    public void Hotkeys_change_the_window_in_use_and_restore_puts_it_back()
    {
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Focus(target);

        Hotkey(Keys.T, target);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0),
            "Ctrl+Alt+T didn't pin.");

        Hotkey(Keys.Down, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 229), "Ctrl+Alt+Down didn't lower opacity.");

        Hotkey(Keys.Up, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 255), "Ctrl+Alt+Up didn't raise opacity.");

        Hotkey(Keys.C, target);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT) != 0),
            "Ctrl+Alt+C didn't set click-through.");

        Hotkey(Keys.R, target);
        Assert.True(Desktop.WaitFor(() => Desktop.ExStyle(target.Handle) == before),
            "Ctrl+Alt+R didn't restore the window.");
    }

    [Fact]
    public void A_changed_hotkey_in_the_settings_file_is_used()
    {
        var settings = new AppSettings();
        settings.Hotkeys[AppCommand.TogglePin] = "Ctrl+Alt+F21";
        RestartWith(settings);

        using var target = new TargetProcess(Spot);
        Focus(target);
        Hotkey(Keys.F21, target);

        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0),
            "The new hotkey didn't pin.");
    }

    [Fact]
    public void Dark_mode_works_from_the_hotkey()
    {
        using var target = new TargetProcess(Spot, "FFFFFF");
        Focus(target);
        var sample = new Rectangle(target.Center.X - 20, target.Center.Y - 20, 40, 40);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)));

        Hotkey(Keys.D, target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.Black)),
            $"Dark mode showed {Desktop.ColorOnScreen(sample)}.");

        target.SetColors("000000");
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)),
            "Dark mode didn't follow the window.");
        target.SetColors("FFFFFF");
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.Black)),
            "Dark mode didn't follow the window back.");

        // Off and on again in the same run, as a person would.
        Hotkey(Keys.D, target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)),
            "Dark mode didn't turn off.");
        Hotkey(Keys.D, target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.Black)),
            "Dark mode didn't turn on again.");
        target.SetColors("000000");
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)),
            "The second dark mode didn't follow the window.");
        target.SetColors("FFFFFF");
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.Black)));

        Hotkey(Keys.R, target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(sample), Color.White)),
            "Restore didn't end dark mode.");
    }

    [Fact]
    public void The_toolbar_appears_on_the_title_bar_and_its_buttons_work()
    {
        using var target = new TargetProcess(Spot);
        target.UseTitleBar();
        Focus(target);

        // Rest the mouse at the top middle of the window, where a title bar is.
        var rest = new Point(target.Center.X, Spot.Top + 12);
        Desktop.RequireWindowAt(rest, target.Handle);
        Desktop.MoveMouse(rest);

        nint toolbar = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            toolbar = Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen toolbar");
            return toolbar != 0;
        }, 4000), "The toolbar didn't appear.");
        Assert.Equal(target.Handle, User32.GetForegroundWindow());

        // First button: pin. The window it acts on must stay active.
        Rectangle bar = Desktop.BoundsOf(toolbar);
        Desktop.ClickOn(new Point(bar.Left + bar.Height / 2, bar.Top + bar.Height / 2), toolbar);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0),
            "The pin button didn't pin.");
        Assert.Equal(target.Handle, User32.GetForegroundWindow());

        // Moving away hides it.
        Desktop.MoveMouse(new Point(Spot.Left + 40, Spot.Bottom - 40));
        Assert.True(Desktop.WaitFor(() => !Desktop.IsWindowVisible(toolbar)), "The toolbar didn't hide.");
    }

    [Fact]
    public void An_anchor_brings_forward_minimizes_and_closes_its_window()
    {
        using var target = new TargetProcess(Spot, "C81E1E", "Anchored window");
        using var other = new TargetProcess(new Rectangle(560, 120, 300, 300), "1E64C8", "Other window");
        Focus(target);
        Hotkey(Keys.A, target);

        nint anchor = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            anchor = Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen anchor - Anchored window");
            return anchor != 0;
        }), "Ctrl+Alt+A didn't add an anchor.");
        Rectangle button = Desktop.BoundsOf(anchor);
        var middle = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);

        // Left-click from another window brings it forward.
        Desktop.Activate(other.Handle);
        Desktop.ClickOn(middle, anchor);
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle),
            "Left-click didn't bring the window forward.");

        // Right-click opens the standard menu; choose its second (Minimize) action with the keyboard.
        Desktop.RightClickOn(middle, anchor);
        Thread.Sleep(150);
        Desktop.Press(Keys.Down);
        Desktop.Press(Keys.Down);
        Desktop.Press(Keys.Enter);
        Assert.True(Desktop.WaitFor(() => User32.IsIconic(target.Handle)), "The Anchor menu didn't minimize.");
        Desktop.ClickOn(middle, anchor);
        Assert.True(
            Desktop.WaitFor(() => !User32.IsIconic(target.Handle) && User32.GetForegroundWindow() == target.Handle),
            "Left-click didn't restore.");

        // Dragging moves the anchor and acts on nothing.
        var moved = new Point(middle.X - 120, middle.Y + 80);
        Desktop.Drag(middle, moved, anchor);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(anchor).Left == button.Left - 120),
            "Dragging didn't move the anchor.");

        // Middle-click closes the window and the anchor goes with it.
        Desktop.MiddleClickOn(moved, anchor);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(target.Handle)), "Middle-click didn't close the window.");
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(anchor)), "The anchor outlived its window.");
    }

    [Fact]
    public void Glass_works_from_the_hotkey()
    {
        using var back = new TargetProcess(Spot, "1E64C8");
        using var target = new TargetProcess(Spot, "202020,202020,FFFFFF");
        Desktop.GainForegroundRights();
        Desktop.Activate(back.Handle);
        Desktop.Activate(target.Handle);
        var background = new Rectangle(Spot.Left + 40, Spot.Top + 80, 30, 30);

        Hotkey(Keys.G, target);

        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(background), Blue)),
            $"Glass showed {Desktop.ColorOnScreen(background)}.");
    }

    /// <summary>A strip along a window's left edge, a little way down so a title or corner doesn't matter.</summary>
    private static Rectangle LeftEdge(Rectangle window) => new(window.Left, window.Top + 40, 3, 40);

    [Fact]
    public void A_pinned_window_gets_a_red_border_that_clicks_pass_through()
    {
        RestartWith(new AppSettings { HighlightPinned = true });

        using var target = new TargetProcess(Spot, "1E64C8");
        Focus(target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(LeftEdge(Spot)), Blue)));

        Hotkey(Keys.T, target);
        Assert.True(Desktop.WaitFor(() => Desktop.ColorOnScreen(LeftEdge(Spot)).R > 180),
            $"The edge showed {Desktop.ColorOnScreen(LeftEdge(Spot))}.");
        Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(Near(Spot, 2)), Blue), "The border covered the window.");

        var edge = new Point(Spot.Left + 1, Spot.Top + 60);
        Assert.Equal(target.Handle, User32.GetAncestor(Desktop.TopWindowAt(edge), User32.GA_ROOT));
        Assert.Equal(target.Handle, User32.GetForegroundWindow());

        Hotkey(Keys.T, target);
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(LeftEdge(Spot)), Blue)),
            "The border stayed after unpinning.");
    }

    [Fact]
    public void Without_the_setting_a_pinned_window_has_no_border()
    {
        using var target = new TargetProcess(Spot, "1E64C8");
        Focus(target);

        Hotkey(Keys.T, target);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0));
        Thread.Sleep(500);

        Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(LeftEdge(Spot)), Blue));
    }

    [Fact]
    public void The_default_level_comes_from_the_settings()
    {
        RestartWith(new AppSettings { DefaultOpacityPercent = 50 });

        using var target = new TargetProcess(Spot);
        Focus(target);

        Hotkey(Keys.O, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 128),
            $"Ctrl+Alt+O gave {Desktop.Alpha(target.Handle)}.");

        Hotkey(Keys.O, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 255), "Ctrl+Alt+O didn't turn it off.");
    }

    [Fact]
    public void A_program_left_off_the_include_list_is_ignored()
    {
        RestartWith(new AppSettings { IncludedPrograms = ["ExampleProgram"] });

        using var target = new TargetProcess(Spot);
        Focus(target);

        Hotkey(Keys.T, target);
        Thread.Sleep(500);

        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void A_remembered_setup_comes_back_for_the_programs_next_window_and_after_a_restart()
    {
        using (var first = new TargetProcess(Spot))
        {
            Focus(first);
            Hotkey(Keys.T, first);
            Hotkey(Keys.Down, first);
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(first.Handle) == 229));

            Hotkey(Keys.S, first);
            string path = Path.Combine(_data, "settings.json");
            Assert.True(Desktop.WaitFor(() => AppSettings.Load(path).SetupFor("WinScreen.TestTarget") is not null),
                "Ctrl+Alt+S saved nothing.");
        }

        using (var second = new TargetProcess(new Rectangle(560, 120, 300, 300)))
        {
            Focus(second);
            Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(second.Handle) & User32.WS_EX_TOPMOST) != 0),
                "The next window wasn't pinned.");
            Assert.True(Desktop.WaitFor(() => Desktop.Alpha(second.Handle) == 229),
                "The next window wasn't see-through.");

            // What the person undoes stays undone.
            Hotkey(Keys.R, second);
            Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(second.Handle) & User32.WS_EX_TOPMOST) == 0));
            Thread.Sleep(600);
            Assert.Equal(0, Desktop.ExStyle(second.Handle) & User32.WS_EX_TOPMOST);
        }

        RestartWith(AppSettings.Load(Path.Combine(_data, "settings.json")));
        using var third = new TargetProcess(Spot);
        Focus(third);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(third.Handle) == 229), "The setup didn't survive a restart.");

        // Pressing it on a window with nothing changed forgets the program.
        Hotkey(Keys.R, third);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(third.Handle) & User32.WS_EX_TOPMOST) == 0));
        Hotkey(Keys.S, third);
        Assert.True(
            Desktop.WaitFor(() =>
                AppSettings.Load(Path.Combine(_data, "settings.json")).SetupFor("WinScreen.TestTarget") is null),
            "The setup wasn't forgotten.");
    }

    [Fact]
    public void Shrinking_leaves_a_live_tile_and_clicking_it_brings_the_window_back()
    {
        using var target = new TargetProcess(Spot, "C81E1E", "Window to shrink");
        Focus(target);

        Hotkey(Keys.W, target);
        nint tile = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            tile = Desktop.FindProcessWindow(_app.Id, title => title == "Shrunk - Window to shrink");
            return tile != 0;
        }), "Ctrl+Alt+W didn't leave a tile.");
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 1), "The window wasn't hidden.");

        Rectangle small = Desktop.BoundsOf(tile);
        Assert.True(small.Width <= Spot.Width / 2, $"The tile is {small}.");
        Assert.Equal(Spot.Location, small.Location);
        Assert.Equal(Spot, Desktop.BoundsOf(target.Handle));
        var middle = new Point(small.Left + small.Width / 2, small.Top + small.Height / 2);
        Assert.True(
            Desktop.WaitFor(() =>
                Desktop.IsNear(Desktop.ColorOnScreen(new Rectangle(middle.X - 10, middle.Y - 10, 20, 20)), Red)),
            "The tile doesn't show the window.");

        // Dragging moves the tile and leaves the window hidden.
        var moved = new Point(middle.X + 300, middle.Y + 60);
        Desktop.Drag(middle, moved, tile);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(tile).Left == small.Left + 300), "The tile didn't move.");
        Assert.Equal((byte?)1, Desktop.Alpha(target.Handle));

        Desktop.ClickOn(moved, tile);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 255), "The window didn't come back.");
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(tile)), "The tile stayed.");
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle),
            "The window didn't come forward.");
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TRANSPARENT);
    }

    [Fact]
    public void A_shrunk_window_comes_back_when_restored_or_when_the_app_closes()
    {
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Focus(target);
        Hotkey(Keys.W, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 1));

        Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);

        Assert.True(_app.WaitForExit(4000), "WinScreen didn't exit when asked to close.");
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
    }

    [Fact]
    public void Dragging_on_a_window_crops_it_and_the_hotkey_uncrops_it()
    {
        using var target = new TargetProcess(Spot, "1E64C8");
        Focus(target);

        Hotkey(Keys.X, target);
        nint overlay = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            overlay = Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen crop");
            return overlay != 0;
        }), "Ctrl+Alt+X didn't offer a crop.");
        Assert.Equal(Spot, Desktop.BoundsOf(overlay));

        var from = new Point(Spot.Left + 20, Spot.Top + 30);
        var to = new Point(Spot.Left + 220, Spot.Top + 180);
        Desktop.Drag(from, to, overlay);

        Assert.True(Desktop.WaitFor(() => Windows.WindowInfo.ShapeOf(target.Handle) == new Rectangle(20, 30, 200, 150)),
            $"The crop came out as {Windows.WindowInfo.ShapeOf(target.Handle)}.");
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(overlay)), "The crop overlay stayed.");
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle),
            "The window didn't get the keyboard back.");

        Hotkey(Keys.X, target);
        Assert.True(Desktop.WaitFor(() => Windows.WindowInfo.ShapeOf(target.Handle) is null),
            "The hotkey didn't uncrop.");
    }

    [Fact]
    public void Escape_cancels_a_crop()
    {
        using var target = new TargetProcess(Spot);
        Focus(target);

        Hotkey(Keys.X, target);
        nint overlay = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            overlay = Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen crop");
            return overlay != 0 && User32.GetForegroundWindow() == overlay;
        }), "Ctrl+Alt+X didn't offer a crop.");

        Desktop.Press(Keys.Escape);

        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(overlay)), "Escape didn't close the overlay.");
        Assert.Null(Windows.WindowInfo.ShapeOf(target.Handle));
    }

    [Fact]
    public void A_second_copy_does_not_start()
    {
        using Process second = Start();

        nint message = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            message = Desktop.FindProcessWindow(second.Id, title => title == "WinScreen");
            return message != 0;
        }, 8000), "A second copy didn't say WinScreen is already running.");

        Desktop.PostMessage(message, User32.WM_CLOSE, 0, 0);
        Assert.True(second.WaitForExit(4000), "The second copy didn't exit.");
        Assert.False(_app.HasExited);
    }

    [Fact]
    public void Starting_after_a_forced_kill_puts_windows_back()
    {
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Focus(target);
        Hotkey(Keys.T, target);
        Hotkey(Keys.Down, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 229));

        _app.Kill();
        _app.WaitForExit(4000);
        Assert.Equal((byte?)229, Desktop.Alpha(target.Handle));
        _app.Dispose();

        _app = Start();
        _appWindow = WaitForAppWindow();

        Assert.True(Desktop.WaitFor(() => Desktop.ExStyle(target.Handle) == before),
            "The window wasn't put back at the next start.");
    }

    [Fact]
    public void Clicking_and_typing_in_the_view_reach_the_real_window_as_real_input()
    {
        using var target = new TargetProcess(Spot, "C81E1E,1E64C8", "WinScreen PiP source");
        Focus(target);
        Hotkey(Keys.P, target);
        nint pip = WaitForPip();

        Rectangle start = Desktop.BoundsOf(pip);
        Desktop.Drag(new Point(start.Left + 60, start.Top + 60), new Point(700, 160), pip);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(pip).Left == 640),
            $"The drag left the view at {Desktop.BoundsOf(pip)}.");
        Rectangle view = Desktop.BoundsOf(pip);

        // A click at three quarters across and half way down lands on that spot of the real window.
        Desktop.ClickOn(new Point(view.Left + view.Width * 3 / 4, view.Top + view.Height / 2), pip);
        string? click = target.NextReport();
        Assert.NotNull(click);
        string[] parts = click.Split(' ');
        Assert.Equal("click", parts[0]);
        Assert.InRange(int.Parse(parts[1]), 290, 310);
        Assert.InRange(int.Parse(parts[2]), 140, 160);
        Assert.Equal("real=True", parts[3]);

        // The real window now has the keyboard too.
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle),
            "The real window didn't get the keyboard.");
        Desktop.Press(Keys.Q);
        Assert.Equal("key q real=True", target.NextReport());

        // Leaving sends the window home and hands back the view.
        Desktop.MoveMouse(new Point(40, 40));
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(target.Handle) == Spot), "The real window didn't go back.");
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void A_click_in_the_view_lands_on_the_right_spot_of_a_maximized_window()
    {
        // The borderless test window fills the whole screen when maximized, which counts as full-screen.
        RestartWith(new AppSettings { PauseInFullScreen = false });

        using var target = new TargetProcess(Spot, "C81E1E,1E64C8", "WinScreen PiP source");
        Focus(target);
        const int maximize = 3;
        User32.ShowWindow(target.Handle, maximize);
        Assert.True(Desktop.WaitFor(() => User32.IsZoomed(target.Handle)));
        Thread.Sleep(300);
        Rectangle full = Desktop.BoundsOf(target.Handle);

        Hotkey(Keys.P, target);
        nint pip = WaitForPip();

        // Keep the view away from the spot being clicked.
        Rectangle start = Desktop.BoundsOf(pip);
        Desktop.Drag(new Point(start.Left + 60, start.Top + 60), new Point(100, 100), pip);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(pip).Left == 40), "The view didn't move.");
        Rectangle view = Desktop.BoundsOf(pip);

        Desktop.ClickOn(new Point(view.Left + view.Width * 3 / 4, view.Top + view.Height / 2), pip);

        string? click = target.NextReport();
        Assert.NotNull(click);
        string[] parts = click.Split(' ');
        Assert.Equal("click", parts[0]);
        Assert.InRange(int.Parse(parts[1]), full.Width * 3 / 4 - 12, full.Width * 3 / 4 + 12);
        Assert.InRange(int.Parse(parts[2]), full.Height / 2 - 12, full.Height / 2 + 12);
        Assert.Equal("real=True", parts[3]);
        Assert.True(User32.IsZoomed(target.Handle), "The window stopped being maximized.");
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle));

        // Still visible on top, so it can be used again.
        Assert.NotEqual(0, Desktop.ExStyle(pip) & User32.WS_EX_TOPMOST);
    }

    [Fact]
    public void The_view_menu_hides_the_original()
    {
        using var target = new TargetProcess(Spot, "C81E1E", "WinScreen PiP source");
        Focus(target);
        Hotkey(Keys.P, target);
        nint pip = WaitForPip();

        Rectangle view = Desktop.BoundsOf(pip);
        Desktop.RightClickOn(new Point(view.Left + 60, view.Top + 60), pip);
        Assert.True(Desktop.WaitFor(() => ForegroundIsApp()), "The view's menu didn't open.");
        Desktop.Press(Keys.H);

        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 1), "The original wasn't hidden.");
    }

    private bool ForegroundIsApp()
    {
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint owner);
        return owner == _app.Id;
    }

    private nint WaitForPip()
    {
        nint pip = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            pip = Desktop.FindProcessWindow(_app.Id, title => title.StartsWith("PiP - ", StringComparison.Ordinal));
            return pip != 0;
        }), "Ctrl+Alt+P didn't open a PiP window.");
        Thread.Sleep(300);
        return pip;
    }

    [Fact]
    public void Closing_the_app_restores_windows()
    {
        using var target = new TargetProcess(Spot);
        long before = Desktop.ExStyle(target.Handle);
        Focus(target);
        Hotkey(Keys.T, target);
        Hotkey(Keys.Down, target);
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 229));

        Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);

        Assert.True(_app.WaitForExit(4000), "WinScreen didn't exit when asked to close.");
        Assert.Equal(before, Desktop.ExStyle(target.Handle));
    }

    [Fact]
    public void Picture_in_picture_opens_from_the_hotkey_and_answers_the_mouse()
    {
        using var target = new TargetProcess(Spot, "C81E1E,1E64C8", "WinScreen PiP source");
        Focus(target);
        Hotkey(Keys.P, target);

        nint pip = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            pip = Desktop.FindProcessWindow(_app.Id, title => title.StartsWith("PiP - ", StringComparison.Ordinal));
            return pip != 0;
        }), "Ctrl+Alt+P didn't open a PiP window.");
        Thread.Sleep(300);

        // Drag: it moves, and gets clear of any notification in the corner.
        Rectangle start = Desktop.BoundsOf(pip);
        var grab = new Point(start.Left + 60, start.Top + 60);
        var goal = new Point(660, 160);
        Desktop.Drag(grab, goal, pip);
        Rectangle moved = Rectangle.Empty;
        Assert.True(Desktop.WaitFor(() =>
            {
                moved = Desktop.BoundsOf(pip);
                return Math.Abs(moved.Left - (goal.X - 60)) <= 2 && Math.Abs(moved.Top - (goal.Y - 60)) <= 2;
            }), $"Dragging moved it to {moved}.");

        // Wheel: one notch makes it 40 pixels wider.
        int width = moved.Width;
        Desktop.Wheel(new Point(moved.Left + moved.Width / 2, moved.Top + moved.Height / 2), 1, pip);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(pip).Width == width + 40), "The wheel didn't resize it.");
        moved = Desktop.BoundsOf(pip);

        // Both halves of the source are visible.
        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Near(moved, 1)), Red)),
            $"Left half showed {Desktop.ColorOnScreen(Near(moved, 1))}.");
        Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(Near(moved, 3)), Blue),
            $"Right half showed {Desktop.ColorOnScreen(Near(moved, 3))}.");

        // Ctrl+drag over the right half: the view dims except the selection, then the crop leaves only blue.
        var from = new Point(moved.Left + moved.Width * 6 / 10, moved.Top + moved.Height / 4);
        var to = new Point(moved.Left + moved.Width * 9 / 10, moved.Top + moved.Height * 3 / 4);
        Desktop.Key(Keys.ControlKey, true);
        try
        {
            Desktop.PressAndDrag(from, to, pip);
            var outside = new Rectangle(moved.Left + moved.Width * 3 / 4 - 10, moved.Top + moved.Height / 8 - 5, 20,
                10);
            var inside = new Rectangle((from.X + to.X) / 2 - 10, (from.Y + to.Y) / 2 - 10, 20, 20);
            Assert.True(Desktop.WaitFor(() => !Desktop.IsNear(Desktop.ColorOnScreen(outside), Blue, 40)),
                $"The view didn't dim while selecting: {Desktop.ColorOnScreen(outside)}.");
            Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(inside), Blue),
                $"The selection didn't stay bright: {Desktop.ColorOnScreen(inside)}.");
        }
        finally
        {
            Desktop.MouseButton(false);
            Desktop.Key(Keys.ControlKey, false);
        }

        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Near(Desktop.BoundsOf(pip), 1)), Blue)),
            "Ctrl+drag didn't crop.");

        // Ctrl+double-click brings the whole source back.
        Rectangle cropped = Desktop.BoundsOf(pip);
        Desktop.Key(Keys.ControlKey, true);
        try
        {
            Desktop.DoubleClick(new Point(cropped.Left + cropped.Width / 2, cropped.Top + cropped.Height / 2), pip);
        }
        finally
        {
            Desktop.Key(Keys.ControlKey, false);
        }

        Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Near(Desktop.BoundsOf(pip), 1)), Red)),
            "Ctrl+double-click didn't remove the crop.");
    }
}
