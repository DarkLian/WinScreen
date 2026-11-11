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
    internal static Color Page => Dark ? Rgb(0x202020) : Rgb(0xF3F3F3);
    internal static Color Side => Page;
    internal static Color Card => Dark ? Rgb(0x2B2B2B) : Rgb(0xFBFBFB);
    internal static Color CardStroke => Dark ? Rgb(0x1D1D1D) : Rgb(0xE5E5E5);
    /// <summary>The line between options on a card: a shade lighter than the card, so options read as one group.</summary>
    internal static Color Divider => Dark ? Blend(Color.White, Card, 0.055) : Rgb(0xEBEBEB);
    /// <summary>The background of a list that drops down, the same as the menus'.</summary>
    internal static Color Flyout => MenuStyle.Back;

    // Text.
    // In the dark theme, a little short of pure white: full white on a dark grey glares at reading size.
    internal static Color Text => Dark ? Rgb(0xEDEDED) : Rgb(0x1B1B1B);
    internal static Color Secondary => Dark ? Rgb(0xC5C5C5) : Rgb(0x5F5F5F);
    internal static Color Tertiary => Dark ? Rgb(0x9E9E9E) : Rgb(0x6E6E6E);
    internal static Color Disabled => Dark ? Rgb(0x8A8A8A) : Rgb(0x9A9A9A);
    internal static Color Error => Dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C);

    // Controls: each sits on a surface and is drawn a little lighter (or, in the light theme, whiter) than it.
    internal static Color FillOn(Color surface) => Dark ? Blend(Color.White, surface, 0.0605) : Blend(Color.White, surface, 0.7);
    internal static Color HoverOn(Color surface) => Dark ? Blend(Color.White, surface, 0.0837) : Blend(Rgb(0xF9F9F9), surface, 0.5);
    internal static Color PressedOn(Color surface) => Dark ? Blend(Color.White, surface, 0.0326) : Blend(Rgb(0xF9F9F9), surface, 0.3);
    internal static Color DisabledOn(Color surface) => Dark ? Blend(Color.White, surface, 0.042) : Blend(Rgb(0xF9F9F9), surface, 0.3);
    /// <summary>A text box while it is being typed in.</summary>
    internal static Color ActiveOn(Color surface) => Dark ? Blend(Rgb(0x1E1E1E), surface, 0.7) : Color.White;
    internal static Color StrokeOn(Color fill) => Dark ? Blend(Color.White, fill, 0.07) : Blend(Color.Black, fill, 0.1);
    /// <summary>The line under a text box that isn't being typed in.</summary>
    internal static Color Underline => Dark ? Rgb(0x9A9A9A) : Rgb(0x868686);
    /// <summary>A row under the pointer, in a list or in the list of pages.</summary>
    internal static Color RowHoverOn(Color surface) => Dark ? Blend(Color.White, surface, 0.0419) : Blend(Color.Black, surface, 0.0373);
    /// <summary>The chosen row of a list: a light wash of the accent colour, with a bar of it at the start.</summary>
    internal static Color RowSelectedOn(Color surface) => Blend(Accent, surface, Dark ? 0.16 : 0.12);
    internal static Color NavSelected => Dark ? Blend(Color.White, Page, 0.0605) : Blend(Color.Black, Page, 0.0373);

    // Accent: the one chosen in Windows, as Windows' own Settings shows it.
    private static readonly Lazy<(Color Dark, Color Light)?> SystemAccent = new(ReadAccent);
    internal static Color Accent => Dark ? SystemAccent.Value?.Dark ?? Rgb(0x60CDFF) : SystemAccent.Value?.Light ?? Rgb(0x005FB8);
    internal static Color AccentHover => Blend(Accent, Page, 0.9);
    internal static Color OnAccent => Dark ? Color.Black : Color.White;
    internal static Color AccentBack => Blend(Accent, Page, Dark ? 0.18 : 0.14);

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
    /// Smooths text in grey rather than in colour, as Windows 11's own Settings does. Windows' colour smoothing, the
    /// default for text drawn here, makes small text look thin and sharp, with coloured edges on a dark background.
    /// Text drawn on <paramref name="g"/> afterwards follows it.
    /// </summary>
    internal static void SoftText(Graphics g) => g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

    internal static readonly Font HeadingFont = new(Face("Segoe UI Variable Text Semib", "Segoe UI Semibold"), 9.75f);
    internal static readonly Font PageTitleFont = new(Face("Segoe UI Variable Display Semib", "Segoe UI Semibold"), 20f);

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

