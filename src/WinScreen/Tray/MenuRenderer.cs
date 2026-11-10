using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>The colours WinScreen's menus are drawn with, in the light and the dark theme.</summary>
internal static class MenuStyle
{
    internal static bool Dark => Application.IsDarkModeEnabled;

    /// <summary>
    /// Light, dark or as Windows is ("system"). It can change while WinScreen runs: windows made afterwards and
    /// everything WinScreen draws itself follow at once.
    /// </summary>
    internal static void UseTheme(string theme)
    {
#pragma warning disable WFO5001 // WinForms marks dark mode as experimental; anything it doesn't draw dark stays readable.
        Application.SetColorMode(theme switch
        {
            "light" => SystemColorMode.Classic,
            "dark" => SystemColorMode.Dark,
            _ => SystemColorMode.System,
        });
#pragma warning restore WFO5001
    }

    // Windows 11's own interface face where it exists, and the face Windows 10 uses otherwise.
    private static readonly string Face =
        FontFamily.Families.Any(family => family.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    internal static readonly Font Font = new(Face, 9.75f);
    internal static readonly Font SmallFont = new(Face, 8.25f);
    internal static readonly Font TitleFont = new(Face, 9.75f, FontStyle.Bold);
    // Menus use Windows 11's own menu sizes: words at 14 px (10.5 pt at 100%), group headings at 12 px, the title in semibold.
    internal static readonly Font MenuFont = new(Face, 10.5f);
    internal static readonly Font MenuHeaderFont = new(Face, 9f);
    internal static readonly Font MenuTitleFont = new(
        FontFamily.Families.FirstOrDefault(family => family.Name.StartsWith("Segoe UI Variable Text Semib", StringComparison.Ordinal))?.Name ?? "Segoe UI Semibold", 10.5f);
    internal static Color Back => Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
    internal static Color Text => Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27);
    internal static Color Muted => Dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);
    internal static Color Disabled => Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(160, 160, 160);
    internal static Color Hover => Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
    internal static Color Line => Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);
    internal static Color Accent => Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
    internal static Color AccentBack => Dark ? Color.FromArgb(40, 62, 76) : Color.FromArgb(221, 235, 247);
    /// <summary>The tag on what is experimental, in menus and in Settings: Windows 11's caution colours.</summary>
    internal static Color TagBack => Dark ? Color.FromArgb(67, 53, 25) : Color.FromArgb(255, 244, 206);
    internal static Color TagText => Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(121, 82, 0);
}

