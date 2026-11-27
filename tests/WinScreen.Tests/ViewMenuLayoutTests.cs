using WinScreen.Localization;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

// 27 Nov: the view menu was regrouped. Use this view (only for a view that can be used), Original window, Picture,
// Capture and record, then Size, Options and Presets, then Help, Minimize and Close. Try smooth capture again sits on top
// while it applies, and Move away from text and images moved into Options.
[Trait("Category", "RealWindows")]
public sealed class ViewMenuLayoutTests
{
    private static void Pump(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    // What shows, top to bottom: headings and items by text, dividers as "-", notes left out.
    private static string[] Shown(ContextMenuStrip menu) => [.. menu.Items.Cast<ToolStripItem>()
        .Where(item => item.Available && item is not NoteRow && item.Name != "inputStatus")
        .Select(item => item is ToolStripSeparator ? "-" : item.Text!)];

    private static string T(string key) => UiText.T(key);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint into, int x, int y, int width, int height, nint from, int fromX, int fromY, int operation);

    // For checking by eye: the open menu as it is on screen, in light and in dark.
    internal static void Save(string name, params ToolStripDropDown[] open)
    {
        if (Environment.GetEnvironmentVariable("WINSCREEN_LAYOUT_EVIDENCE") is not { } folder) return;
        Rectangle area = open.Select(menu => menu.Bounds).Aggregate(Rectangle.Union);
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                MenuStyle.UseTheme(theme);
                foreach (ToolStripDropDown menu in open) menu.Invalidate(true);
                Pump(150);
                using var picture = new Bitmap(area.Width, area.Height);
                // CAPTUREBLT takes layered windows too, which a menu can be; CopyFromScreen refuses the flag.
                using (Graphics g = Graphics.FromImage(picture))
                {
                    nint screen = GetDC(0), into = g.GetHdc();
                    try { BitBlt(into, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, 0x00CC0020 | 0x40000000); }
                    finally { g.ReleaseHdc(into); ReleaseDC(0, screen); }
                }
                Directory.CreateDirectory(folder);
                picture.Save(Path.Combine(folder, $"{name}-{theme}.png"));
            }
        }
        finally { MenuStyle.UseTheme("system"); }
    }

    private static void Show(PipWindow view) => view.ContextMenuStrip!.Show(new Point(view.Left + 20, view.Top + 20));

    // Shown only while they apply, so left out of what the tests compare.
    private static readonly string[] Sometimes = ["pip.backend.retry", "menu.original.use", "menu.original.mute", "menu.original.muteBrowser", "menu.original.awayHidden"];

    private static string[] Open(PipWindow view, string name)
    {
        ContextMenuStrip menu = view.ContextMenuStrip!;
        Show(view);
        Assert.True(Desktop.WaitFor(() => menu.Visible, 2000, Application.DoEvents));
        Pump(50);
        string[] shown = Shown(menu);
        // Try smooth capture again shows, at the top, exactly when it can be done.
        Assert.Equal(view.CanRetryCapture, shown.Contains(T("pip.backend.retry")));
        if (view.CanRetryCapture) Assert.Equal(T("pip.backend.retry"), shown.First(text => text != "-"));
        Save($"view-menu-{name}", menu);
        menu.Close();
        Pump(30);
        return [.. shown.Where(text => !Sometimes.Select(T).Contains(text))];
    }

    private static readonly string[] Tail =
    [
        "-", "pip.size", "menu.picture.options", "pip.presets", "-", "menu.help", "pip.minimize", "pip.close",
    ];

    private static readonly string[] PictureAndCapture =
    [
        "menu.group.picture", "pip.freeze", "pip.turn", "pip.zoom.reset",
        "menu.group.capture", "pip.picture.copy", "pip.picture.copyText", "pip.picture.freezeCopy", "pip.picture.record", "pip.picture.cover",
    ];

    [Fact]
    public void A_window_view_shows_the_groups_in_order()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            try
            {
                view.ShowOnTop();
                view.Location = new Point(900, 200);
                Pump(1000);

                view.SetInteractive(true);
                string[] interactive = Open(view, "interactive");
                Assert.Equal([.. new[] { "menu.group.use", "menu.view.watch", "menu.view.interactive", "menu.view.hover", "menu.view.middleScroll",
                    "menu.group.original", "menu.original.keepHidden", "menu.original.goTo" }.Select(T)],
                    interactive.TakeWhile(text => text != T("menu.group.picture")).Where(text => text != "-"));
                Assert.Equal([.. PictureAndCapture.Select(T)], interactive.SkipWhile(text => text != T("menu.group.picture")).Take(PictureAndCapture.Length));
                Assert.Equal([.. Tail.Select(text => text == "-" ? "-" : T(text))], interactive.TakeLast(Tail.Length));
                Assert.DoesNotContain(T("pip.autoMove"), interactive);
                // Settings' Actions and shortcuts lists the view's commands in the menu's order.
                var keyed = (Dictionary<AppCommand, ToolStripMenuItem>)typeof(PipWindow)
                    .GetField("_keyed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
                IEnumerable<ToolStripItem> Walk(ToolStripItemCollection items) => items.Cast<ToolStripItem>()
                    .SelectMany(item => new[] { item }.Concat(item is ToolStripMenuItem parent ? Walk(parent.DropDownItems) : []));
                Assert.Equal(CommandCatalog.View, Walk(view.ContextMenuStrip!.Items).OfType<ToolStripMenuItem>()
                    .Select(item => keyed.FirstOrDefault(pair => pair.Value == item)).Where(pair => pair.Value is not null).Select(pair => pair.Key));

                view.SetInteractive(false);
                string[] watching = Open(view, "watching");
                Assert.DoesNotContain(T("menu.view.hover"), watching);
                Assert.DoesNotContain(T("menu.view.middleScroll"), watching);

                // Move away from text and images is in Options, first, and stays there each time Options opens.
                ToolStripMenuItem options = view.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == T("menu.picture.options"));
                for (int visit = 0; visit < 2; visit++)
                {
                    Show(view);
                    options.ShowDropDown();
                    Assert.True(Desktop.WaitFor(() => options.DropDown.Visible, 2000, Application.DoEvents));
                    Assert.Equal(T("pip.autoMove"), options.DropDownItems[0].Text);
                    Assert.Equal([T("pip.lockPosition"), T("pip.keepAspectRatio"), T("pip.titleBarAlways"), T("pip.clickThrough")],
                        options.DropDownItems.Cast<ToolStripItem>().Skip(1).Take(4).Select(item => item.Text));
                    Assert.False(options.DropDownItems[0].IsDisposed);
                    if (visit == 0) Save("view-menu-options", view.ContextMenuStrip, options.DropDown);
                    view.ContextMenuStrip.Close();
                    Pump(30);
                }

                view.SetCovers([new Rectangle(10, 10, 50, 50)]);
                Assert.Contains(T("pip.picture.uncover"), Open(view, "covers"));

                // Minimized, the view offers to try smooth capture again (Open checks it is at the top).
                view.SuspendView();
                Assert.True(view.CanRetryCapture);
                Open(view, "minimized");
            }
            finally { view.Close(); }
        });
    }

    [Fact]
    public void A_view_that_cant_be_used_has_no_use_heading()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 600, 400));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = false };
            try
            {
                view.ShowOnTop();
                view.Location = new Point(900, 200);
                if (view.CanInteract) { Skipped.Because("This test app can be used through a view without the switch."); return; }
                string[] shown = Open(view, "not-usable");
                Assert.DoesNotContain(T("menu.group.use"), shown);
                Assert.DoesNotContain(T("menu.view.watch"), shown);
                Assert.Equal(T("menu.group.original"), shown.First(text => text != "-"));
            }
            finally { view.Close(); }
        });
    }

    [Fact]
    public void A_display_view_has_no_original_group_and_can_save_its_source()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            UiText.Initialize("en");
            using var view = new PipWindow(new ScreenPipSource(Screen.PrimaryScreen!.DeviceName, new Rectangle(100, 100, 400, 300)), new WindowController([]));
            try
            {
                view.ShowOnTop();
                view.Location = new Point(900, 200);
                string[] shown = Open(view, "display");
                Assert.DoesNotContain(T("menu.group.original"), shown);
                Assert.DoesNotContain(T("menu.group.use"), shown);
                Assert.Equal(T("menu.group.picture"), shown.First(text => text != "-"));
                ToolStripMenuItem presets = view.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == T("pip.presets"));
                Show(view);
                presets.ShowDropDown();
                Assert.True(Desktop.WaitFor(() => presets.DropDown.Visible, 2000, Application.DoEvents));
                Assert.Contains(presets.DropDownItems.Cast<ToolStripItem>(), item => item.Text == T("pip.bookmarks.save"));
                view.ContextMenuStrip.Close();
            }
            finally { view.Close(); }
        });
    }
}
