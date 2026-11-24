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
}
