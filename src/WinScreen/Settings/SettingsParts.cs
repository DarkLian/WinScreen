using System.Drawing.Drawing2D;
using WinScreen.Tray;

namespace WinScreen.Settings;

/// <summary>
/// The Settings window's colours and type, modelled on Windows 11's own Settings. Every part asks for a colour by
/// what it is for (a card, a control on a card, text that explains) and gets the light or dark value from here.
/// </summary>
internal static class SettingsStyle
{
    private static bool Dark => MenuStyle.Dark;
    private static Color Rgb(int value) => Color.FromArgb(value >> 16 & 0xFF, value >> 8 & 0xFF, value & 0xFF);

    /// <summary><paramref name="top"/> laid over <paramref name="under"/> at <paramref name="amount"/> strength.</summary>
    internal static Color Blend(Color top, Color under, double amount) => Color.FromArgb(
        (int)Math.Round(under.R + (top.R - under.R) * amount),
        (int)Math.Round(under.G + (top.G - under.G) * amount),
        (int)Math.Round(under.B + (top.B - under.B) * amount));

    // Surfaces.
    // In a high-contrast theme every surface, text and mark takes Windows' own colours for it (roadmap P2-4).
    private static bool Contrast => Tray.MenuStyle.HighContrast;
    internal static Color Page => Contrast ? SystemColors.Window : Dark ? Rgb(0x202020) : Rgb(0xF3F3F3);
    internal static Color Side => Page;
    internal static Color Card => Contrast ? SystemColors.Window : Dark ? Rgb(0x2B2B2B) : Rgb(0xFBFBFB);
    internal static Color CardStroke => Contrast ? SystemColors.WindowText : Dark ? Rgb(0x1D1D1D) : Rgb(0xE5E5E5);
    /// <summary>The line between options on a card: a shade lighter than the card, so options read as one group.</summary>
    internal static Color Divider => Contrast ? SystemColors.GrayText : Dark ? Blend(Color.White, Card, 0.055) : Rgb(0xEBEBEB);
    /// <summary>The background of a list that drops down, the same as the menus'.</summary>
    internal static Color Flyout => MenuStyle.Back;

