using System.Drawing.Drawing2D;
using WinScreen.Tray;

namespace WinScreen.Settings;

/// <summary>The colours the Settings window adds to the menus' own.</summary>
internal static class SettingsStyle
{
    internal static Color Side => MenuStyle.Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    internal static Color Page => MenuStyle.Dark ? Color.FromArgb(39, 39, 39) : Color.FromArgb(249, 249, 249);
    internal static Color Card => MenuStyle.Dark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(255, 255, 255);
    internal static Color BadgeBack => MenuStyle.Dark ? Color.FromArgb(67, 53, 25) : Color.FromArgb(255, 244, 206);
    internal static Color BadgeText => MenuStyle.Dark ? Color.FromArgb(252, 225, 0) : Color.FromArgb(121, 82, 0);

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

    private int Unit(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    private int TextWidth => Unit(RowWidth) - Unit(64);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Compact) return new Size(Unit(48), Unit(30));
        Size text = TextRenderer.MeasureText(Text, Font, new Size(TextWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(Unit(RowWidth), Math.Max(Unit(34), text.Height + Unit(14)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SettingsStyle.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color text = Enabled ? MenuStyle.Text : MenuStyle.Disabled;
        var area = new Rectangle(Unit(4), 0, TextWidth, Height);
        if (!Compact)
            TextRenderer.DrawText(g, Text, Font, area, text,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        int width = Unit(36), height = Unit(18);
        var track = new Rectangle(Width - width - Unit(6), (Height - height) / 2, width, height);
        using (GraphicsPath path = SettingsStyle.Rounded(track, height / 2))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Enabled ? MenuStyle.Accent : MenuStyle.Disabled);
                g.FillPath(fill, path);
            }
            else
            {
                using var edge = new Pen(Enabled ? (_hot ? MenuStyle.Text : MenuStyle.Muted) : MenuStyle.Disabled);
                g.DrawPath(edge, path);
            }
        }
        int knob = Unit(_hot && Enabled ? 12 : 10);
        int x = Checked ? track.Right - Unit(4) - knob / 2 - Unit(5) : track.Left + Unit(4) + Unit(5) - knob / 2;
        using var dot = new SolidBrush(Checked ? SettingsStyle.Card : Enabled ? MenuStyle.Muted : MenuStyle.Disabled);
        g.FillEllipse(dot, x, track.Top + (height - knob) / 2, knob, knob);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(track, Unit(3), Unit(3)), text, Parent?.BackColor ?? SettingsStyle.Card);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>A titled group of options: the title above, the options in a bordered card with a line between them.</summary>
internal sealed class SettingsCard : Panel
{
    private readonly Label _title = new() { AutoSize = true, Margin = Padding.Empty };
    private readonly Body _body = new();
    private readonly bool _experimental;
    private readonly string _badge;

    internal SettingsCard(string title, bool experimental, string badge, int width, IEnumerable<Control> items)
    {
        _experimental = experimental;
        _badge = badge;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 14);
        _title.Text = title;
        _title.Font = MenuStyle.TitleFont;
        _title.ForeColor = MenuStyle.Text;
        _title.Location = new Point(2, 0);
        _title.Padding = new Padding(0, 0, 0, 6);
        _body.MinimumSize = new Size(width, 0);
        foreach (Control item in items) _body.Controls.Add(item);
        Controls.Add(_title);
        Controls.Add(_body);
        Paint += (_, e) => DrawBadge(e.Graphics);
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
        Size text = TextRenderer.MeasureText(_badge, MenuStyle.SmallFont, Size.Empty, TextFormatFlags.NoPadding);
        var area = new Rectangle(_title.Right + 8, _title.Top + 1, text.Width + 12, text.Height + 4);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = SettingsStyle.Rounded(area, 4);
        using var back = new SolidBrush(SettingsStyle.BadgeBack);
        g.FillPath(back, path);
        TextRenderer.DrawText(g, _badge, MenuStyle.SmallFont, area, SettingsStyle.BadgeText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SettingsStyle.Page);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = SettingsStyle.Rounded(area, 6))
            using (var fill = new SolidBrush(SettingsStyle.Card))
            using (var edge = new Pen(MenuStyle.Line))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            using var line = new Pen(MenuStyle.Line);
            Control? previous = null;
            foreach (Control child in Controls.Cast<Control>().Where(child => child.Visible).OrderBy(child => child.Top))
            {
                // A note belongs to the option above it, so no line comes between them.
                if (previous is not null && child is not Label)
                    g.DrawLine(line, 1, (previous.Bottom + child.Top) / 2, Width - 2, (previous.Bottom + child.Top) / 2);
                previous = child;
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control!.LocationChanged += (_, _) => Invalidate();
            e.Control.VisibleChanged += (_, _) => Invalidate();
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

    private int Unit(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

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
            int height = Unit(heading ? 30 : 32);
            yield return (entry, new Rectangle(Unit(6), y, Width - Unit(12), height));
            y += height;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int index = 0;
        foreach (var (entry, bounds) in Rows())
        {
            if (entry.Page is null)
            {
                TextRenderer.DrawText(g, entry.Text, MenuStyle.SmallFont, new Rectangle(bounds.Left + Unit(8), bounds.Top + Unit(8), bounds.Width, bounds.Height - Unit(8)),
                    MenuStyle.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                bool selected = Equals(entry.Page, _selected);
                if (selected || index == _hot)
                {
                    using GraphicsPath path = SettingsStyle.Rounded(Rectangle.Inflate(bounds, 0, -Unit(1)), Unit(4));
                    using var back = new SolidBrush(selected ? MenuStyle.AccentBack : MenuStyle.Hover);
                    g.FillPath(back, path);
                }
                Color color = selected ? MenuStyle.Accent : MenuStyle.Text;
                if (MenuParts.Icon(entry.Glyph, color) is { } icon)
                    g.DrawImage(icon, bounds.Left + Unit(8), bounds.Top + (bounds.Height - icon.Height) / 2);
                TextRenderer.DrawText(g, entry.Text, MenuStyle.Font, new Rectangle(bounds.Left + Unit(34), bounds.Top, bounds.Width - Unit(36), bounds.Height),
                    color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (selected && Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(bounds, -Unit(2), -Unit(3)));
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

/// <summary>A button drawn flat with rounded corners: filled in the accent colour for the main one, outlined otherwise.</summary>
internal sealed class PlainButton : Button
{
    private bool _hot;
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

    /// <summary>As big as its words need and no bigger: a standard button leaves far more room around them.</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        int unit = DeviceDpi;
        return new Size(Math.Max(text.Width + 22 * unit / 96, 64 * unit / 96), Math.Max(text.Height + 10 * unit / 96, 28 * unit / 96));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SettingsStyle.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = SettingsStyle.Rounded(area, 4);
        Color back = !Enabled ? SettingsStyle.Page : Primary ? MenuStyle.Accent : _hot ? MenuStyle.Hover : SettingsStyle.Card;
        using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
        if (!Primary || !Enabled) { using var edge = new Pen(MenuStyle.Line); g.DrawPath(edge, path); }
        Color text = !Enabled ? MenuStyle.Disabled : Primary ? SettingsStyle.Card : MenuStyle.Text;
        TextRenderer.DrawText(g, Text, Font, area, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(area, -3, -3));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
}

/// <summary>The search field: an icon and the words typed, in one rounded box, with the words centred in its height.</summary>
internal sealed class SearchField : Panel
{
    internal SearchField(TextBox box)
    {
        Dock = DockStyle.Top;
        Height = 32;
        BackColor = SettingsStyle.Card;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        box.BorderStyle = BorderStyle.None;
        box.Dock = DockStyle.None;
        box.BackColor = SettingsStyle.Card;
        box.ForeColor = MenuStyle.Text;
        Controls.Add(box);
        void Place() => box.SetBounds(Height, (Height - box.Height) / 2, Math.Max(10, Width - Height - 8), box.Height);
        Resize += (_, _) => Place();
        box.SizeChanged += (_, _) => Place();
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => box.Focus();
        Place();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SettingsStyle.Side);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = SettingsStyle.Rounded(area, 4);
        using var fill = new SolidBrush(SettingsStyle.Card);
        using var edge = new Pen(ContainsFocus ? MenuStyle.Accent : MenuStyle.Line);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
        if (MenuParts.Icon(MenuParts.Search, MenuStyle.Muted) is { } icon)
            g.DrawImage(icon, (Height - icon.Width) / 2, (Height - icon.Height) / 2);
    }
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
