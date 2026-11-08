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
                TopLayer.Above = null;
                TopLayer.Raise(cover.Handle);
                Pump(50);
                Assert.Equal(cover.Handle, RootAt(inMenu)); // Without the rule, it really would cover the menu.

                TopLayer.Above = () => TopLayer.OpenDropDowns(menu);
                TopLayer.Raise(cover.Handle);
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

    private static nint RootAt(Point point) => User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);

    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