    // Text.
    // In the dark theme, a little short of pure white: full white on a dark grey glares at reading size.
    internal static Color Text => Contrast ? SystemColors.WindowText : Dark ? Rgb(0xEDEDED) : Rgb(0x1B1B1B);
    internal static Color Secondary => Contrast ? SystemColors.WindowText : Dark ? Rgb(0xC5C5C5) : Rgb(0x5F5F5F);
    internal static Color Tertiary => Contrast ? SystemColors.WindowText : Dark ? Rgb(0x9E9E9E) : Rgb(0x6E6E6E);
    internal static Color Disabled => Contrast ? SystemColors.GrayText : Dark ? Rgb(0x8A8A8A) : Rgb(0x9A9A9A);
    internal static Color Error => Dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C);

    // Controls: each sits on a surface and is drawn a little lighter (or, in the light theme, whiter) than it.
    internal static Color FillOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.0605) : Blend(Color.White, surface, 0.7);
    internal static Color HoverOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.0837) : Blend(Rgb(0xF9F9F9), surface, 0.5);
    internal static Color PressedOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.0326) : Blend(Rgb(0xF9F9F9), surface, 0.3);
    internal static Color DisabledOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.042) : Blend(Rgb(0xF9F9F9), surface, 0.3);
    /// <summary>A text box while it is being typed in.</summary>
    internal static Color ActiveOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Rgb(0x1E1E1E), surface, 0.7) : Color.White;
    internal static Color StrokeOn(Color fill) => Contrast ? SystemColors.WindowText : Dark ? Blend(Color.White, fill, 0.07) : Blend(Color.Black, fill, 0.1);
    /// <summary>The line under a text box that isn't being typed in.</summary>
    internal static Color Underline => Contrast ? SystemColors.WindowText : Dark ? Rgb(0x9A9A9A) : Rgb(0x868686);
    /// <summary>A row under the pointer, in a list or in the list of pages.</summary>
    internal static Color RowHoverOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.0419) : Blend(Color.Black, surface, 0.0373);
    /// <summary>The chosen row of a list: a soft grey wash, as Windows 11 has, with a bar of the accent colour at the start.</summary>
    internal static Color RowSelectedOn(Color surface) => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, surface, 0.0605) : Blend(Color.Black, surface, 0.0373);
    internal static Color NavSelected => Contrast ? SystemColors.Window : Dark ? Blend(Color.White, Page, 0.0605) : Blend(Color.Black, Page, 0.0373);

    // Accent: the one chosen in Windows, as Windows' own Settings shows it.
    private static readonly Lazy<(Color Dark, Color Light)?> SystemAccent = new(ReadAccent);
    internal static Color Accent => Contrast ? SystemColors.Highlight : Dark ? SystemAccent.Value?.Dark ?? Rgb(0x60CDFF) : SystemAccent.Value?.Light ?? Rgb(0x005FB8);
    internal static Color AccentHover => Contrast ? SystemColors.Highlight : Blend(Accent, Page, 0.9);
    internal static Color OnAccent => Contrast ? SystemColors.HighlightText : Dark ? Color.Black : Color.White;
    internal static Color AccentBack => Contrast ? SystemColors.Window : Blend(Accent, Page, Dark ? 0.18 : 0.14);

    private static (Color Dark, Color Light)? ReadAccent()
    {
        try
        {
            var colors = new global::Windows.UI.ViewManagement.UISettings();
            static Color Of(global::Windows.UI.Color c) => Color.FromArgb(c.R, c.G, c.B);
            return (Of(colors.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight2)),
                Of(colors.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentDark1)));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or TypeLoadException
                                       or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return null; // The fixed blue then.
        }
    }

    // Type. Windows 11's own faces where they exist (some names are cut short by Windows), Segoe UI otherwise.
    private static string Face(string prefix, string fallback) =>
        FontFamily.Families.FirstOrDefault(family => family.Name.StartsWith(prefix, StringComparison.Ordinal))?.Name ?? fallback;
    /// <summary>
    /// Smooths text in grey on a dark background, where Windows' colour smoothing makes small light text look thin and
    /// sharp, with coloured edges. Dark text on a light background keeps the colour smoothing: in grey it came out pale
    /// and washed out next to Windows 11's own Settings. Text drawn on <paramref name="g"/> afterwards follows it.
    /// </summary>
    internal static void SoftText(Graphics g) => g.TextRenderingHint = Dark
        ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

    // Windows 11's type sizes, measured against its own Settings: body 14 pixels, captions 12, section headings 14 in
    // semibold and page titles 28. Settings and the window for a new view use them; menus keep their own.
    internal static readonly Font BodyFont = new(MenuStyle.Face, 10.5f);
    internal static readonly Font CaptionFont = new(MenuStyle.Face, 9f);
    internal static readonly Font HeadingFont = new(Face("Segoe UI Variable Text Semib", "Segoe UI Semibold"), 10.5f);
    internal static readonly Font PageTitleFont = new(Face("Segoe UI Variable Display Semib", "Segoe UI Semibold"), 21f);

    internal static GraphicsPath Rounded(Rectangle area, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        path.AddArc(area.Left, area.Top, d, d, 180, 90);
        path.AddArc(area.Right - d, area.Top, d, d, 270, 90);
        path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
        path.AddArc(area.Left, area.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static int Unit(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96.0);

    /// <summary>
    /// Whether to ring <paramref name="control"/> as where the keyboard is. As in Windows 11, the ring shows only
    /// while the keyboard is being used to move around, not after a click.
    /// </summary>
    internal static bool ShowsFocus(Control control) => control.Focused && FocusVisuals.Keyboard;

    /// <summary>The ring that shows where the keyboard is, as Windows 11 draws it.</summary>
    internal static void FocusRing(Graphics g, Rectangle area, int radius, Control control)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Rounded(Rectangle.Inflate(area, -1, -1), radius);
        using var pen = new Pen(Text, Unit(control, 2));
        g.DrawPath(pen, path);
    }

    /// <summary>The short bar of accent colour at the start of a chosen row.</summary>
    internal static void SelectionBar(Graphics g, Rectangle row, Control control)
    {
        int height = Math.Min(Unit(control, 16), row.Height - Unit(control, 8));
        var bar = new Rectangle(row.Left, row.Top + (row.Height - height) / 2, Unit(control, 3), height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Rounded(bar, Math.Max(1, bar.Width / 2));
        using var brush = new SolidBrush(Accent);
        g.FillPath(brush, path);
    }

    /// <summary>A small downward chevron, for a list that drops down.</summary>
    internal static void Chevron(Graphics g, Point centre, Color color, Control control, bool up = false)
    {
        float w = Unit(control, 4), h = Unit(control, 2) * (up ? -1 : 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, Math.Max(1f, control.DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new PointF(centre.X - w, centre.Y - h), new PointF(centre.X, centre.Y + h), new PointF(centre.X + w, centre.Y - h)]);
    }
}

/// <summary>A setting that can say more about itself under its name, which a search also looks through.</summary>
internal interface IDescribed
{
    string Description { get; }
}

/// <summary>
/// Where a setting's icon, name and description go in its card, as Windows 11 lays them out, and how they are drawn.
/// </summary>
internal static class SettingText
{
    /// <summary>Where the icon starts, at 100% scaling, from the row's left edge.</summary>
    internal const int IconLeft = 6;
    /// <summary>Where the words start with an icon before them, and without one, at 100% scaling.</summary>
    internal const int WithIcon = 47, WithoutIcon = 4;
    private const TextFormatFlags Words = TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    internal static int Left(Control row, char glyph) => SettingsStyle.Unit(row, glyph != '\0' ? WithIcon : WithoutIcon);

    /// <summary>How tall the name and the description are together, at this width.</summary>
    internal static int Height(Control row, string title, string description, int width)
    {
        int height = Native.DirectText.MeasureText(title, row.Font, new Size(width, 0), Words).Height;
        if (description.Length > 0)
            height += SettingsStyle.Unit(row, 2) + Native.DirectText.MeasureText(description, SettingsStyle.CaptionFont, new Size(width, 0), Words).Height;
        return height;
    }

    /// <summary>Draws the icon, the name and, under it in grey, the description, all centred on the row's height.</summary>
    internal static void Draw(Graphics g, Control row, char glyph, string title, string description, int width, bool enabled)
    {
        int left = Left(row, glyph);
        int height = Height(row, title, description, width);
        int top = (row.Height - height) / 2;
        if (glyph != '\0' && MenuParts.Icon(glyph, enabled ? SettingsStyle.Text : SettingsStyle.Disabled) is { } icon)
            g.DrawImage(icon, SettingsStyle.Unit(row, IconLeft) + (SettingsStyle.Unit(row, 20) - icon.Width) / 2, (row.Height - icon.Height) / 2);
        int titleHeight = Native.DirectText.MeasureText(title, row.Font, new Size(width, 0), Words).Height;
        Native.DirectText.DrawText(g, title, row.Font, new Rectangle(left, top, width, titleHeight), enabled ? SettingsStyle.Text : SettingsStyle.Disabled, Words);
        if (description.Length == 0) return;
        int below = top + titleHeight + SettingsStyle.Unit(row, 2);
        Native.DirectText.DrawText(g, description, SettingsStyle.CaptionFont, new Rectangle(left, below, width, row.Height - below),
            enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, Words);
    }
}

/// <summary>An on/off option drawn as a row: its icon, name and description on the left, a switch on the right. Still
/// a check box to everything that reads or sets it.</summary>
internal sealed class SwitchBox : CheckBox, IDescribed
{
    /// <summary>The width a row takes inside a card, at 100% scaling.</summary>
    internal const int RowWidth = 524;
    private bool _hot;

    /// <summary>Only the switch, with no words beside it: for a cell in a table whose row says what it is.</summary>
    [System.ComponentModel.DefaultValue(false)]
    public bool Compact { get; set; }

    public SwitchBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);
    private int TextWidth => Unit(RowWidth) + Extra - Unit(64) - (SettingText.Left(this, Glyph) - Unit(SettingText.WithoutIcon));

    /// <summary>The icon before its name, or none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal char Glyph
    {
        get;
        set { field = value; Fit(); }
    }

    /// <summary>More about the option, in grey under its name.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get;
        set { field = value ?? ""; Fit(); }
    } = "";

    private void Fit()
    {
        if (AutoSize && !Compact) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    /// <summary>How much wider than usual the row is, in pixels, for a Settings window made wider.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int Extra
    {
        get;
        set
        {
            if (field == value || Compact) return;
            field = value;
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Compact) return new Size(Unit(50), Unit(30));
        int text = SettingText.Height(this, Text, Description, TextWidth);
        return new Size(Unit(RowWidth) + Extra, Math.Max(Unit(36), text + Unit(Description.Length > 0 ? 18 : 14)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        Color surface = Parent?.BackColor ?? SettingsStyle.Card;
        g.Clear(surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Compact) SettingText.Draw(g, this, Glyph, Text, Description, TextWidth, Enabled);
        int width = Unit(40), height = Unit(20);
        var track = new Rectangle(Width - width - Unit(6), (Height - height) / 2, width, height);
        using (GraphicsPath path = SettingsStyle.Rounded(track, height / 2))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(!Enabled ? SettingsStyle.Disabled : _hot ? SettingsStyle.AccentHover : SettingsStyle.Accent);
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(Enabled && _hot ? SettingsStyle.HoverOn(surface) : surface);
                using var edge = new Pen(Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, Math.Max(1f, DeviceDpi / 96f));
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
        }
        int knob = Unit(_hot && Enabled ? 14 : 12);
        int x = Checked ? track.Right - Unit(10) - knob / 2 : track.Left + Unit(10) - knob / 2;
        using var dot = new SolidBrush(Checked ? (Enabled ? SettingsStyle.OnAccent : surface) : Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled);
        g.FillEllipse(dot, x, track.Top + (height - knob) / 2f, knob, knob);
        if (SettingsStyle.ShowsFocus(this))
            SettingsStyle.FocusRing(g, Rectangle.Inflate(track, Unit(3), Unit(3)), Unit(13), this);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>
/// A titled group of settings: the title above, each setting in a card of its own under it, as Windows 11 has them.
/// A note belongs to the setting above it and shares its card.
/// </summary>
internal sealed class SettingsCard : Panel
{
    private readonly Label _title = new SoftLabel() { AutoSize = true, Margin = Padding.Empty };
    private readonly Body _body = new();
    private readonly bool _experimental;

    internal SettingsCard(string title, bool experimental, int width, IEnumerable<Control> items)
    {
        _experimental = experimental;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 18);
        _title.Text = title;
        _title.Font = SettingsStyle.HeadingFont;
        _title.ForeColor = SettingsStyle.Text;
        _title.Location = new Point(1, 0);
        _title.Padding = new Padding(0, 0, 0, 6);
        _body.MinimumSize = new Size(width, 0);
        foreach (Control item in items) _body.Controls.Add(item);
        Controls.Add(_title);
        Controls.Add(_body);
        Paint += (_, e) => { SettingsStyle.SoftText(e.Graphics); DrawBadge(e.Graphics); };
    }

    /// <summary>The card's title, for anything looking a section up by name.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public override string Text { get => _title.Text; set => _title.Text = value ?? ""; }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        _body.Location = new Point(0, _title.Bottom);
        base.OnLayout(levent);
    }

    private void DrawBadge(Graphics g)
    {
        if (!_experimental) return;
        // Level with the title's words, which sit above the room left under them.
        MenuParts.DrawTag(g, _title.Right + SettingsStyle.Unit(this, 8), _title.Top + (_title.Height - _title.Padding.Bottom) / 2,
            value => SettingsStyle.Unit(this, value));
    }

    /// <summary>
    /// The settings, each in a card of its own with a small gap between them. The parts keep the card's colour as
    /// their own, and the room around and between them is painted here.
    /// </summary>
    private sealed class Body : FlowLayoutPanel
    {
        /// <summary>The room inside a card above and below what it holds, and the gap between cards, at 100% scaling.</summary>
        private const int Inside = 8, Gap = 4;
        private bool _regrouping;

        internal Body()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(8, Inside, 8, Inside);
            BackColor = SettingsStyle.Card;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private int Unit(int value) => SettingsStyle.Unit(this, value);

        /// <summary>The settings in order, each with the notes and parts that share its card.</summary>
        private List<List<Control>> Groups()
        {
            var groups = new List<List<Control>>();
            foreach (Control child in Controls.Cast<Control>().Where(child => child.Visible))
            {
                if (groups.Count == 0 || !SettingGroups.SharesCardAbove(child)) groups.Add([]);
                groups[^1].Add(child);
            }
            return groups;
        }

        /// <summary>Spaces the cards apart: room inside each card above and below, and a gap between them.</summary>
        private void Regroup()
        {
            if (_regrouping) return;
            _regrouping = true;
            try
            {
                List<List<Control>> groups = Groups();
                for (int index = 0; index < groups.Count; index++)
                    for (int at = 0; at < groups[index].Count; at++)
                    {
                        Control child = groups[index][at];
                        var margin = child.Margin;
                        if (at == 0) margin.Top = index == 0 ? 0 : Unit(Inside * 2 + Gap);
                        if (at == groups[index].Count - 1) margin.Bottom = 0;
                        if (child.Margin != margin) child.Margin = margin;
                    }
            }
            finally { _regrouping = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SettingsStyle.Page);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(SettingsStyle.Card);
            using var edge = new Pen(SettingsStyle.CardStroke);
            foreach (List<Control> group in Groups())
            {
                var area = Rectangle.FromLTRB(0, group[0].Top - Unit(Inside), Width - 1, group[^1].Bottom + Unit(Inside) - 1);
                using GraphicsPath path = SettingsStyle.Rounded(area, Unit(6));
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            Regroup();
            e.Control!.LocationChanged += (_, _) => Invalidate();
            e.Control.VisibleChanged += (_, _) => { Regroup(); Invalidate(); };
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            Regroup();
            base.OnLayout(levent);
        }
    }
}

/// <summary>A label whose words are smoothed like the rest of Settings (<see cref="SettingsStyle.SoftText"/>).</summary>
internal class SoftLabel : Label
{
    /// <summary>How the label's words are set, from its own settings, as Windows' label would set them.</summary>
    private TextFormatFlags Flags
    {
        get
        {
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
            int align = (int)TextAlign;
            if ((align & 0x0F0) != 0) flags |= TextFormatFlags.VerticalCenter; // Middle.
            else if ((align & 0xF00) != 0) flags |= TextFormatFlags.Bottom;
            if ((align & 0x222) != 0) flags |= TextFormatFlags.HorizontalCenter;
            else if ((align & 0x444) != 0) flags |= TextFormatFlags.Right;
            return flags;
        }
    }

    /// <summary>Its size measured the way its words are drawn, so they fit exactly.</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!Native.DirectText.Available || !AutoSize) return base.GetPreferredSize(proposedSize);
        int most = MaximumSize.Width > 0 ? MaximumSize.Width - Padding.Horizontal : 0;
        Size text = Native.DirectText.MeasureText(Text, Font, new Size(Math.Max(0, most), 0), most > 0 ? Flags : Flags & ~TextFormatFlags.WordBreak);
        var size = new Size(text.Width + Padding.Horizontal, text.Height + Padding.Vertical);
        if (MaximumSize.Width > 0) size.Width = Math.Min(size.Width, MaximumSize.Width);
        return new Size(Math.Max(size.Width, MinimumSize.Width), Math.Max(size.Height, MinimumSize.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Native.DirectText.Available) { SettingsStyle.SoftText(e.Graphics); base.OnPaint(e); return; }
        var area = Rectangle.FromLTRB(Padding.Left, Padding.Top, Width - Padding.Right, Height - Padding.Bottom);
        Native.DirectText.DrawText(e.Graphics, Text, Font, area, Enabled ? ForeColor : SettingsStyle.Disabled, Flags);
    }
}

/// <summary>A plain card: a rounded panel in the card colour, for content that isn't a list of options.</summary>
internal class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = SettingsStyle.Card;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, this, drawRows: false);

    /// <summary>Draws a card over the whole of <paramref name="card"/>; with <paramref name="drawRows"/>, a line between
    /// options too. A note belongs to the option above it, so no line comes between them.</summary>
    internal static void Draw(Graphics g, Control card, bool drawRows)
    {
        g.Clear(card.Parent?.BackColor ?? SettingsStyle.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
        using (GraphicsPath path = SettingsStyle.Rounded(area, SettingsStyle.Unit(card, 6)))
        using (var fill = new SolidBrush(SettingsStyle.Card))
        using (var edge = new Pen(SettingsStyle.CardStroke))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }
        if (!drawRows) return;
        g.SmoothingMode = SmoothingMode.None;
        using var line = new Pen(SettingsStyle.Divider);
        Control? previous = null;
        foreach (Control child in card.Controls.Cast<Control>().Where(child => child.Visible).OrderBy(child => child.Top))
        {
            if (previous is not null && child is not Label)
                g.DrawLine(line, 1, (previous.Bottom + child.Top) / 2, card.Width - 2, (previous.Bottom + child.Top) / 2);
            previous = child;
        }
    }
}

