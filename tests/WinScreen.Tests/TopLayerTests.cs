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
                Assert.True(IsAbove(tipWindow, menu.Handle), "The tip went under its menu.");
                Assert.True(IsAbove(menu.Handle, taskbar), "The menu went under the taskbar.");
                Assert.True(IsAbove(taskbar, view.Handle), "The view went over the taskbar: " + TopOrder(view.Handle, menu.Handle, tipWindow, taskbar));
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

    private static string TopOrder(nint view, nint menu, nint tip, nint taskbar)
    {
        var names = new List<string>();
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5);
        for (int i = 0; window != 0 && i < 40; i++, window = User32.GetWindow(window, 2))
        {
            if (!User32.IsWindowVisible(window)) continue;
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

    private static bool IsAbove(nint upper, nint lower)
    {
        for (nint window = User32.GetWindow(upper, 2); window != 0; window = User32.GetWindow(window, 2))
            if (window == lower) return true;
        return false;
    }

    private static nint RootAt(Point point) => User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);

    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
