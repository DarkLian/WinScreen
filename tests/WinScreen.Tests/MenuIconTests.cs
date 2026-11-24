using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

// The owner, 23 Nov: the newer menu items had plain icons or none. Every command with an icon has a coloured one, drawn
// like the rest (Tray/ColorIcons.cs), never a plain font glyph.
public sealed class MenuIconTests
{
    [Fact]
    public void No_two_toolbar_buttons_look_the_same()
    {
        var same = CommandCatalog.ToolbarButtons.GroupBy(command => MenuParts.IconOf(command) ?? MenuParts.More).Where(group => group.Count() > 1);
        Assert.Empty(same.Select(group => string.Join(", ", group)));
        Assert.All(CommandCatalog.ToolbarButtons, command => Assert.True(ColorIcons.Has(MenuParts.IconOf(command) ?? MenuParts.More)));
    }

    [Fact]
    public void Every_command_with_an_icon_has_a_coloured_one()
    {
        AppCommand[] commands = Enum.GetValues<AppCommand>();
        var plain = commands.Where(command => MenuParts.IconOf(command) is { } glyph && !ColorIcons.Has(glyph)).ToList();
        Assert.Empty(plain);
        // The view's own menu and the new window menu items.
        foreach (AppCommand command in CommandCatalog.View)
            Assert.NotNull(MenuParts.IconOf(command));
        foreach (char glyph in new[] { MenuParts.Volume, MenuParts.Pause, MenuParts.Rotate, MenuParts.Text, MenuParts.Record, MenuParts.Hide,
                     MenuParts.News, MenuParts.Diagnostic })
            Assert.True(ColorIcons.Has(glyph));

        if (Environment.GetEnvironmentVariable("WINSCREEN_LAYOUT_EVIDENCE") is not { } folder) return;
        char[] glyphs = [.. commands.Select(MenuParts.IconOf).OfType<char>().Distinct()
            .Concat([MenuParts.News, MenuParts.Diagnostic, MenuParts.Sync])];
        const int Size = 32;
        glyphs = [.. glyphs, ColorIcons.Displays, ColorIcons.WindowSize];
        using var strip = new Bitmap(glyphs.Length * (Size + 8) + 8, Size + 16);
        using (Graphics g = Graphics.FromImage(strip))
        {
            g.Clear(Color.White);
            for (int i = 0; i < glyphs.Length; i++)
                if (ColorIcons.Of(glyphs[i], Size) is { } icon) g.DrawImage(icon, 8 + i * (Size + 8), 8);
        }
        Directory.CreateDirectory(folder);
        strip.Save(Path.Combine(folder, "menu-icons.png"));
    }

    // The owner, 24 Nov: the slider, Fill display and most of Arrange window had none.
    [Fact]
    public void The_window_menu_and_arrange_have_icons()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            WindowMenuBuilder Builder() => new(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
            {
                OpacityPercent = _ => 90, SetOpacityPercent = (_, _) => { }, MoveToDisplay = (_, _) => { }, ResizeTo = (_, _) => { },
            };
            using var menu = new ContextMenuStrip();
            Builder().Populate(menu.Items, false);
            Assert.NotNull(menu.Items.OfType<OpacityRow>().Single().Image);
            ToolStripMenuItem effects = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == Localization.UiText.T("menu.effects"));
            Assert.False(MenuParts.IsExperimental(effects));
            Assert.Equal([AppCommand.ToggleDark], effects.DropDownItems.OfType<ToolStripMenuItem>().Where(MenuParts.IsExperimental).Select(item => (AppCommand)item.Tag!));
            ToolStripMenuItem borderless = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == Localization.UiText.T("window.borderless"));
            Image?[] frames = [.. borderless.DropDownItems.Cast<ToolStripItem>().Select(item => item.Image)];
            Assert.All(frames, Assert.NotNull);
            Assert.Equal(frames.Length, frames.Distinct().Count());
            foreach (bool places in new[] { true, false })
            {
                using var arrange = new ContextMenuStrip();
                Builder().PopulateArrange(arrange.Items, places);
                ToolStripMenuItem[] rows = [.. arrange.Items.OfType<ToolStripMenuItem>()];
                Assert.Empty(rows.Where(row => row.Image is null).Select(row => row.Text));
                Assert.Equal(rows.Length, rows.Select(row => row.Image).Distinct().Count());
                Save(arrange, places ? "menu-arrange-places.png" : "menu-arrange.png");
            }
            using var opacity = new ContextMenuStrip();
            Builder().PopulateOpacity(opacity.Items);
            Save(opacity, "menu-opacity.png");
            Save(menu, "menu-window.png");
        });

        static void Save(ToolStripDropDownMenu menu, string name)
        {
            MenuRenderer.Apply(menu);
            if (Environment.GetEnvironmentVariable("WINSCREEN_LAYOUT_EVIDENCE") is not { } folder) return;
            menu.CreateControl();
            menu.PerformLayout();
            using var picture = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(picture, new Rectangle(Point.Empty, menu.Size));
            Directory.CreateDirectory(folder);
            picture.Save(Path.Combine(folder, name));
        }
    }

    // The tray's Last window shows the window's own icon, or its program's when it has none.
    [Fact]
    public void A_window_gives_its_own_icon_at_the_size_asked_for()
    {
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Color own = Color.FromArgb(200, 40, 160);
            using var drawn = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(drawn)) g.Clear(own);
            using var icon = Icon.FromHandle(drawn.GetHicon());
            using var window = new Form { Icon = icon };
            _ = window.Handle;
            foreach (int size in new[] { 16, 20, 32 })
            {
                using Bitmap? shown = WindowIcons.Of(window.Handle, size);
                Assert.NotNull(shown);
                Assert.Equal(new Size(size, size), shown.Size);
                Color middle = shown.GetPixel(size / 2, size / 2);
                Assert.True(Math.Abs(middle.R - own.R) + Math.Abs(middle.G - own.G) + Math.Abs(middle.B - own.B) < 12 && middle.A > 250, $"{size}: {middle}");
            }
            Assert.Null(WindowIcons.Of(0, 16));
        });
    }
}