/// <summary>Which parts of a group of settings share the card of the setting above them.</summary>
internal static class SettingGroups
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Joined = new();

    /// <summary>Puts <paramref name="part"/> in the same card as what comes before it, such as a button under a table.</summary>
    internal static T JoinAbove<T>(T part) where T : Control
    {
        Joined.AddOrUpdate(part, true);
        return part;
    }

    /// <summary>A note shares the card of the setting above it, and so does a part joined to it.</summary>
    internal static bool SharesCardAbove(Control part) => part is NoteLabel || Joined.TryGetValue(part, out _);
}

/// <summary>A line of explanation in grey: under the setting it belongs to, in the same card.</summary>
internal sealed class NoteLabel : SoftLabel;

/// <summary>
/// One setting chosen with a box, a list or a number: its icon, name and description on the left, what sets it on the
/// right, centred on the row.
/// </summary>
internal sealed class SettingRowPanel : Panel, IDescribed
{
    private readonly Control _control;

    /// <summary>What sets it: a box, a list or a number.</summary>
    internal Control Setting => _control;

    internal SettingRowPanel(string text, Control control)
    {
        _control = control;
        Text = text;
        // A screen reader reads the row's name for the box, list or slider that sets it (roadmap P2-4).
        // A framed box is inside its frame, so whatever in the row takes the keyboard gets the name.
        string name = text.TrimEnd(':', '：', ' ');
        void Name(Control part)
        {
            if (string.IsNullOrEmpty(part.AccessibleName)) part.AccessibleName = name;
            foreach (Control inner in part.Controls) Name(inner);
        }
        Name(control);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(SwitchBox.RowWidth, Math.Max(42, control.Height + 10));
        Margin = new Padding(3, 0, 3, 0);
        control.Margin = Padding.Empty;
        control.EnabledChanged += (_, _) => Invalidate();
        Controls.Add(control);
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>The icon before its name, or none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal char Glyph
    {
        get;
        set { field = value; PerformLayout(); Invalidate(); }
    }

    /// <summary>More about the setting, in grey under its name.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get;
        set { field = value ?? ""; PerformLayout(); Invalidate(); }
    } = "";

