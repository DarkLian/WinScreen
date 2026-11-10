using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>The colours WinScreen's menus are drawn with, in the light and the dark theme.</summary>
internal static class MenuStyle
{
    internal static bool Dark => Application.IsDarkModeEnabled;

    // Windows 11's own interface face where it exists, and the face Windows 10 uses otherwise.
    private static readonly string Face =
        FontFamily.Families.Any(family => family.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    internal static readonly Font Font = new(Face, 9.75f);
    internal static readonly Font SmallFont = new(Face, 8.25f);
    internal static readonly Font TitleFont = new(Face, 9.75f, FontStyle.Bold);
    internal static Color Back => Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
    internal static Color Text => Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27);
    internal static Color Muted => Dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);
    internal static Color Disabled => Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(160, 160, 160);
    internal static Color Hover => Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
    internal static Color Line => Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);
    internal static Color Accent => Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
    internal static Color AccentBack => Dark ? Color.FromArgb(40, 62, 76) : Color.FromArgb(221, 235, 247);
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
        menu.Font = MenuStyle.Font;
        menu.ForeColor = MenuStyle.Text;
        if (menu is ToolStripDropDownMenu withMargin) withMargin.ShowCheckMargin = false;
        menu.Opening += (_, _) => Prepare(menu);
        menu.Opened += (_, _) => Round(menu);
        Prepare(menu);
    }

    private static void Prepare(ToolStripDropDown menu)
    {
        int gap = MenuParts.Scale(4);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem entry)
            {
                if (entry.Padding.Top != gap) entry.Padding = new Padding(0, gap, 0, gap);
                if (entry.HasDropDownItems) Apply(entry.DropDown);
            }
        }
    }

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
        if (e.Item is ToolStripLabel) e.TextColor = MenuStyle.Muted;
        else if (e.Item.Name == TitleName)
        {
            // Drawn here: a line that can't be chosen would otherwise be greyed like an unavailable command.
            TextRenderer.DrawText(e.Graphics, e.Text, MenuStyle.TitleFont, e.TextRectangle, MenuStyle.Text, e.TextFormat);
            return;
        }
        else if (!e.Item.Enabled) e.TextColor = MenuStyle.Disabled;
        else if (e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } keys } && e.Text == keys) e.TextColor = MenuStyle.Muted;
        else if (e.Item is ToolStripMenuItem { Checked: true }) e.TextColor = MenuStyle.Accent;
        else e.TextColor = MenuStyle.Text;
        base.OnRenderItemText(e);
    }

    /// <summary>What is on gets a tinted square, with its icon in it or a tick when it has none.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle box = Rectangle.Inflate(e.ImageRectangle, MenuParts.Scale(3), MenuParts.Scale(3));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Rounded(box, MenuParts.Scale(4)))
        using (var back = new SolidBrush(MenuStyle.AccentBack))
            e.Graphics.FillPath(back, path);
        if (e.Item.Image is not null) return; // The icon is drawn over the square next.
        using var pen = new Pen(e.Item.Enabled ? MenuStyle.Accent : MenuStyle.Disabled, MenuParts.Scale(2) * 0.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        Rectangle r = e.ImageRectangle;
        e.Graphics.DrawLines(pen,
        [
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
        ]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(MenuStyle.Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, MenuParts.Scale(8), y, e.Item.Width - MenuParts.Scale(8), y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Enabled: false } ? MenuStyle.Disabled : MenuStyle.Muted;
        base.OnRenderArrow(e);
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