/// <summary>An on/off option drawn as a row: what it does on the left, a switch on the right. Still a check box to
/// everything that reads or sets it.</summary>
internal sealed class SwitchBox : CheckBox
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
    private int TextWidth => Unit(RowWidth) - Unit(64);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Compact) return new Size(Unit(50), Unit(30));
        Size text = TextRenderer.MeasureText(Text, Font, new Size(TextWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(Unit(RowWidth), Math.Max(Unit(36), text.Height + Unit(14)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        Color surface = Parent?.BackColor ?? SettingsStyle.Card;
        g.Clear(surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color text = Enabled ? SettingsStyle.Text : SettingsStyle.Disabled;
        var area = new Rectangle(Unit(4), 0, TextWidth, Height);
        if (!Compact)
            TextRenderer.DrawText(g, Text, Font, area, text,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
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
        if (Focused && ShowFocusCues)
            SettingsStyle.FocusRing(g, Rectangle.Inflate(track, Unit(3), Unit(3)), Unit(13), this);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>A titled group of options: the title above, the options in a card with a line between them.</summary>
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

    private sealed class Body : FlowLayoutPanel
    {
        internal Body()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(8, 4, 8, 4);
            BackColor = SettingsStyle.Card;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e) => CardPanel.Draw(e.Graphics, this, drawRows: true);

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            // A pixel between options, where the line between them is drawn: an option covers whatever is under it, so
            // without it only the ends of the line showed, as short dashes at the card's edges.
            if (Controls.Count > 1 && e.Control is not Label && e.Control!.Margin.Top < 1)
                e.Control.Margin = e.Control.Margin with { Top = 1 };
            e.Control!.LocationChanged += (_, _) => Invalidate();
            e.Control.VisibleChanged += (_, _) => Invalidate();
        }
    }
}

/// <summary>A label whose words are smoothed like the rest of Settings (<see cref="SettingsStyle.SoftText"/>).</summary>
internal class SoftLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        base.OnPaint(e);
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

/// <summary>The list of pages down the left of the Settings window, under group headings.</summary>
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

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>Adds a group heading.</summary>
    internal void AddHeading(string text) => _entries.Add(new Entry(text, '\0', null));
    internal void Add(string text, char glyph, object page) => _entries.Add(new Entry(text, glyph, page));

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal object? Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
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
            int height = Unit(heading ? 32 : 36);
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
                TextRenderer.DrawText(g, entry.Text, MenuStyle.SmallFont, new Rectangle(bounds.Left + Unit(12), bounds.Top + Unit(10), bounds.Width, bounds.Height - Unit(10)),
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
                TextRenderer.DrawText(g, entry.Text, MenuStyle.Font, new Rectangle(bounds.Left + Unit(42), bounds.Top, bounds.Width - Unit(44), bounds.Height),
                    SettingsStyle.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (selected && Focused && ShowFocusCues) SettingsStyle.FocusRing(g, row, Unit(4), this);
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
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
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
            : _pressed ? SettingsStyle.PressedOn(surface) : _hot ? SettingsStyle.HoverOn(surface) : SettingsStyle.FillOn(surface);
        using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
        if (!Primary || !Enabled) { using var edge = new Pen(SettingsStyle.StrokeOn(back)); g.DrawPath(edge, path); }
        Color text = !Enabled ? SettingsStyle.Disabled : Primary ? SettingsStyle.OnAccent : _pressed ? SettingsStyle.Secondary : SettingsStyle.Text;
        TextRenderer.DrawText(g, Text, Font, area, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
        if (Focused && ShowFocusCues) SettingsStyle.FocusRing(g, area, Unit(4), this);
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
