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
        var settings = new AppSettings { ExperimentalFeatures = true };
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
        Assert.True(_app.WaitForInputIdle(10000), "WinScreen did not finish initializing its UI.");
        Thread.Sleep(2000); // Input-idle can occur while the notification-area startup still pumps messages.
    }

    private void RestartWith(AppSettings settings)
    {
        Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);
        Assert.True(_app.WaitForExit(4000));
        _app.Dispose();

        settings.ExperimentalFeatures = true;
        settings.Save(Path.Combine(_data, "settings.json"));
        _app = Start();
        _appWindow = WaitForAppWindow();
        Assert.True(_app.WaitForInputIdle(10000), "WinScreen did not finish initializing its UI.");
        Thread.Sleep(2000); // Input-idle can occur while the notification-area startup still pumps messages.
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
    public void A_region_can_be_opened_using_only_the_keyboard()
    {
        var settings = new AppSettings { ShowToolbar = false, WelcomeShown = true };
        settings.Hotkeys[AppCommand.PickPipSource] = "Ctrl+Alt+F21";
        RestartWith(settings);
        using var target = new TargetProcess(Spot);
        Focus(target);
        Hotkey(Keys.F21, target);
        nint picker = 0;
        Assert.True(Desktop.WaitFor(() => (picker = Desktop.FindProcessWindow(_app.Id,
            title => title == Localization.UiText.T("pip.chooseSource"))) != 0));
        Desktop.Activate(picker);
        Desktop.Press(Keys.Tab, Keys.ShiftKey); // Search -> source type.
        Desktop.Press(Keys.Home);
        Desktop.Press(Keys.Down);
        Desktop.Press(Keys.Down); // Region.
        Desktop.Press(Keys.Tab); // Search.
        Desktop.Press(Keys.Tab); // Display list.
        Desktop.Press(Keys.Home);
        Desktop.Press(Keys.Enter);
        nint bounds = 0;
        Assert.True(Desktop.WaitFor(() => (bounds = Desktop.FindProcessWindow(_app.Id,
            title => title == Localization.UiText.T("pip.sources.regionBounds"))) != 0));
        Desktop.Press(Keys.Tab);
        Desktop.Press(Keys.Tab); // Width.
        Desktop.Press(Keys.A, Keys.ControlKey);
        Desktop.Press(Keys.D3); Desktop.Press(Keys.D2); Desktop.Press(Keys.D0);
        Desktop.Press(Keys.Tab); // Height.
        Desktop.Press(Keys.A, Keys.ControlKey);
        Desktop.Press(Keys.D2); Desktop.Press(Keys.D4); Desktop.Press(Keys.D0);
        Desktop.Press(Keys.Enter);
        nint view = WaitForPip();
        Assert.Contains("region", WindowTitle(view), StringComparison.OrdinalIgnoreCase);
        Assert.InRange(Desktop.BoundsOf(view).Width / (double)Desktop.BoundsOf(view).Height, 1.30, 1.36);
        Focus(target);
        Hotkey(Keys.R, target);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(view)));
    }

    private static string WindowTitle(nint window) => WinScreen.Windows.WindowInfo.Title(window);

    [Fact]
    public void A_saved_region_reopens_from_the_picker_with_its_layout()
    {
        Screen display = Screen.PrimaryScreen!;
        var settings = new AppSettings { WelcomeShown = true, ShowToolbar = false, PipBookmarks = [new()
        {
            Name = "Saved reference", Device = display.DeviceName, DisplayX = display.Bounds.X, DisplayY = display.Bounds.Y,
            DisplayWidth = display.Bounds.Width, DisplayHeight = display.Bounds.Height,
            Region = new PipCropShares(0, 0, 320.0 / display.Bounds.Width, 240.0 / display.Bounds.Height),
            View = new PipPreset { Name = "Layout", LongSide = 320, PositionLocked = false },
        }] };
        settings.Hotkeys[AppCommand.PickPipSource] = "Ctrl+Alt+F21";
        RestartWith(settings);
        using var target = new TargetProcess(Spot);
        Focus(target);
        Hotkey(Keys.F21, target);
        nint picker = 0;
        Assert.True(Desktop.WaitFor(() => (picker = Desktop.FindProcessWindow(_app.Id,
            title => title == Localization.UiText.T("pip.chooseSource"))) != 0));
        Desktop.Activate(picker);
        Desktop.Press(Keys.Tab, Keys.ShiftKey);
        Desktop.Press(Keys.Home);
        Desktop.Press(Keys.Down); Desktop.Press(Keys.Down); Desktop.Press(Keys.Down);
        Desktop.Press(Keys.Tab); Desktop.Press(Keys.Tab);
        Desktop.Press(Keys.Home); Desktop.Press(Keys.Enter);
        nint view = WaitForPip();
        Assert.Equal(320, Desktop.BoundsOf(view).Width);
        Assert.Contains("region", WindowTitle(view), StringComparison.OrdinalIgnoreCase);
        Focus(target);
        Hotkey(Keys.R, target);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(view)));
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
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 0), "The window wasn't hidden.");

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
        Assert.Equal((byte?)0, Desktop.Alpha(target.Handle));

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
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 0));

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

    [Theory]
    [InlineData(AppCommand.ToggleBorderless)]
    [InlineData(AppCommand.ToggleDarkTitleBar)]
    public void Starting_after_a_forced_kill_recovers_window_appearance(AppCommand command)
    {
        var settings = new AppSettings();
        settings.Hotkeys[command] = "Ctrl+Alt+F21";
        RestartWith(settings);
        using var target = new TargetProcess(Spot);
        target.UseFrame();
        long style = User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE);
        bool dark = new WinScreen.Windows.WindowController([]).IsDarkTitleBar(target.Handle);
        Focus(target);
        Hotkey(Keys.F21, target);
        Assert.True(Desktop.WaitFor(() => command == AppCommand.ToggleBorderless
            ? (User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE) & User32.WS_CAPTION) == 0
            : new WinScreen.Windows.WindowController([]).IsDarkTitleBar(target.Handle) != dark));
        _app.Kill();
        Assert.True(_app.WaitForExit(4000));
        _app.Dispose();
        _app = Start();
        _appWindow = WaitForAppWindow();
        Assert.True(Desktop.WaitFor(() =>
            (long)User32.GetWindowLongPtr(target.Handle, User32.GWL_STYLE) == style &&
            new WinScreen.Windows.WindowController([]).IsDarkTitleBar(target.Handle) == dark));
    }

    [Fact]
    public void Window_menu_hotkey_works_without_enabling_mouse_gestures()
    {
        var settings = new AppSettings();
        settings.Hotkeys[AppCommand.ShowWindowMenu] = "Ctrl+Alt+F21";
        RestartWith(settings);
        using var target = new TargetProcess(Spot);
        Focus(target);
        Desktop.MoveMouse(target.Center);
        Hotkey(Keys.F21, target);
        Assert.True(Desktop.WaitFor(() => Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen.WindowMenu") != 0));
        Desktop.Press(Keys.Escape);
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle));
    }

    [Theory]
    [InlineData(WindowMenuGesture.CtrlRightClick)]
    [InlineData(WindowMenuGesture.CtrlShiftRightClick)]
    [InlineData(WindowMenuGesture.CtrlMiddleClick)]
    public void Window_menu_gesture_earns_focus_from_input_in_another_process(WindowMenuGesture gesture)
    {
        RestartWith(new AppSettings { WindowMenu = true, WindowMenuGesture = gesture });
        using var target = new TargetProcess(Spot);
        Focus(target);
        Thread.Sleep(350); // Foreground policy runs every 250 ms.
        try
        {
            Desktop.Key(Keys.ControlKey, true);
            if (gesture == WindowMenuGesture.CtrlShiftRightClick) Desktop.Key(Keys.ShiftKey, true);
            if (gesture == WindowMenuGesture.CtrlMiddleClick) Desktop.MiddleClickOn(target.Center, target.Handle);
            else Desktop.RightClickOn(target.Center, target.Handle);
        }
        finally { Desktop.Key(Keys.ControlKey, false); Desktop.Key(Keys.ShiftKey, false); }
        nint host = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            host = Desktop.FindProcessWindow(_app.Id, title => title == "WinScreen.WindowMenu");
            return host != 0 && User32.GetForegroundWindow() == host;
        }, 1000), "The pointer menu did not receive focus.");
        Assert.Null(target.NextReport(100));
        Desktop.Press(Keys.Down);
        Desktop.Press(Keys.Escape);
        Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == target.Handle));
    }

    [Fact]
    public void Starting_after_a_forced_kill_restores_a_source_taken_off_the_taskbar()
    {
        if (Environment.OSVersion.Version.Build < 22000)
        {
            // Windows 10 refuses switcher hiding for smooth capture, so this uses standard views there.
            AppSettings standard = AppSettings.Load(Path.Combine(_data, "settings.json"));
            standard.Pip.Backend = PipBackendPreference.Dwm;
            RestartWith(standard);
        }
        using var target = new TargetProcess(Spot, "C81E1E", "WinScreen switcher recovery source");
        long original = Desktop.ExStyle(target.Handle);
        Rectangle originalBounds = Desktop.BoundsOf(target.Handle);
        Focus(target);
        Hotkey(Keys.P, target);
        nint pip = WaitForPip();
        Rectangle view = Desktop.BoundsOf(pip);
        Point point = new(view.Left + 60, view.Top + 60);
        Desktop.RightClickOn(point, pip);
        Assert.True(Desktop.WaitFor(ForegroundIsApp));
        Desktop.Press(Keys.K); // &Keep updating
        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 0));
        // Hidden, it leaves the taskbar but keeps its own styles, so its caption buttons stay its own.
        Assert.True(Desktop.WaitFor(() => File.Exists(Path.Combine(_data, "changed-windows.json")) &&
            File.ReadAllText(Path.Combine(_data, "changed-windows.json")).Contains("\"TaskbarButtonRemoved\":true")));
        Assert.Equal(0, Desktop.ExStyle(target.Handle) & WinScreen.Windows.WindowController.SwitcherMask & ~original);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(target.Handle) != originalBounds));
        _app.Kill();
        Assert.True(_app.WaitForExit(4000));
        _app.Dispose();
        _app = Start();
        _appWindow = WaitForAppWindow();
        Assert.True(Desktop.WaitFor(() => Desktop.ExStyle(target.Handle) == original));
        Assert.True(Desktop.Alpha(target.Handle) is null or > 1, "The window is still hidden.");
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(target.Handle) == originalBounds));
    }

    [Fact]
    public void A_click_on_a_view_leaves_the_original_where_it_is()
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

        // Position this fixture directly; the dedicated title-bar test covers dragging.
        Assert.True(User32.SetWindowPos(pip, 0, 40, 40, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOZORDER));
        Rectangle view = Desktop.BoundsOf(pip);
        using var other = new TargetProcess(new Rectangle(view.Right + 80, view.Bottom + 80, 240, 160), "1E64C8", "Another window");
        Focus(other);

        var pressed = new Point(view.Left + view.Width * 3 / 4, view.Top + view.Height / 2);
        Desktop.ClickOn(pressed, pip);

        // Going to the original is in the view's menu; a click on the picture does nothing to it.
        Assert.Null(target.NextReport(400));
        Assert.Equal(pressed, Desktop.CursorPosition());
        Assert.Equal(full, Desktop.BoundsOf(target.Handle));
        Assert.True(User32.IsZoomed(target.Handle), "The window stopped being maximized.");
        Thread.Sleep(300);
        Assert.Equal(other.Handle, User32.GetForegroundWindow()); // Nothing brought the original forward.

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
        Desktop.Press(Keys.K);

        Assert.True(Desktop.WaitFor(() => Desktop.Alpha(target.Handle) == 0), "The original wasn't hidden.");
    }

    private bool ForegroundIsApp()
    {
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint owner);
        return owner == _app.Id;
    }

    [Fact]
    public void A_saved_view_returns_after_a_restart_for_the_first_window_only()
    {
        using var first = new TargetProcess(Spot);
        Focus(first);
        Hotkey(Keys.P, first);
        nint pip = WaitForPip();
        Hotkey(Keys.S, first);
        string settingsFile = Path.Combine(_data, "settings.json");
        ProgramSetup setup = null!;
        Assert.True(Desktop.WaitFor(() =>
        {
            setup = AppSettings.Load(settingsFile).SavedSetups.FirstOrDefault()!;
            return setup?.View is not null;
        }), "The view wasn't saved with the setup.");
        Rectangle place = Desktop.BoundsOf(pip);

        AppSettings saved = AppSettings.Load(settingsFile);
        saved.SavedSetups[0].ViewKeepsUpdating = true;
        RestartWith(saved);
        Focus(first);
        pip = WaitForPip();
        Assert.Equal(place, Desktop.BoundsOf(pip));
        Assert.Equal(first.Handle, User32.GetForegroundWindow());
        Thread.Sleep(600); // Past the app's foreground watch: the source mustn't hide while it's in use.
        Assert.False(Windows.WindowInfo.IsConcealed(first.Handle));

        using var second = new TargetProcess(new Rectangle(640, 200, 300, 240));
        Focus(second);
        Assert.True(Desktop.WaitFor(() => Windows.WindowInfo.IsConcealed(first.Handle)), "The source didn't hide after losing the foreground.");
        Thread.Sleep(600);
        Assert.Equal(1, Desktop.CountProcessWindows(_app.Id, title => title.StartsWith("PiP - ", StringComparison.Ordinal)));
    }

    private static int Copies() => Process.GetProcessesByName("WinScreen").Length;

    private void ShrinkAndAwaitWatcher(TargetProcess target)
    {
        Focus(target);
        Hotkey(Keys.W, target);
        Assert.True(Desktop.WaitFor(() => Windows.WindowInfo.IsConcealed(target.Handle)), "Shrink didn't hide the window.");
        Assert.True(Desktop.WaitFor(() => Copies() == 2), "The watcher didn't start.");
    }

    [Fact]
    public void A_forced_kill_reveals_a_hidden_window_within_two_seconds_and_the_watcher_leaves()
    {
        using var target = new TargetProcess(Spot);
        ShrinkAndAwaitWatcher(target);
        _app.Kill();
        Assert.True(Desktop.WaitFor(() => !Windows.WindowInfo.IsConcealed(target.Handle), 2000), "The window stayed hidden.");
        Assert.True(Desktop.WaitFor(() => Copies() == 0, 2000), "The watcher stayed.");
        Assert.False(File.Exists(Path.Combine(_data, "changed-windows.json")));
        _app.WaitForExit();
        _app.Dispose();
        _app = Start();
        _appWindow = WaitForAppWindow();
    }

    /// <summary>
    /// The watcher can find WinScreen and then fail to open it because it died in between, right after starting the
    /// watcher. Before the fix that crashed the watcher and left the window hidden (3 in 24 such kills in the VM).
    /// </summary>
    [Fact]
    public void A_forced_kill_right_after_the_watcher_starts_still_reveals_the_window()
    {
        var log = new System.Text.StringBuilder();
        for (int round = 0; round < 10; round++)
        {
            using var target = new TargetProcess(Spot);
            ShrinkAndAwaitWatcher(target);
            _app.Kill(); // At once: the watcher is tens of milliseconds old.
            bool revealed = Desktop.WaitFor(() => !Windows.WindowInfo.IsConcealed(target.Handle), 2000);
            log.AppendLine($"round {round}: revealed={revealed} journal={File.Exists(Path.Combine(_data, "changed-windows.json"))}");
            Assert.True(revealed, "The window stayed hidden.\n" + log);
            Assert.True(Desktop.WaitFor(() => Copies() == 0, 3000), "The watcher stayed.\n" + log);
            _app.WaitForExit();
            _app.Dispose();
            _app = Start();
            _appWindow = WaitForAppWindow();
            Thread.Sleep(1000);
        }
    }

    [Fact]
    public void The_watcher_goes_when_nothing_is_hidden_and_does_nothing_after_a_clean_exit()
    {
        using var target = new TargetProcess(Spot);
        ShrinkAndAwaitWatcher(target);
        Desktop.PostMessage(_appWindow, User32.WM_CLOSE, 0, 0);
        Assert.True(_app.WaitForExit(4000));
        Assert.False(Windows.WindowInfo.IsConcealed(target.Handle));
        Assert.True(Desktop.WaitFor(() => Copies() == 0, 2000), "The watcher stayed after a clean exit.");
        _app.Dispose();
        _app = Start();
        _appWindow = WaitForAppWindow();
        Assert.Equal(1, Copies());
    }

    [Fact]
    public void A_restart_racing_the_watcher_recovers_once_and_keeps_working()
    {
        using var target = new TargetProcess(Spot);
        ShrinkAndAwaitWatcher(target);
        _app.Kill();
        _app.WaitForExit();
        _app.Dispose();
        _app = Start(); // Started at once, so it and the watcher reach the journal together.
        _appWindow = WaitForAppWindow();
        Assert.True(Desktop.WaitFor(() => !Windows.WindowInfo.IsConcealed(target.Handle), 2000));
        Assert.True(Desktop.WaitFor(() => Copies() == 1, 2000), "The watcher stayed.");
        Assert.False(File.Exists(Path.Combine(_data, "changed-windows.json")));
        Focus(target);
        Hotkey(Keys.T, target);
        Assert.True(Desktop.WaitFor(() => (Desktop.ExStyle(target.Handle) & User32.WS_EX_TOPMOST) != 0), "The new copy didn't work.");
    }

    private nint WaitForPip()
    {
        nint pip = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            pip = Desktop.FindProcessWindow(_app.Id, title => title.StartsWith("PiP - ", StringComparison.Ordinal));
            return pip != 0;
        }), $"Ctrl+Alt+P did not open a view. Shell state={Windows.FullScreen.Current()}, foreground={Windows.WindowInfo.Title(User32.GetForegroundWindow())}, appExited={_app.HasExited}");
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

    [Fact]
    public void Opacity_wheel_changes_the_pointed_window_and_passes_ordinary_scrolling_through()
    {
        var settings = new AppSettings { OpacityWheel = true, OpacityStepPercent = 3 };
        RestartWith(settings);
        using var target = new TargetProcess(Spot);
        Focus(target);
        // The foreground watch installs the shared hook within a tick or two of the window coming forward, which
        // takes longer on a busy machine. Until then the wheel scrolls the window, so keep trying for a while.
        bool applied = Desktop.WaitFor(() =>
        {
            Desktop.Key(Keys.ControlKey, true);
            Desktop.Key(Keys.Menu, true);
            try { Desktop.Wheel(target.Center, -1, target.Handle); }
            finally { Desktop.Key(Keys.Menu, false); Desktop.Key(Keys.ControlKey, false); }
            return Desktop.WaitFor(() => Desktop.Alpha(target.Handle) is not null, 400);
        }, 5000);
        Assert.True(applied && Desktop.Alpha(target.Handle) == 247,
            $"The opacity wheel didn't apply the configured 3% step: alpha={Desktop.Alpha(target.Handle)}.");
        while (target.NextReport(100) is not null) { } // Wheels sent before the hook was there.
        Assert.Null(target.NextReport(150));
        Desktop.Wheel(target.Center, -1, target.Handle);
        Assert.StartsWith("wheel -120", target.NextReport() ?? "no wheel report");
    }

    [Fact]
    public void Title_bar_drag_and_minimize_work_with_real_mouse_input()
    {
        using var target = new TargetProcess(Spot);
        Focus(target);
        Hotkey(Keys.P, target);
        nint pip = WaitForPip();
        Rectangle bounds = Desktop.BoundsOf(pip);
        Desktop.MoveMouse(new Point(bounds.Left + 60, bounds.Top + 60));
        int dpi = Dpi.Of(pip), height = Dpi.Scale(28, dpi);
        bool above = bounds.Top - height >= Screen.FromHandle(pip).WorkingArea.Top;
        Point grip = new(bounds.Left + Dpi.Scale(60, dpi), above ? bounds.Top - height / 2 : bounds.Bottom + height / 2);
        Size gripOffset = new(grip.X - bounds.Left, grip.Y - bounds.Top);
        nint bar = 0;
        Assert.True(Desktop.WaitFor(() =>
        {
            bar = User32.GetAncestor(Desktop.TopWindowAt(grip), User32.GA_ROOT);
            User32.GetWindowThreadProcessId(bar, out uint pid);
            return bar != pip && pid == _app.Id && (Desktop.ExStyle(bar) & User32.WS_EX_NOACTIVATE) != 0;
        }), "The title bar did not appear.");
        Point goal = new(800, 180);
        Desktop.Drag(grip, goal, bar);
        Assert.True(Desktop.WaitFor(() =>
        {
            Rectangle moved = Desktop.BoundsOf(pip);
            return Math.Abs(moved.Left - (goal.X - gripOffset.Width)) <= 2 && Math.Abs(moved.Top - (goal.Y - gripOffset.Height)) <= 2;
        }), Desktop.BoundsOf(pip).ToString());
        Assert.Equal(target.Handle, User32.GetForegroundWindow());
        Rectangle title = Desktop.BoundsOf(bar);
        Desktop.ClickOn(new Point(title.Right - Dpi.Scale(69, dpi), title.Top + height / 2), bar);
        Assert.True(Desktop.WaitFor(() => !Desktop.IsWindowVisible(pip)), "Minimize did not hide the view.");
        Assert.True(User32.IsWindow(pip));
        Assert.Equal(target.Handle, User32.GetForegroundWindow());
        Hotkey(Keys.P, target);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(pip)), "The hotkey could not close a suspended view.");
    }
}
