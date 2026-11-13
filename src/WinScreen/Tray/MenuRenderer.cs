using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>The colours WinScreen's menus are drawn with, in the light and the dark theme.</summary>
internal static class MenuStyle
{
    internal static bool Dark => Application.IsDarkModeEnabled;

    /// <summary>Windows' high-contrast theme is on: WinScreen draws with its colours, not its own (roadmap P2-4).</summary>
    internal static bool HighContrast => _contrastHere || SystemInformation.HighContrast;

    [ThreadStatic] private static bool _contrastHere;

    /// <summary>For tests: draws as in a high-contrast theme on this thread until disposed, and nowhere else.</summary>
    internal static IDisposable AsIfHighContrast()
    {
        _contrastHere = true;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _contrastHere = false;
    }

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
    internal static readonly string Face =
        FontFamily.Families.Any(family => family.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    internal static readonly Font Font = new(Face, 9.75f);
    internal static readonly Font SmallFont = new(Face, 8.25f);
    internal static readonly Font TitleFont = new(Face, 9.75f, FontStyle.Bold);
    // Menus use Windows 11's own menu sizes: words at 14 px (10.5 pt at 100%), group headings at 12 px, the title in semibold.
    internal static readonly Font MenuFont = new(Face, 10.5f);
    internal static readonly Font MenuHeaderFont = new(Face, 9f);
    internal static readonly Font MenuTitleFont = new(
        FontFamily.Families.FirstOrDefault(family => family.Name.StartsWith("Segoe UI Variable Text Semib", StringComparison.Ordinal))?.Name ?? "Segoe UI Semibold", 10.5f);
    internal static Color Back => HighContrast ? SystemColors.Menu : Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
    internal static Color Text => HighContrast ? SystemColors.MenuText : Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27);
    internal static Color Muted => HighContrast ? SystemColors.MenuText : Dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);
    internal static Color Disabled => HighContrast ? SystemColors.GrayText : Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(160, 160, 160);
    internal static Color Hover => HighContrast ? SystemColors.Menu : Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);

    /// <summary>Whether menus show a blurred glimpse of what is behind them, as Windows' Transparency effects asks.</summary>
    internal static bool Translucent => !HighContrast && Native.Translucency.On;
    /// <summary>What a menu's background is painted: see-through over the blur when translucent, its colour otherwise.</summary>
    internal static Color Ground => Translucent ? Color.Transparent : Back;
    /// <summary>The highlight under the pointer: a soft wash over the blur, as Windows 11's menus have.</summary>
    internal static Color HoverWash => !Translucent ? Hover : Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
    /// <summary>The tint over the blur, lighter than a solid menu so what is behind shows through a little.</summary>
    internal static Color Tint => Dark ? Color.FromArgb(0xD8, 40, 40, 40) : Color.FromArgb(0xE0, 245, 245, 245);
    internal static Color Line => HighContrast ? SystemColors.MenuText : Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);
    internal static Color Accent => HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
    internal static Color AccentBack => HighContrast ? SystemColors.Menu : Dark ? Color.FromArgb(40, 62, 76) : Color.FromArgb(221, 235, 247);
    /// <summary>The tag on what is experimental, in menus and in Settings: Windows 11's caution colours.</summary>
    internal static Color TagBack => HighContrast ? SystemColors.Menu : Dark ? Color.FromArgb(67, 53, 25) : Color.FromArgb(255, 244, 206);
    internal static Color TagText => HighContrast ? SystemColors.MenuText : Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(121, 82, 0);
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
        SubmenuEdge.Watch(menu);
        Prepare(menu);
        menu.HandleCreated += (_, _) => { if (Blurred.TryGetValue(menu, out StrongBox<Color?>? applied)) applied.Value = null; };
    }

    /// <summary>
    /// Places a submenu against the edge of the menu it opens from. Windows leaves a few pixels between them, and
    /// where a menu lay under that gap, the pointer crossing it chose that menu's row instead, which closed the
    /// submenus on the way. It is moved before it shows, so nothing jumps.
    /// </summary>
    private sealed class SubmenuEdge : NativeWindow
    {
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOMOVE = 0x2;
        private readonly ToolStripDropDown _menu;

        private SubmenuEdge(ToolStripDropDown menu) => _menu = menu;

        internal static void Watch(ToolStripDropDown menu)
        {
            var edge = new SubmenuEdge(menu);
            // Made now: a menu made as it first opens is put in its place as it is made, before this could move it.
            edge.AssignHandle(menu.Handle);
            menu.HandleCreated += (_, _) => edge.AssignHandle(menu.Handle);
            menu.HandleDestroyed += (_, _) => edge.ReleaseHandle();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WindowPos
        {
            public nint Window, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGING && m.LParam != 0) Meet(m.LParam);
            base.WndProc(ref m);
        }

        private void Meet(nint place)
        {
            if (_menu.OwnerItem?.Owner is not ToolStripDropDown { Visible: true, IsHandleCreated: true } parent) return;
            var position = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPos>(place);
            if ((position.Flags & SWP_NOMOVE) != 0 || position.Width <= 0) return;
            if (!User32.GetWindowRect(parent.Handle, out Rect from)) return;
            int most = MenuParts.Scale(8), x = position.X;
            if (x > from.Right && x - from.Right <= most) x = from.Right; // Opened to the right.
            else if (x + position.Width < from.Left && from.Left - (x + position.Width) <= most) x = from.Left - position.Width; // To the left.
            if (x == position.X) return;
            position.X = x;
            System.Runtime.InteropServices.Marshal.StructureToPtr(position, place, false);
        }
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

    private static readonly ConditionalWeakTable<ToolStripDropDown, StrongBox<Color?>> Blurred = [];

    private static void Round(ToolStripDropDown menu)
    {
        if (!menu.IsHandleCreated) return;
        int round = DWMWCP_ROUND;
        Dwmapi.DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, in round, sizeof(int)); // Windows 11; refused before it.
        // The blur behind follows Transparency effects and the light or dark look, which may have changed since.
        Color? tint = MenuStyle.Translucent ? MenuStyle.Tint : null;
        StrongBox<Color?> applied = Blurred.GetValue(menu, _ => new StrongBox<Color?>(null));
        if (applied.Value == tint) return;
        applied.Value = tint;
        Native.Translucency.Apply(menu.Handle, tint);
        menu.Invalidate(true);
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
        // See-through over the blur Windows draws behind a translucent menu.
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        using var brush = new SolidBrush(MenuStyle.Ground);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
        e.Graphics.CompositingMode = CompositingMode.SourceOver;
    }

    /// <summary>
    /// A row's words, drawn with DirectWrite: smoother, and see-through where they should be over a translucent menu,
    /// which Windows' own text drawing isn't. Words set from the left may run a little past the room Windows measured.
    /// </summary>
    private static void DrawWords(ToolStripItemTextRenderEventArgs e)
    {
        Rectangle area = e.TextRectangle;
        if ((e.TextFormat & (TextFormatFlags.Right | TextFormatFlags.HorizontalCenter)) == 0 && e.Item is not null)
            area.Width = Math.Max(area.Width, e.Item.Width - area.Left);
        Native.DirectText.DrawText(e.Graphics, e.Text, e.TextFont ?? MenuStyle.MenuFont, area, e.TextColor, e.TextFormat);
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
        using var brush = new SolidBrush(MenuStyle.HoverWash);
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
        if (e.Item is NoteRow note)
        {
            e.TextColor = MenuStyle.Muted;
            DrawWords(e);
            if (note.How.Length == 0) return;
            var how = new Rectangle(note.HowLeft, 0, Math.Max(0, e.Item.Width - note.HowLeft), e.Item.Height);
            Native.DirectText.DrawText(e.Graphics, note.How, e.TextFont ?? MenuStyle.MenuFont, how, MenuStyle.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return;
        }
        if (e.Item is ToolStripLabel) e.TextColor = MenuStyle.Muted;
        else if (e.Item.Name == TitleName)
        {
            // Drawn here: a line that can't be chosen would otherwise be greyed like an unavailable command.
            Native.DirectText.DrawText(e.Graphics, e.Text, MenuStyle.MenuTitleFont, e.TextRectangle, MenuStyle.Text, e.TextFormat);
            return;
        }
        else if (!e.Item.Enabled) e.TextColor = MenuStyle.Disabled;
        else if (e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } keys } && e.Text == keys) e.TextColor = MenuStyle.Muted;
        else if (e.Item is ToolStripMenuItem { Checked: true }) e.TextColor = MenuStyle.Accent;
        else e.TextColor = MenuStyle.Text;
        DrawWords(e);
        if (shortcut || !MenuParts.IsExperimental(e.Item) || string.IsNullOrEmpty(e.Text)) return;
        int words = Native.DirectText.MeasureText(e.Text, e.TextFont ?? MenuStyle.MenuFont, e.TextRectangle.Size, e.TextFormat).Width;
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
        if (MenuParts.IsOneOf(e.Item))
        {
            // One of several: a dot, as Windows marks the choice made among others.
            float size = MenuParts.Scale(6);
            using var dot = new SolidBrush(e.Item.Enabled ? MenuStyle.Accent : MenuStyle.Disabled);
            e.Graphics.FillEllipse(dot, r.Left + (r.Width - size) / 2f, r.Top + (r.Height - size) / 2f, size, size);
            return;
        }
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