    private int TextWidth => Math.Max(Unit(60), Width - _control.Width - Unit(6 + 16) - SettingText.Left(this, Glyph));

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_control is null) return; // Still being built.
        if (_control is ComboBox choices) FitChoices(choices);
        int height = Math.Max(Math.Max(Unit(42), _control.Height + Unit(10)),
            SettingText.Height(this, Text, Description, TextWidth) + Unit(Description.Length > 0 ? 18 : 14));
        if (Height != height) Height = height;
        _control.Location = new Point(Width - _control.Width - Unit(6), (Height - _control.Height) / 2);
    }

    /// <summary>
    /// A list of choices as wide as its longest one, with room for the arrow, as Windows 11's are; never narrower than
    /// Windows 11's narrowest. Measured each time, so another language or display scaling fits too.
    /// </summary>
    private void FitChoices(ComboBox box)
    {
        int widest = 0;
        foreach (object? item in box.Items)
            widest = Math.Max(widest, Native.DirectText.MeasureText(box.GetItemText(item), box.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
        int most = Math.Max(Unit(120), Width - Unit(6 + 16 + 160) - SettingText.Left(this, Glyph));
        int width = Math.Clamp(widest + Unit(11 + 44), Unit(120), most);
        if (box.Width != width) box.Width = width;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); PerformLayout(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        e.Graphics.Clear(Parent?.BackColor ?? SettingsStyle.Card);
        SettingText.Draw(e.Graphics, this, Glyph, Text, Description, TextWidth, Enabled && _control.Enabled);
    }
}

/// <summary>The list of pages down the left of the Settings window.</summary>
internal sealed class NavList : Control
{
    internal sealed record Entry(string Text, char Glyph, object? Page);
    private readonly List<Entry> _entries = [];
    private HashSet<object>? _shown;
    private int _hot = -1;
    private object? _selected;
    internal event Action<object>? Chosen;

    public NavList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
    }

    /// <summary>A screen reader hears the list's name and the page that is open, and hears it change.</summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new Spoken(this);

    private sealed class Spoken(NavList owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;
        public override string? Value =>
            owner._entries.FirstOrDefault(entry => entry.Page is not null && ReferenceEquals(entry.Page, owner._selected))?.Text;
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>Adds a group heading.</summary>
    internal void AddHeading(string text) => _entries.Add(new Entry(text, '\0', null));
    internal void Add(string text, char glyph, object page) => _entries.Add(new Entry(text, glyph, page));

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal object? Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); }
    }

    /// <summary>Shows only these pages, or all of them again with null. A heading with none of its pages left goes too.</summary>
    internal void Filter(IEnumerable<object>? pages)
    {
        _shown = pages is null ? null : [.. pages];
        Invalidate();
    }

    private IEnumerable<(Entry Entry, Rectangle Bounds)> Rows()
    {
        int y = Unit(4);
        for (int index = 0; index < _entries.Count; index++)
        {
            Entry entry = _entries[index];
            bool heading = entry.Page is null;
            if (heading)
            {
                bool any = _entries.Skip(index + 1).TakeWhile(next => next.Page is not null).Any(next => _shown?.Contains(next.Page!) != false);
                if (!any) continue;
            }
            else if (_shown?.Contains(entry.Page!) == false) continue;
            int height = Unit(heading ? 32 : 40); // As far apart as in Windows 11's Settings, each highlight 36 tall.
            yield return (entry, new Rectangle(Unit(2), y, Width - Unit(4), height));
            y += height;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int index = 0;
        foreach (var (entry, bounds) in Rows())
        {
            if (entry.Page is null)
            {
                Native.DirectText.DrawText(g, entry.Text, SettingsStyle.CaptionFont, new Rectangle(bounds.Left + Unit(12), bounds.Top + Unit(10), bounds.Width, bounds.Height - Unit(10)),
                    SettingsStyle.Tertiary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                bool selected = Equals(entry.Page, _selected);
                Rectangle row = Rectangle.Inflate(bounds, 0, -Unit(2));
                if (selected || index == _hot)
                {
                    using GraphicsPath path = SettingsStyle.Rounded(row, Unit(4));
                    using var back = new SolidBrush(selected ? SettingsStyle.NavSelected : SettingsStyle.RowHoverOn(BackColor));
                    g.FillPath(back, path);
                }
                if (selected) SettingsStyle.SelectionBar(g, row, this);
                if (MenuParts.Icon(entry.Glyph) is { } icon)
                    g.DrawImage(icon, bounds.Left + Unit(14), bounds.Top + (bounds.Height - icon.Height) / 2);
                Native.DirectText.DrawText(g, entry.Text, SettingsStyle.BodyFont, new Rectangle(bounds.Left + Unit(42), bounds.Top, bounds.Width - Unit(44), bounds.Height),
                    SettingsStyle.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (selected && SettingsStyle.ShowsFocus(this)) SettingsStyle.FocusRing(g, row, Unit(4), this);
            }
            index++;
        }
    }

    private int RowAt(Point point)
    {
        int index = 0;
        foreach (var (entry, bounds) in Rows())
        {
            if (entry.Page is not null && bounds.Contains(point)) return index;
            index++;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = RowAt(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int row = RowAt(e.Location);
        if (row >= 0) Choose(Rows().ElementAt(row).Entry.Page!);
    }

    private void Choose(object page)
    {
        _selected = page;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        Chosen?.Invoke(page);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
        object[] pages = Rows().Where(row => row.Entry.Page is not null).Select(row => row.Entry.Page!).ToArray();
        if (pages.Length == 0) return;
        int at = Array.IndexOf(pages, _selected);
        Choose(pages[Math.Clamp(at + (e.KeyCode == Keys.Down ? 1 : -1), 0, pages.Length - 1)]);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>A button drawn flat with rounded corners: filled in the accent colour for the main one, a quiet control otherwise.</summary>
internal sealed class PlainButton : Button
{
    private bool _hot, _pressed;
    [System.ComponentModel.DefaultValue(false)]
    public bool Primary { get; set; }

    /// <summary>An icon instead of words, for a small square button whose purpose its icon and tooltip say.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal char Glyph { get; set { field = value; Invalidate(); } }

    /// <summary>
    /// A button that takes something away: only its grey icon shows, and a light red wash and red icon come up under
    /// the pointer.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Removes { get; set { field = value; Invalidate(); } }

    public PlainButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // In the dark theme Windows draws a standard button itself and never asks this one to paint.
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>As big as its words need and no bigger: a standard button leaves far more room around them.</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Glyph != '\0') return new Size(Unit(32), Unit(32));
        Size text = Native.DirectText.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(Math.Max(text.Width + Unit(22), Unit(72)), Math.Max(text.Height + Unit(12), Unit(32)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        Color surface = Parent?.BackColor ?? SettingsStyle.Page;
        g.Clear(surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = SettingsStyle.Rounded(area, Unit(4));
        Color back = !Enabled ? SettingsStyle.DisabledOn(surface)
            : Primary ? (_hot ? SettingsStyle.AccentHover : SettingsStyle.Accent)
            : Removes ? (_pressed ? SettingsStyle.Blend(SettingsStyle.Error, surface, 0.22) : _hot ? SettingsStyle.Blend(SettingsStyle.Error, surface, 0.14) : surface)
            : _pressed ? SettingsStyle.PressedOn(surface) : _hot ? SettingsStyle.HoverOn(surface) : SettingsStyle.FillOn(surface);
        using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
        if ((!Primary && !Removes) || !Enabled) { using var edge = new Pen(SettingsStyle.StrokeOn(back)); g.DrawPath(edge, path); }
        Color text = !Enabled ? SettingsStyle.Disabled : Primary ? SettingsStyle.OnAccent
            : Removes ? (_hot ? SettingsStyle.Error : SettingsStyle.Secondary) : _pressed ? SettingsStyle.Secondary : SettingsStyle.Text;
        if (Glyph != '\0' && Tray.MenuParts.Icon(Glyph, text) is { } icon)
        {
            int size = Unit(16);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
        }
        else Native.DirectText.DrawText(g, Text, Font, area, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
        if (SettingsStyle.ShowsFocus(this)) SettingsStyle.FocusRing(g, area, Unit(4), this);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>The pages of the Settings window. Its row of tabs is never shown: the list on the left chooses the page.</summary>
internal sealed class PagesControl : TabControl
{
    private const int TCM_ADJUSTRECT = 0x1328;

    protected override void WndProc(ref Message m)
    {
        // Saying the pages take the whole control leaves no room for tabs or a border.
        if (m.Msg == TCM_ADJUSTRECT && !DesignMode) { m.Result = 1; return; }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Whether the keyboard or the pointer was used last in WinScreen's windows. Windows 11 shows where the keyboard is
/// only while it is used to move around: a click hides the ring again.
/// </summary>
internal static class FocusVisuals
{
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204,
        WM_MBUTTONDOWN = 0x0207, WM_NCLBUTTONDOWN = 0x00A1;
    private static bool _watching;

    internal static bool Keyboard { get; private set; }

    /// <summary>Starts following the keyboard and the pointer, once for the whole app.</summary>
    internal static void Watch()
    {
        if (_watching) return;
        _watching = true;
        Application.AddMessageFilter(new Filter());
    }

    private static void Set(bool keyboard)
    {
        if (Keyboard == keyboard) return;
        Keyboard = keyboard;
        // The control that has the keyboard draws its ring, or takes it away.
        if (Form.ActiveForm?.ActiveControl is { } focused) Deepest(focused).Invalidate();
    }

    private static Control Deepest(Control control) =>
        control is ContainerControl { ActiveControl: { } inner } ? Deepest(inner) : control;

    /// <summary>Takes in one message: a key that moves around shows the ring, a click hides it.</summary>
    internal static void Saw(int message, nint wParam)
    {
        switch (message)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                var key = (Keys)(int)wParam;
                if (key is Keys.Tab or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End
                    or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.Enter or Keys.F6) Set(true);
                break;
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_NCLBUTTONDOWN:
                Set(false);
                break;
        }
    }

    private sealed class Filter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            Saw(m.Msg, m.WParam);
            return false;
        }
    }
}
