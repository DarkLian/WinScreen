using System.Reflection;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>WinScreen's own menus stay above the things it keeps on top, and the tray menu reads cleanly.</summary>
[Trait("Category", "RealWindows")]
public sealed class TopLayerTests
{
    [Fact]
    public void The_tray_menu_never_shows_two_dividers_in_a_row()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-traymenu-").FullName;
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var settings = new AppSettings { WelcomeShown = true, ShowNotifications = false, ShowToolbar = false,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => "") };
                using var app = new TrayApplication(new WindowController([]), settings, Path.Combine(folder, "settings.json"));
                try
                {
                    var tray = (NotifyIcon)typeof(TrayApplication).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
                    ToolStripItem[] items = tray.ContextMenuStrip!.Items.Cast<ToolStripItem>().ToArray();
                    Assert.IsNotType<ToolStripSeparator>(items[0]);
                    Assert.IsNotType<ToolStripSeparator>(items[^1]);
                    for (int i = 1; i < items.Length; i++)
                        Assert.False(items[i] is ToolStripSeparator && items[i - 1] is ToolStripSeparator, $"Two dividers in a row at {i}.");
                }
                finally { app.ExitThread(); }
            });
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Something_lifted_to_the_top_stays_under_an_open_menu_and_its_submenu()
    {
        Func<IEnumerable<nint>>? before = TopLayer.Above;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            using var menu = new ContextMenuStrip();
            var more = new ToolStripMenuItem("More");
            more.DropDownItems.Add("Inner one");
            more.DropDownItems.Add("Inner two");
            menu.Items.Add(more);
            menu.Items.Add("Second");
            menu.AutoClose = false; // The cover below takes focus, which would otherwise close the menu.
            more.DropDown.AutoClose = false;
            menu.Show(new Point(screen.Left + 200, screen.Top + 200));
            more.ShowDropDown();
            Pump(200);
            Rectangle outer = menu.Bounds, inner = more.DropDown.Bounds;
            // A plain always-on-top window over both, standing in for a view, its title bar or its chip.
            using var cover = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                Bounds = Rectangle.Union(outer, inner), BackColor = Color.Magenta,
            };
            cover.Show();
            var inMenu = new Point(outer.Left + outer.Width / 2, outer.Bottom - 6);
            var inSubmenu = new Point(inner.Left + inner.Width / 2, inner.Bottom - 6);
            try
            {
                const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
                User32.SetWindowPos(cover.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                Pump(50);
                Assert.Equal(cover.Handle, RootAt(inMenu)); // Without the rule, it really would cover the menu.

                TopLayer.Above = () => TopLayer.OpenDropDowns(menu);
                User32.SetWindowPos(cover.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                TopLayer.KeepAbove();
                Pump(50);
                Assert.Equal(menu.Handle, RootAt(inMenu));
                Assert.Equal(more.DropDown.Handle, RootAt(inSubmenu));
            }
            finally
            {
                TopLayer.Above = before;
                cover.Close();
                menu.Close();
            }
        });
    }

    // Owner, 1.3.0: the window menu opened behind pinned ExitLag, which Windows kept above it; even WinScreen's pin
    // border, lifted every 33 ms, stayed under it, and ExitLag wouldn't go under either. Agreed with the owner: a window
    // that goes under keeps its pin; one that won't is unpinned while the menu is open, and pinned again after.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pinned_app_over_an_open_menu_goes_under_it_or_is_unpinned_until_it_closes(bool refusesToGoUnder)
    {
        Func<IEnumerable<nint>>? before = TopLayer.Above;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            // Standing in for an open WinScreen menu: always on top, and one TopLayer keeps above.
            using var menu = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                Bounds = new Rectangle(screen.Left + 240, screen.Top + 240, 160, 120), BackColor = Color.Magenta,
            };
            menu.Show();
            Pump(200);
            Rectangle outer = menu.Bounds;
            using var pinned = new TargetProcess(Rectangle.Inflate(outer, 60, 60), title: "WinScreen layer test pinned");
            var inMenu = new Point(outer.Left + outer.Width / 2, outer.Bottom - 6);
            try
            {
                // Pinned as WinScreen pins, and over the menu, as the owner's app was.
                Desktop.GainForegroundRights();
                WindowController.Pin(pinned.Handle);
                User32.SetWindowPos(menu.Handle, pinned.Handle, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                Assert.True(Desktop.WaitFor(() => RootAt(inMenu) == pinned.Handle, 2000, () => Pump(20)),
                    "The pinned window never came over the menu.\n" + Order());
                Assert.True((Desktop.ExStyle(pinned.Handle) & User32.WS_EX_TOPMOST) != 0 && (Desktop.ExStyle(menu.Handle) & User32.WS_EX_TOPMOST) != 0,
                    $"The pinned window and the menu aren't both always on top: menu visible {User32.IsWindowVisible(menu.Handle)}, " +
                    $"ex {Desktop.ExStyle(menu.Handle):X}.\n" + Order());

                // Windows ignores the menu's lift, as it did with the owner's app.
                Func<nint, bool> refused = window => window == menu.Handle || refusesToGoUnder && window == pinned.Handle;
                TopLayer.Above = () => [menu.Handle];
                long lifts = TopLayer.LiftCount, pushes = TopLayer.PushCount;
                string order = Order();
                TopLayer.KeepAbove(refused);
                Pump(50);
                string Report() => $"Menu {menu.Handle:X}, pinned {pinned.Handle:X}; {TopLayer.LiftCount - lifts} lifts, " +
                                   $"{TopLayer.PushCount - pushes} pushes.\nBefore:\n{order}\nAfter:\n{Order()}";
                bool topmost = (Desktop.ExStyle(pinned.Handle) & User32.WS_EX_TOPMOST) != 0;
                Assert.True(topmost != refusesToGoUnder, (refusesToGoUnder ? "It wasn't unpinned. " : "It lost its pin. ") + Report());
                Assert.Equal(refusesToGoUnder, TopLayer.IsUnpinnedForNow(pinned.Handle));
                Assert.True(RootAt(inMenu) == menu.Handle, "The menu is still covered. " + Report());

                // Once in order, it's left alone.
                lifts = TopLayer.LiftCount;
                pushes = TopLayer.PushCount;
                order = Order();
                TopLayer.KeepAbove(refused);
                Assert.True(TopLayer.LiftCount == lifts && TopLayer.PushCount == pushes, "Something moved again. " + Report());

                // The menu closed: pinned again.
                menu.Hide();
                TopLayer.KeepAbove();
                Assert.True((Desktop.ExStyle(pinned.Handle) & User32.WS_EX_TOPMOST) != 0, "It wasn't pinned again. " + Report());
                Assert.False(TopLayer.IsUnpinnedForNow(pinned.Handle));
            }
            finally
            {
                TopLayer.Above = before;
                TopLayer.PinChanged(pinned.Handle, false);
                menu.Close();
            }
        });
    }

    [Fact]
    public void A_view_lifted_to_the_top_stays_under_the_taskbar_and_tips_stay_over_menus()
    {
        Func<IEnumerable<nint>>? before = TopLayer.Above;
        nint taskbar = User32.FindWindow("Shell_TrayWnd", null);
        Assert.NotEqual(0, taskbar);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle bar = Desktop.BoundsOf(taskbar);
            using var view = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                Bounds = Rectangle.Inflate(bar, 0, 40), BackColor = Color.Magenta, TopMost = true,
            };
            using var menu = new ContextMenuStrip { AutoClose = false };
            menu.Items.Add("Something with a tip").ToolTipText = "x";
            using var menuOwner = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Bounds = new Rectangle(0, 0, 1, 1) };
            using var tipForm = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
            Func<nint, bool> wasTip = TopLayer.IsTip;
            try
            {
                view.Show();
                TopLayer.Raise(view.Handle);
                Pump(100);
                var onBar = new Point(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
                Assert.Equal(taskbar, RootAt(onBar)); // The taskbar stays reachable over a view.

                Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
                // A menu belongs to the window it's shown for, and moves with it; the tray menu's is hidden.
                menuOwner.Show();
                menu.Show(menuOwner, menuOwner.PointToClient(new Point(screen.Left + 300, screen.Top + 300)));
                TopLayer.Above = () => TopLayer.OpenDropDowns(menu);
                // A small always-on-top window over the menu, standing in for a hover tip.
                tipForm.Bounds = new Rectangle(menu.Left + 10, menu.Top + 5, 120, 24);
                tipForm.Show();
                User32.SetWindowPos(tipForm.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                nint tipWindow = tipForm.Handle;
                TopLayer.IsTip = window => window == tipWindow;
                Pump(100);
                TopLayer.Raise(view.Handle); // What the menu guard and every view does over and over.
                TopLayer.KeepAbove();
                Pump(50);
                Assert.True(Desktop.IsAbove(tipWindow, menu.Handle), "The tip went under its menu.");
                Assert.True(Desktop.IsAbove(menu.Handle, taskbar), "The menu went under the taskbar.");
                Assert.True(Desktop.IsAbove(taskbar, view.Handle), "The view went over the taskbar: " + TopOrder(view.Handle, menu.Handle, tipWindow, taskbar));
            }
            finally
            {
                TopLayer.Above = before;
                TopLayer.IsTip = wasTip;
                tipForm.Close();
                menu.Close();
                view.Close();
            }
        });
    }

    /// <summary>
    /// Windows' own passing surfaces, such as the screen snip or a taskbar flyout, stay over WinScreen's menus and
    /// whatever it lifts. Lifting over them made the two take turns on top, which flickered.
    /// </summary>
    [Fact]
    public void Windows_own_surfaces_stay_over_menus_and_lifted_windows()
    {
        Func<IEnumerable<nint>>? before = TopLayer.Above;
        Func<nint, bool> wasSystem = TopLayer.IsSystemSurface;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            Form Make(Rectangle bounds, Color color) => new()
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                Bounds = bounds, BackColor = color,
            };
            using var menu = new ContextMenuStrip { AutoClose = false };
            menu.Items.Add("Something");
            menu.Items.Add("Something else");
            using var view = Make(new(screen.Left + 150, screen.Top + 150, 500, 400), Color.Magenta);
            using var overlay = Make(new(screen.Left + 100, screen.Top + 100, 700, 600), Color.Cyan);
            try
            {
                menu.Show(new Point(screen.Left + 200, screen.Top + 200));
                view.Show();
                overlay.Show();
                nint shell = overlay.Handle;
                TopLayer.IsSystemSurface = window => window == shell;
                TopLayer.Above = () => TopLayer.OpenDropDowns(menu);
                const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
                User32.SetWindowPos(view.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                User32.SetWindowPos(overlay.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                Pump(50);

                TopLayer.Raise(view.Handle);
                TopLayer.KeepAbove();
                Pump(50);
                Assert.True(Desktop.IsAbove(overlay.Handle, menu.Handle), "The menu went over Windows' own surface.");
                Assert.True(Desktop.IsAbove(overlay.Handle, view.Handle), "The view went over Windows' own surface.");
                Assert.True(Desktop.IsAbove(menu.Handle, view.Handle), "The menu went under the view.");

                // Settled: the guard ticking over and over moves nothing.
                long lifts = TopLayer.LiftCount;
                for (int i = 0; i < 10; i++) { TopLayer.KeepAbove(); TopLayer.Raise(view.Handle); Pump(20); }
                Assert.True(lifts == TopLayer.LiftCount, $"Still lifting ({TopLayer.LiftCount - lifts}): " +
                    TopOrder(view.Handle, menu.Handle, overlay.Handle, User32.FindWindow("Shell_TrayWnd", null)));
            }
            finally
            {
                TopLayer.Above = before;
                TopLayer.IsSystemSurface = wasSystem;
                overlay.Close();
                view.Close();
                menu.Close();
            }
        });
    }

    private static string TopOrder(nint view, nint menu, nint tip, nint taskbar)
    {
        var names = new List<string>();
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5);
        for (int i = 0; window != 0 && i < 40; window = User32.GetWindow(window, 2))
        {
            if (!User32.IsWindowVisible(window)) continue;
            i++;
            string name = window == view ? "VIEW" : window == menu ? "MENU" : window == tip ? "TIP" : window == taskbar ? "TASKBAR" : WindowInfo.ClassName(window);
            bool top = (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
            names.Add(name + (top ? "*" : ""));
        }
        return string.Join(" > ", names);
    }

    /// <summary>
    /// An app's menu and its submenu are lifted together, oldest first. Lifting one alone treated the submenu as a
    /// window covering its parent and put the parent over it.
    /// </summary>
    [Fact]
    public void A_menu_chain_is_lifted_in_order_and_a_correct_order_is_left_alone()
    {
        Desktop.GainForegroundRights(); // Before any window: on a thread with windows it would wait on their painting.
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
            Form Make(Rectangle bounds, Color color) => new()
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                Bounds = bounds, BackColor = color,
            };
            using var parent = Make(new(screen.Left + 200, screen.Top + 200, 240, 300), Color.Blue);
            using var child = Make(new(screen.Left + 420, screen.Top + 240, 220, 200), Color.Lime);
            using var cover = Make(new(screen.Left + 150, screen.Top + 150, 600, 450), Color.Magenta);
            using var active = new Form { StartPosition = FormStartPosition.Manual, Bounds = new(screen.Left + 900, screen.Top + 600, 200, 120) };
            active.Show();
            parent.Show(); child.Show(); cover.Show();
            try
            {
                // The test's own window is the active one, so nothing the person has open is over these.
                Desktop.Activate(active.Handle);
                Point overlap = new(screen.Left + 430, screen.Top + 300), parentOnly = new(screen.Left + 220, screen.Top + 470);
                const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
                User32.SetWindowPos(cover.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                Pump(50);
                Assert.Equal(cover.Handle, RootAt(overlap));

                TopLayer.RaiseGroup([parent.Handle, child.Handle]);
                Pump(50);
                Assert.Equal(child.Handle, RootAt(overlap));
                Assert.Equal(parent.Handle, RootAt(parentOnly));

                // The order is already right and nothing ordinary is ahead: nothing is lifted.
                long lifts = TopLayer.LiftCount;
                TopLayer.RaiseGroup([parent.Handle, child.Handle]);
                Assert.Equal(lifts, TopLayer.LiftCount);

                // Lifting the parent by itself no longer counts as covered by its own submenu once grouped:
                // a wrong order inside the chain is repaired.
                User32.SetWindowPos(parent.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, flags);
                Pump(50);
                Assert.Equal(parent.Handle, RootAt(overlap));
                TopLayer.RaiseGroup([parent.Handle, child.Handle]);
                Pump(50);
                Assert.Equal(child.Handle, RootAt(overlap));
                Assert.Equal(parent.Handle, RootAt(parentOnly));
            }
            finally
            {
                cover.Close(); child.Close(); parent.Close(); active.Close();
            }
        });
    }

    private static nint RootAt(Point point) => User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);

    // The always-on-top windows from the top, for a failure message.
    private static string Order()
    {
        var lines = new List<string>();
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5);
        for (int seen = 0; window != 0 && seen < 4096 && lines.Count < 15; seen++, window = User32.GetWindow(window, 2))
        {
            if (!User32.IsWindowVisible(window)) continue;
            long style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
            if ((style & User32.WS_EX_TOPMOST) == 0) break;
            User32.GetWindowThreadProcessId(window, out uint process);
            User32.GetWindowRect(window, out Rect r);
            lines.Add($"  {window:X} {WindowInfo.ClassName(window)} process={process} ex={style:X} {r.Left},{r.Top},{r.Right},{r.Bottom}");
        }
        return string.Join("\n", lines);
    }

    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