/// <summary>
/// Draws WinScreen's menus: no icon gutter, a rounded highlight under the pointer, a tinted mark for what is on,
/// thin dividers, and rounded corners where Windows offers them.
/// </summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly MenuRenderer Instance = new();
    private static readonly ConditionalWeakTable<ToolStripDropDown, object> Styled = [];
    /// <summary>The name of a menu's first line when it says what the menu is about; it is drawn as a title, not as a greyed command.</summary>
    internal const string TitleName = "inputStatus";
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    private MenuRenderer() : base(new Colors()) => RoundedEdges = false;

    /// <summary>Gives a menu this look, and its submenus as they open, rows that appear later included.</summary>
    internal static void Apply(ToolStripDropDown menu)
    {
        if (Styled.TryGetValue(menu, out _)) return;
        Styled.Add(menu, menu);
        menu.Renderer = Instance;
        menu.BackColor = MenuStyle.Back;
        menu.Font = MenuStyle.MenuFont;
        menu.ForeColor = MenuStyle.Text;
        if (menu is ToolStripDropDownMenu withMargin) withMargin.ShowCheckMargin = false;
        menu.Opening += (_, _) => Prepare(menu);
        menu.Opened += (_, _) => Round(menu);
        Prepare(menu);
    }

    /// <summary>
    /// Two layouts, measured at 100% from menus the owner chose. With icons, as File Explorer's: 14 px words, rows 31 px
    /// apart, an icon 14 px in from the edge and words 40 px in. With no icons and nothing ticked there is no icon
    /// column: 13 px words, rows 28 px apart, words and headings 18 px in.
    /// </summary>
    private static void Prepare(ToolStripDropDown menu)
    {
        // The light or dark look may have changed since the menu was made.
        if (menu.BackColor != MenuStyle.Back) menu.BackColor = MenuStyle.Back;
        if (menu.ForeColor != MenuStyle.Text) menu.ForeColor = MenuStyle.Text;
        int gap = MenuParts.Scale(5);
        bool icons = menu.Items.Cast<ToolStripItem>().Any(item => item.Image is not null ||
            item is ToolStripMenuItem { Checked: true } or ToolStripMenuItem { CheckOnClick: true });
        if (menu is ToolStripDropDownMenu withMargin && withMargin.ShowImageMargin != icons) withMargin.ShowImageMargin = icons;
        int inset = MenuParts.Scale(icons ? 8 : 13);
        // Beside icons the words are a size up, as in File Explorer's menu; a list of words alone is set a little smaller.
        Font font = icons ? MenuStyle.MenuFont : MenuStyle.Font;
        if (menu.Font != font) menu.Font = font;
        foreach (ToolStripItem item in menu.Items)
        {
            // A title is measured in the font it is drawn in, or a long one runs off the menu's edge.
            if (item.Name == TitleName && item.Font != MenuStyle.MenuTitleFont) item.Font = MenuStyle.MenuTitleFont;
            if (item is ToolStripMenuItem entry)
            {
                // As much room on the right as the words move right, so a long one is never cut off.
                var padding = new Padding(0, gap, TextRoom(menu), gap);
                if (entry.Padding != padding) entry.Padding = padding;
                if (entry.HasDropDownItems) Apply(entry.DropDown);
            }
            else if (item is ToolStripLabel label && label.Padding.Left != inset)
                label.Padding = new Padding(inset, 0, 0, 0);
            else if (item is ToolStripControlHost host && host.Margin.Left != MenuParts.Scale(4))
                host.Margin = host.Margin with { Left = MenuParts.Scale(4) };
        }
    }

    /// <summary>How far an icon moves right of where Windows places it, to sit 14 px in as Windows 11's own do.</summary>
    private static int IconRoom => MenuParts.Scale(10);

    /// <summary>How far the words move right of where Windows places them: to 40 px in beside icons, 18 px without.</summary>
    private static int TextRoom(ToolStrip? menu) => MenuParts.Scale(menu is ToolStripDropDownMenu { ShowImageMargin: false } ? 7 : 4);

    /// <summary>The space between an experimental command's words and its tag.</summary>
    private static int TagGap => MenuParts.Scale(8);

    private static void Round(ToolStripDropDown menu)
    {
        if (!menu.IsHandleCreated) return;
        int round = DWMWCP_ROUND;
        Dwmapi.DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, in round, sizeof(int)); // Windows 11; refused before it.
    }

    private static GraphicsPath Rounded(Rectangle area, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(area.Left, area.Top, d, d, 180, 90);
        path.AddArc(area.Right - d, area.Top, d, d, 270, 90);
        path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
        path.AddArc(area.Left, area.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuStyle.Back);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { } // No gutter behind the icons.

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(MenuStyle.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item is ToolStripMenuItem { DropDown.Visible: true })) return;
        var area = new Rectangle(MenuParts.Scale(4), 0, e.Item.Width - MenuParts.Scale(8), e.Item.Height);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Rounded(area, MenuParts.Scale(4));
        using var brush = new SolidBrush(MenuStyle.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Centred on the whole row, as the icon and the arrow are; Windows places the words a little high.
        bool shortcut = e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } shown } && e.Text == shown;
        int shift = shortcut || e.Item is ToolStripLabel ? 0 : TextRoom(e.ToolStrip);
        if (shortcut)
        {
            // Only the keys are drawn; the room kept for an experimental command's tag stays empty.
            e.Text = MenuParts.KeysOnly(e.Text);
            if (e.Text.Length == 0) return;
        }
        e.TextRectangle = new Rectangle(e.TextRectangle.Left + shift, 0, e.TextRectangle.Width, e.Item.Height);
        e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter;
        if (e.Item is ToolStripLabel) e.TextColor = MenuStyle.Muted;
        else if (e.Item.Name == TitleName)
        {
            // Drawn here: a line that can't be chosen would otherwise be greyed like an unavailable command.
            TextRenderer.DrawText(e.Graphics, e.Text, MenuStyle.MenuTitleFont, e.TextRectangle, MenuStyle.Text, e.TextFormat);
            return;
        }
        else if (!e.Item.Enabled) e.TextColor = MenuStyle.Disabled;
        else if (e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } keys } && e.Text == keys) e.TextColor = MenuStyle.Muted;
        else if (e.Item is ToolStripMenuItem { Checked: true }) e.TextColor = MenuStyle.Accent;
        else e.TextColor = MenuStyle.Text;
        base.OnRenderItemText(e);
        if (shortcut || !MenuParts.IsExperimental(e.Item) || string.IsNullOrEmpty(e.Text)) return;
        int words = TextRenderer.MeasureText(e.Graphics, e.Text, e.TextFont, e.TextRectangle.Size, e.TextFormat).Width;
        MenuParts.DrawTag(e.Graphics, e.TextRectangle.Left + words + TagGap, e.Item.Height / 2, MenuParts.Scale, e.Item.Enabled);
    }

    /// <summary>What is on gets a tinted square, with its icon in it or a tick when it has none.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle r = Centred(e.ImageRectangle, e.Item);
        Rectangle box = Rectangle.Inflate(r, MenuParts.Scale(3), MenuParts.Scale(3));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Rounded(box, MenuParts.Scale(4)))
        using (var back = new SolidBrush(MenuStyle.AccentBack))
            e.Graphics.FillPath(back, path);
        if (e.Item.Image is not null) return; // The icon is drawn over the square next.
        using var pen = new Pen(e.Item.Enabled ? MenuStyle.Accent : MenuStyle.Disabled, MenuParts.Scale(2) * 0.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen,
        [
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
        ]);
    }

    /// <summary>An icon's place moved to the middle of its row: Windows puts it a little high.</summary>
    private static Rectangle Centred(Rectangle image, ToolStripItem? item) =>
        item is null ? image : image with { X = image.X + IconRoom, Y = (item.Height - image.Height) / 2 };

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null || e.Item is null) { base.OnRenderItemImage(e); return; }
        Rectangle r = Centred(e.ImageRectangle, e.Item);
        Image image = MenuParts.ForCurrentTheme(e.Image);
        if (e.Item.Enabled) { e.Graphics.DrawImage(image, r); return; }
        using Image faded = CreateDisabledImage(image);
        e.Graphics.DrawImage(faded, r);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(MenuStyle.Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, MenuParts.Scale(8), y, e.Item.Width - MenuParts.Scale(8), y);
    }

    /// <summary>A thin chevron centred on the row, as Windows 11 draws one, in place of the small filled arrow.</summary>
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Direction != ArrowDirection.Right && e.Direction != ArrowDirection.Left) { base.OnRenderArrow(e); return; }
        Rectangle area = e.ArrowRectangle;
        float half = MenuParts.Scale(4), x = area.Left + area.Width / 2f, y = (e.Item?.ContentRectangle is { } row ? row.Top + row.Height / 2f : area.Top + area.Height / 2f);
        float tip = e.Direction == ArrowDirection.Right ? half / 2 : -half / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item is { Enabled: false } ? MenuStyle.Disabled : MenuStyle.Muted, Math.Max(1f, MenuParts.Scale(1) * 1.1f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, [new PointF(x - tip, y - half), new PointF(x + tip, y), new PointF(x - tip, y + half)]);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuStyle.Back;
        public override Color MenuBorder => MenuStyle.Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => MenuStyle.Hover;
        public override Color ImageMarginGradientBegin => MenuStyle.Back;
        public override Color ImageMarginGradientMiddle => MenuStyle.Back;
        public override Color ImageMarginGradientEnd => MenuStyle.Back;
        public override Color SeparatorDark => MenuStyle.Line;
        public override Color SeparatorLight => MenuStyle.Line;
        public override Color CheckBackground => MenuStyle.AccentBack;
        public override Color CheckSelectedBackground => MenuStyle.AccentBack;
        public override Color CheckPressedBackground => MenuStyle.AccentBack;
    }
}
