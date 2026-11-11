using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Tray;

namespace WinScreen.Settings;

/// <summary>
/// A rounded box drawn around a control whose own border Windows would draw too bright: a text box, a number box, a
/// list or a table. The control inside loses its border and takes the box's colour.
/// </summary>
internal sealed class InputFrame : Panel
{
    internal enum Look
    {
        /// <summary>Something typed into: a control fill, with a line under it that turns the accent colour while typing.</summary>
        Text,
        /// <summary>A list or a table: the card colour, with an edge that turns the accent colour while it has the keyboard.</summary>
        List,
    }

    private readonly Look _look;
    private Control? _inner;
    private bool _hot;
    private int _hotSpin = -1;

    /// <summary>An icon drawn at the start of the box, as in a search field.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal char Glyph { get => _glyph; init { _glyph = value; Place(); } }
    private readonly char _glyph;
    /// <summary>A colour shown at the start of the box, for a box that holds one; null shows none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Color?>? Swatch { get => _swatch; init { _swatch = value; Place(); } }
    private readonly Func<Color?>? _swatch;
    /// <summary>Room left empty at the end of the box, at 100% scaling: for an error icon beside the text.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int ReserveEnd { get => _reserveEnd; init { _reserveEnd = value; Place(); } }
    private readonly int _reserveEnd;

    internal InputFrame(Control? inner, Look look = Look.Text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _look = look;
        Margin = Padding.Empty;
        Size = new Size(inner?.Width ?? 200, look == Look.Text ? 32 : inner?.Height ?? 100);
        Host(inner);
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);
    private NumericUpDown? Number => _inner as NumericUpDown;
    private bool Active => _inner?.ContainsFocus == true;

    /// <summary>Puts a different control in the box, or none.</summary>
    internal void Host(Control? inner)
    {
        if (_inner == inner) return;
        if (_inner is not null) { Controls.Remove(_inner); _clip.Controls.Remove(_inner); Controls.Remove(_clip); }
        _inner = inner;
        if (inner is null) { Invalidate(); return; }
        switch (inner)
        {
            case TextBox text: text.BorderStyle = BorderStyle.None; break;
            case NumericUpDown number:
                number.BorderStyle = BorderStyle.None;
                // Its own arrows are drawn by Windows in the light theme's colours; the box draws its own instead.
                foreach (Control part in number.Controls) if (part is not TextBox) part.Visible = false;
                break;
            case ListBox list: list.BorderStyle = BorderStyle.None; break;
            case ListView view: view.BorderStyle = BorderStyle.None; break;
        }
        inner.Dock = DockStyle.None;
        inner.Margin = Padding.Empty;
        inner.ForeColor = SettingsStyle.Text;
        if (!_hooked.Contains(inner))
        {
            _hooked.Add(inner);
            inner.Enter += (_, _) => Restyle();
            inner.Leave += (_, _) => Restyle();
            inner.EnabledChanged += (_, _) => Restyle();
            inner.MouseEnter += (_, _) => Hover(true);
            inner.MouseLeave += (_, _) => Hover(false);
            foreach (Control part in inner.Controls) { part.MouseEnter += (_, _) => Hover(true); part.MouseLeave += (_, _) => Hover(false); }
        }
        // A number box still keeps room for the arrows it no longer shows; a panel just narrower than it cuts that off.
        if (inner is NumericUpDown || Scrolls(inner)) { _clip.Controls.Add(inner); Controls.Add(_clip); }
        else Controls.Add(inner);
        if (_bar is not null) { Controls.Remove(_bar); _bar.Dispose(); _bar = null; }
        if (inner is ThemedList names)
        {
            _bar = new ThinScrollBar(names, row => names.TopIndex = row, () => SettingsStyle.Card);
            names.Painted += _bar.Sync;
        }
        else if (inner is ThemedTable table)
        {
            _bar = new ThinScrollBar(table, table.ScrollToRow, () => SettingsStyle.Card);
            table.Moved += _bar.Sync;
        }
        if (_bar is not null) { Controls.Add(_bar); _bar.BringToFront(); }
        Restyle();
        Place();
    }

    private readonly HashSet<Control> _hooked = [];

    private readonly Panel _clip = new() { Margin = Padding.Empty };
    private ThinScrollBar? _bar;
    private static bool Scrolls(Control control) => control is ThemedList or ThemedTable;

    private Color Surface => Parent?.BackColor ?? SettingsStyle.Card;

    private Color Fill
    {
        get
        {
            if (_look == Look.List) return SettingsStyle.Card;
            if (_inner is { Enabled: false } || !Enabled) return SettingsStyle.DisabledOn(Surface);
            return Active ? SettingsStyle.ActiveOn(Surface) : _hot ? SettingsStyle.HoverOn(Surface) : SettingsStyle.FillOn(Surface);
        }
    }

    private void Restyle()
    {
        if (_inner is not null)
        {
            _inner.BackColor = _clip.BackColor = Fill;
            _inner.ForeColor = _inner.Enabled ? SettingsStyle.Text : SettingsStyle.Disabled;
        }
        Invalidate();
    }

    private void Hover(bool on)
    {
        if (!on && ClientRectangle.Contains(PointToClient(Cursor.Position))) return; // Moved from the box onto what's in it.
        if (_hot == on) return;
        _hot = on;
        if (!on) _hotSpin = -1;
        Restyle();
    }

    private int StartRoom => Unit(Glyph != '\0' ? 34 : Swatch is not null ? 36 : 10);
    private int SpinWidth => Unit(26);
    private int EndRoom => (Number is not null ? SpinWidth * 2 + Unit(4) : Unit(8)) + Unit(ReserveEnd);

    private void Place()
    {
        if (_inner is null) return;
        if (_look == Look.List)
        {
            int inset = Unit(4);
            var area = new Rectangle(inset, inset, Math.Max(10, Width - inset * 2), Math.Max(10, Height - inset * 2));
            if (!Scrolls(_inner)) { _inner.Bounds = area; return; }
            // Windows' own scroll bar lies just past the cut-off edge; the thin one sits over the list's right side.
            _clip.Bounds = area;
            _inner.SetBounds(0, 0, area.Width + SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi), area.Height);
            int bar = Unit(ThinScrollBar.BarWidth);
            _bar?.SetBounds(area.Right - bar, area.Top, bar, area.Height);
            _bar?.Sync();
            return;
        }
        int height = _inner.Height;
        int width = Math.Max(10, Width - StartRoom - EndRoom);
        if (_inner is NumericUpDown)
        {
            _clip.SetBounds(StartRoom, (Height - height) / 2, width, height);
            _inner.SetBounds(0, 0, width + SystemInformation.VerticalScrollBarWidth + Unit(4), height);
        }
        else _inner.SetBounds(StartRoom, (Height - height) / 2, width, height);
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Place(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Place(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Restyle(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Restyle(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Restyle(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover(true); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover(false); }

    private Rectangle SpinBounds(int index) // 0 = up, 1 = down
    {
        int top = Unit(4), height = Height - Unit(8);
        int right = Width - Unit(4) - Unit(ReserveEnd);
        return new Rectangle(right - SpinWidth * (2 - index), top, SpinWidth, height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Number is null) return;
        int hot = SpinBounds(0).Contains(e.Location) ? 0 : SpinBounds(1).Contains(e.Location) ? 1 : -1;
        if (hot != _hotSpin) { _hotSpin = hot; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_inner is null || !_inner.Enabled) return;
        _inner.Focus();
        if (Number is not { } number || e.Button != MouseButtons.Left) return;
        if (SpinBounds(0).Contains(e.Location)) number.UpButton();
        else if (SpinBounds(1).Contains(e.Location)) number.DownButton();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Unit(4);
        bool enabled = Enabled && _inner?.Enabled != false;
        Color fill = Fill;
        using GraphicsPath path = SettingsStyle.Rounded(area, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        Color edge = _look == Look.List && Active ? SettingsStyle.Accent
            : _look == Look.List ? SettingsStyle.StrokeOn(SettingsStyle.Blend(SettingsStyle.Text, fill, 0.03)) : SettingsStyle.StrokeOn(fill);
        using (var pen = new Pen(edge)) g.DrawPath(pen, path);
        if (_look == Look.Text && enabled)
        {
            // The line under the words, inside the rounded corners: thin at rest, thicker in the accent colour while typing.
            int thick = Active ? Unit(2) : 1;
            g.SetClip(path);
            using var line = new SolidBrush(Active ? SettingsStyle.Accent : SettingsStyle.Underline);
            g.FillRectangle(line, 0, Height - thick, Width, thick);
            g.ResetClip();
        }
        Color glyphColor = enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled;
        if (Glyph != '\0' && MenuParts.Icon(Glyph, glyphColor) is { } icon)
            g.DrawImage(icon, Unit(10), (Height - icon.Height) / 2);
        if (Swatch?.Invoke() is { } swatch)
        {
            int size = Unit(18);
            var chip = new Rectangle(Unit(10), (Height - size) / 2, size, size);
            using GraphicsPath chipPath = SettingsStyle.Rounded(chip, Unit(3));
            using var chipFill = new SolidBrush(swatch);
            using var chipEdge = new Pen(SettingsStyle.Blend(SettingsStyle.Text, fill, 0.35));
            g.FillPath(chipFill, chipPath);
            g.DrawPath(chipEdge, chipPath);
        }
        if (Number is not null)
        {
            for (int index = 0; index < 2; index++)
            {
                Rectangle spin = SpinBounds(index);
                if (index == _hotSpin && enabled)
                {
                    using GraphicsPath spinPath = SettingsStyle.Rounded(spin, Unit(3));
                    using var hover = new SolidBrush(SettingsStyle.RowHoverOn(fill));
                    g.FillPath(hover, spinPath);
                }
                SettingsStyle.Chevron(g, new Point(spin.Left + spin.Width / 2, spin.Top + spin.Height / 2), glyphColor, this, up: index == 0);
            }
        }
    }
}

/// <summary>A list that drops down to choose one thing, drawn as Windows 11 draws one.</summary>
internal sealed class ThemedCombo : ComboBox
{
    private bool _hot;

    public ThemedCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SettingsStyle.Flyout;
        ForeColor = SettingsStyle.Text;
        FitHeight();
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>As tall as a text box beside it: the box is one row of the list plus its edges.</summary>
    private void FitHeight() => ItemHeight = Math.Max(TextRenderer.MeasureText("Ag", Font).Height + Unit(12), Unit(26));

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitHeight(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        Graphics g = e.Graphics;
        Color surface = Parent?.BackColor ?? SettingsStyle.Card;
        g.Clear(surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = !Enabled ? SettingsStyle.DisabledOn(surface) : DroppedDown ? SettingsStyle.PressedOn(surface)
            : _hot ? SettingsStyle.HoverOn(surface) : SettingsStyle.FillOn(surface);
        using (GraphicsPath path = SettingsStyle.Rounded(area, Unit(4)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(SettingsStyle.StrokeOn(fill)))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        string text = (SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text) ?? "";
        TextRenderer.DrawText(g, text, Font, new Rectangle(Unit(11), 0, Width - Unit(44), Height), Enabled ? SettingsStyle.Text : SettingsStyle.Disabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        SettingsStyle.Chevron(g, new Point(Width - Unit(18), Height / 2), Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, this);
        if (Focused && ShowFocusCues && !DroppedDown) SettingsStyle.FocusRing(g, area, Unit(4), this);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        if (e.Index < 0) return;
        Graphics g = e.Graphics;
        using (var back = new SolidBrush(SettingsStyle.Flyout)) g.FillRectangle(back, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var row = Rectangle.Inflate(e.Bounds, -Unit(3), -Unit(1));
        if (selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = SettingsStyle.Rounded(row, Unit(3));
            using var brush = new SolidBrush(SettingsStyle.RowHoverOn(SettingsStyle.Flyout));
            g.FillPath(brush, path);
        }
        if (e.Index == SelectedIndex) SettingsStyle.SelectionBar(g, row, this);
        TextRenderer.DrawText(g, GetItemText(Items[e.Index]), Font, new Rectangle(row.Left + Unit(10), row.Top, row.Width - Unit(12), row.Height),
            SettingsStyle.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>
/// A box to search for a program or type its name, as Windows 11's search boxes are: a search icon, words saying what
/// to do that stay readable, and suggestions from the programs running now as soon as typing starts. Its arrow opens
/// all of them. Windows draws the box's edge and arrow in the light theme's colours, so they are drawn over after it
/// paints; the typing part is its own window, and only the icon and the waiting words are drawn into it.
/// </summary>
internal sealed class ThemedPicker : ComboBox
{
    private const int EM_SETMARGINS = 0x00D3, EC_LEFTMARGIN = 0x1, MostSuggestions = 12;
    private bool _hot, _suggesting;
    private List<string>? _choices;
    private TypingPart? _typing;
    private nint _listWindow;

    /// <summary>What the box says while nothing is typed in it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Placeholder { get; set; } = "";

    /// <summary>The names it offers, read again each time the list opens or typing starts.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IEnumerable<string>>? Choices { get; set; }

    /// <summary>The picture beside a name in the list, at a size in pixels, or null for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, int, Image?>? IconOf { get; set; }

    public ThemedPicker()
    {
        DropDownStyle = ComboBoxStyle.DropDown;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ForeColor = SettingsStyle.Text;
        FitHeight();
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);
    private Color Surface => Parent?.BackColor ?? SettingsStyle.Card;
    private Color Fill => !Enabled ? SettingsStyle.DisabledOn(Surface) : Focused || ContainsFocus ? SettingsStyle.ActiveOn(Surface)
        : _hot ? SettingsStyle.HoverOn(Surface) : SettingsStyle.FillOn(Surface);
    /// <summary>The room before the words, where the search icon is.</summary>
    private int IconRoom => Unit(30);

    /// <summary>As tall as the buttons beside it: one row of the list plus the box's edges.</summary>
    private void FitHeight() => ItemHeight = Math.Max(TextRenderer.MeasureText("Ag", Font).Height + Unit(10), Unit(26));

    private void Restyle()
    {
        if (BackColor != Fill) BackColor = Fill;
        Invalidate();
        _typing?.Redraw();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
        _listWindow = GetComboBoxInfo(Handle, ref info) ? info.List : 0;
        nint typing = Native.User32.FindWindowEx(Handle, 0, "Edit", null);
        if (typing == 0) return;
        _typing = new TypingPart(this);
        _typing.AssignHandle(typing);
        SetMargin();
        PlaceTyping(); // Placed before it was watched.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboInfo
    {
        public int Size;
        public Native.Rect Item, Button;
        public int ButtonState;
        public nint Combo, Edit, List;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(nint combo, ref ComboInfo info);

    /// <summary>
    /// Fits the open list to what it offers: Windows opens it at its full height whatever it holds. Kept against the
    /// box when it opened upward for want of room below.
    /// </summary>
    private void FitList()
    {
        if (!DroppedDown || _listWindow == 0 || !Native.User32.GetWindowRect(_listWindow, out Native.Rect r)) return;
        int height = Math.Clamp(Items.Count, 1, MaxDropDownItems) * ItemHeight + 2;
        if (r.Bottom - r.Top == height) return;
        int top = r.Top < RectangleToScreen(ClientRectangle).Top ? r.Bottom - height : r.Top;
        Native.User32.SetWindowPos(_listWindow, 0, r.Left, top, r.Right - r.Left, height,
            Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _typing = null; // It lets go of the typing part by itself as that goes, after WinForms' own hold on it.
        _listWindow = 0;
        base.OnHandleDestroyed(e);
    }

    /// <summary>Moves the typing part to where <see cref="CenterTyping"/> keeps it, after anything that changes the box.</summary>
    private void PlaceTyping()
    {
        if (_typing is not { Handle: not 0 } typing || !Native.User32.GetWindowRect(typing.Handle, out Native.Rect r)) return;
        Rectangle at = RectangleToClient(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        Native.User32.SetWindowPos(typing.Handle, 0, at.X, 0, at.Width, Math.Max(at.Height, ClientSize.Height),
            Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); PlaceTyping(); }

    private void SetMargin()
    {
        if (_typing is { Handle: not 0 } typing) Native.User32.SendMessage(typing.Handle, EM_SETMARGINS, EC_LEFTMARGIN, IconRoom);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == ListRows.WM_PAINT && IsHandleCreated) DrawOver();
    }

    private void DrawOver()
    {
        using Graphics g = CreateGraphics();
        if (_typing is { Handle: not 0 } typing && Native.User32.GetWindowRect(typing.Handle, out Native.Rect r))
            g.ExcludeClip(RectangleToClient(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
        g.Clear(Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = Fill;
        using (GraphicsPath path = SettingsStyle.Rounded(area, Unit(4)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(SettingsStyle.StrokeOn(fill)))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
            g.SetClip(path, CombineMode.Intersect);
            int thick = ContainsFocus ? Unit(2) : 1;
            using var line = new SolidBrush(!Enabled ? fill : ContainsFocus ? SettingsStyle.Accent : SettingsStyle.Underline);
            g.FillRectangle(line, 0, Height - thick, Width, thick);
        }
        SettingsStyle.Chevron(g, new Point(Width - Unit(14), Height / 2), Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, this);
    }

    /// <summary>The search icon, and the waiting words while nothing is typed, drawn into the typing part after it paints.</summary>
    private void DrawInTyping(nint typing)
    {
        if (!Native.User32.GetClientRect(typing, out Native.Rect r)) return;
        var area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        // The blinking caret is drawn by inverting what's under it, so it is taken away while anything is drawn there.
        bool caret = Native.User32.HideCaret(typing);
        try
        {
            using Graphics g = Graphics.FromHwnd(typing);
            Color quiet = Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled;
            if (MenuParts.Icon(MenuParts.Search, quiet) is { } icon)
            {
                int size = Math.Min(icon.Height, area.Height);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new Rectangle(Unit(6), area.Top + (area.Height - size) / 2, size, size));
            }
            // Where typed words start, so they take the waiting words' place; gone while a character is being composed.
            if (Text.Length > 0 || Placeholder.Length == 0 || _typing?.Composing == true) return;
            TextRenderer.DrawText(g, Placeholder, Font, Rectangle.FromLTRB(IconRoom, area.Top, area.Right, area.Bottom), quiet, BackColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        finally
        {
            if (caret) Native.User32.ShowCaret(typing);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    /// <summary>
    /// Keeps the typing part at most one line tall and centred in the box: Windows puts it near the top and writes at
    /// its top, which leaves the words high.
    /// </summary>
    private void CenterTyping(nint place)
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
        WindowPos position = Marshal.PtrToStructure<WindowPos>(place);
        if ((position.Flags & SWP_NOSIZE) != 0 || !Native.User32.GetClientRect(Handle, out Native.Rect box)) return;
        int line = Math.Min(position.Height, TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height);
        if ((position.Flags & SWP_NOMOVE) != 0)
        {
            if (_typing is not { Handle: not 0 } typing || !Native.User32.GetWindowRect(typing.Handle, out Native.Rect now)) return;
            position.X = PointToClient(new Point(now.Left, now.Top)).X;
            position.Flags &= ~SWP_NOMOVE;
        }
        // Placed so its capitals sit in the middle of the box, as the words in the other boxes do; a line's capitals sit
        // high in it, above the room kept for descenders.
        position.Y = Math.Clamp((box.Bottom - box.Top) / 2 - CapitalsMiddle(Font), 0, Math.Max(0, box.Bottom - box.Top - line));
        position.Height = line;
        Marshal.StructureToPtr(position, place, false);
    }

    private static readonly Dictionary<(string, float, FontStyle), int> Capitals = [];

    /// <summary>How far down a line of this font the middle of its capitals is, found by writing one and looking.</summary>
    private static int CapitalsMiddle(Font font)
    {
        if (Capitals.TryGetValue((font.Name, font.Size, font.Style), out int middle)) return middle;
        Size cell = TextRenderer.MeasureText("H", font, Size.Empty, TextFormatFlags.NoPadding);
        using var picture = new Bitmap(Math.Max(1, cell.Width), Math.Max(1, cell.Height));
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.White);
            TextRenderer.DrawText(g, "H", font, Point.Empty, Color.Black, Color.White, TextFormatFlags.NoPadding);
        }
        int top = -1, bottom = -1;
        for (int y = 0; y < picture.Height; y++)
            for (int x = 0; x < picture.Width; x++)
                if (picture.GetPixel(x, y).R < 128)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
        middle = top < 0 ? cell.Height / 2 : (top + bottom + 1) / 2;
        return Capitals[(font.Name, font.Size, font.Style)] = middle;
    }

    private sealed class TypingPart(ThemedPicker owner) : NativeWindow
    {
        private const int WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_WINDOWPOSCHANGING = 0x0046;

        /// <summary>While a character is being put together in an input method, before it reaches the box.</summary>
        internal bool Composing { get; private set; }

        internal void Redraw()
        {
            if (Handle != 0) Native.User32.InvalidateRect(Handle, 0, true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is WM_IME_STARTCOMPOSITION or WM_IME_ENDCOMPOSITION)
            {
                Composing = m.Msg == WM_IME_STARTCOMPOSITION;
                Redraw();
            }
            else if (m.Msg == WM_WINDOWPOSCHANGING && m.LParam != 0) owner.CenterTyping(m.LParam);
            base.WndProc(ref m);
            if (m.Msg == ListRows.WM_PAINT) owner.DrawInTyping(Handle);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _typing?.Redraw(); // The waiting words come and go with what's typed.
    }

    protected override void OnDropDown(EventArgs e)
    {
        // Opened with the arrow: every program. Opened by typing: what matches, already in the list.
        if (!_suggesting) Offer(_choices = Choices?.Invoke().ToList() ?? []);
        base.OnDropDown(e);
    }

    /// <summary>As a name is typed, the running programs whose names hold it are offered under the box.</summary>
    protected override void OnTextUpdate(EventArgs e)
    {
        base.OnTextUpdate(e);
        if (Choices is null) return;
        string typed = Text;
        int caret = SelectionStart;
        _choices ??= Choices().ToList();
        string wanted = typed.Trim();
        List<string> matches = wanted.Length == 0 ? [] : _choices
            .Where(name => name.Contains(wanted, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(name => !name.StartsWith(wanted, StringComparison.CurrentCultureIgnoreCase))
            .Take(MostSuggestions).ToList();
        _suggesting = true;
        try
        {
            // Changing the list wipes the box's words, so it is left alone when it would stay empty.
            if (matches.Count > 0 || Items.Count > 0) Offer(matches);
            if (matches.Count > 0 && !DroppedDown)
            {
                DroppedDown = true;
                Cursor.Current = Cursors.Default; // Opening the list hides the pointer otherwise.
            }
            else if (matches.Count == 0 && DroppedDown) DroppedDown = false;
            FitList();
            // Changing the list and opening it both take over the box's words; what was typed is put back.
            if (Text != typed) Text = typed;
            Select(Math.Min(caret, typed.Length), 0);
        }
        finally { _suggesting = false; }
    }

    private void Offer(List<string> names)
    {
        BeginUpdate();
        Items.Clear();
        Items.AddRange(names.Cast<object>().ToArray());
        EndUpdate();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        _choices = null; // Programs started since last time are offered too.
        Restyle();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Graphics g = e.Graphics;
        using (var back = new SolidBrush(SettingsStyle.Flyout)) g.FillRectangle(back, e.Bounds);
        var row = Rectangle.Inflate(e.Bounds, -Unit(3), -Unit(1));
        if ((e.State & DrawItemState.Selected) != 0)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = SettingsStyle.Rounded(row, Unit(3));
            using var brush = new SolidBrush(SettingsStyle.RowHoverOn(SettingsStyle.Flyout));
            g.FillPath(brush, path);
        }
        string name = GetItemText(Items[e.Index]) ?? "";
        int size = Unit(16), left = row.Left + Unit(8);
        if (IconOf?.Invoke(name, size) is { } icon)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(left, row.Top + (row.Height - size) / 2, size, size));
        }
        left += size + Unit(10);
        TextRenderer.DrawText(g, name, Font, new Rectangle(left, row.Top, row.Right - left - Unit(4), row.Height), SettingsStyle.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); SetMargin(); PlaceTyping(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitHeight(); SetMargin(); PlaceTyping(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Restyle(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Restyle(); } // Placed on a card.
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Restyle(); }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return; // Onto the typing part.
        _hot = false;
        Restyle();
    }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Restyle(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Restyle(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
}

/// <summary>The pictures of running programs, by the names the program lists use, each made once.</summary>
internal static class ProgramIcons
{
    private static readonly Dictionary<string, nint> Windows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, int), Image?> Made = [];

    /// <summary>Remembers a window of the program, to find its file from when its picture is first wanted.</summary>
    internal static void Remember(string name, nint window) => Windows[name] = window;

    internal static Image? Of(string name, int size)
    {
        if (Made.TryGetValue((name, size), out Image? made)) return made;
        if (!Windows.TryGetValue(name, out nint window)) return null; // Not running; asked again once it is.
        Image? picture = null;
        string path = WinScreen.Windows.WindowInfo.ExecutablePath(window);
        try
        {
            if (path.Length > 0 && Icon.ExtractAssociatedIcon(path) is { } icon)
                using (icon)
                using (var sized = new Icon(icon, size, size))
                    picture = sized.ToBitmap();
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No picture then; the name is still there.
        }
        return Made[(name, size)] = picture;
    }
}

/// <summary>Rows drawn the Windows 11 way for <see cref="ThemedList"/> and <see cref="ThemedCheckList"/>.</summary>
internal static class ListRows
{
    internal const int LB_SETITEMHEIGHT = 0x01A0;
    private const int LB_ADDSTRING = 0x0180, LB_INSERTSTRING = 0x0181, LB_DELETESTRING = 0x0182, LB_RESETCONTENT = 0x0184;
    internal const int WM_PAINT = 0x000F;

    internal static bool ChangesItems(int message) => message is LB_ADDSTRING or LB_INSERTSTRING or LB_DELETESTRING or LB_RESETCONTENT;

    internal static int RowHeight(Control list) => Math.Max(TextRenderer.MeasureText("Ag", list.Font).Height + SettingsStyle.Unit(list, 12), SettingsStyle.Unit(list, 30));

    /// <summary>The background of a row: a wash under the pointer, the accent wash and bar when chosen.</summary>
    internal static Rectangle Background(Graphics g, ListBox list, Rectangle bounds, bool selected, bool hot)
    {
        using (var back = new SolidBrush(list.BackColor)) g.FillRectangle(back, bounds);
        var row = new Rectangle(bounds.Left + SettingsStyle.Unit(list, 2), bounds.Top + SettingsStyle.Unit(list, 1),
            bounds.Width - SettingsStyle.Unit(list, 4), bounds.Height - SettingsStyle.Unit(list, 2));
        if (selected || hot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = SettingsStyle.Rounded(row, SettingsStyle.Unit(list, 4));
            using var brush = new SolidBrush(selected ? SettingsStyle.RowSelectedOn(list.BackColor) : SettingsStyle.RowHoverOn(list.BackColor));
            g.FillPath(brush, path);
        }
        if (selected) SettingsStyle.SelectionBar(g, row, list);
        return row;
    }

    /// <summary>What an empty list says instead of showing nothing.</summary>
    internal static void Empty(ListBox list, string text)
    {
        if (list.Items.Count > 0 || text.Length == 0 || !list.IsHandleCreated) return;
        using Graphics g = list.CreateGraphics();
        var area = Rectangle.Inflate(list.ClientRectangle, -SettingsStyle.Unit(list, 12), -SettingsStyle.Unit(list, 4));
        TextRenderer.DrawText(g, text, list.Font, area, list.Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    internal static int HotRow(ListBox list, Point point)
    {
        int index = list.IndexFromPoint(point);
        return index >= 0 && list.GetItemRectangle(index).Contains(point) ? index : -1;
    }
}

/// <summary>A list of names with Windows 11's rows, and a line of its own to say so when it is empty.</summary>
internal class ThemedList : ListBox
{
    private int _hot = -1;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string EmptyText { get; set; } = "";
    internal event EventHandler? ItemsChanged;
    /// <summary>After every repaint, which is when it may have scrolled.</summary>
    internal event Action? Painted;

    public ThemedList()
    {
        BorderStyle = BorderStyle.None;
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        ScrollAlwaysVisible = true; // Its rows are then always as wide as what shows: the bar itself is hidden past the edge.
        BackColor = SettingsStyle.Card;
        ForeColor = SettingsStyle.Text;
        ItemHeight = ListRows.RowHeight(this);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = ListRows.RowHeight(this); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Rectangle row = ListRows.Background(e.Graphics, this, e.Bounds, (e.State & DrawItemState.Selected) != 0, e.Index == _hot);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(row.Left + SettingsStyle.Unit(this, 12), row.Top, row.Width - SettingsStyle.Unit(this, 16), row.Height),
            Enabled ? SettingsStyle.Text : SettingsStyle.Disabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == ListRows.WM_PAINT)
        {
            ListRows.Empty(this, EmptyText);
            Painted?.Invoke();
        }
        else if (ListRows.ChangesItems(m.Msg))
        {
            Invalidate();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (Items.Count == 0) Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = ListRows.HotRow(this, e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }
}

/// <summary>A list of things to tick, with Windows 11's rows and check boxes.</summary>
internal sealed class ThemedCheckList : CheckedListBox
{
    private int _hot = -1;

    public ThemedCheckList()
    {
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        BackColor = SettingsStyle.Card;
        ForeColor = SettingsStyle.Text;
    }

    /// <summary>The height of one row. The list's own ItemHeight follows the font only, so the rows are set to this instead.</summary>
    internal int RowHeight => ListRows.RowHeight(this);

    /// <summary>Which entries are experimental, to show the tag after their words.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<object, bool>? Tagged { get; set; }

    private void ApplyRowHeight()
    {
        if (IsHandleCreated) Native.User32.SendMessage(Handle, ListRows.LB_SETITEMHEIGHT, 0, RowHeight);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyRowHeight(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyRowHeight(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        SettingsStyle.SoftText(e.Graphics);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Graphics g = e.Graphics;
        Rectangle row = ListRows.Background(g, this, e.Bounds, false, e.Index == _hot);
        bool on = GetItemChecked(e.Index);
        int size = SettingsStyle.Unit(this, 18);
        var box = new Rectangle(row.Left + SettingsStyle.Unit(this, 10), row.Top + (row.Height - size) / 2, size, size);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = SettingsStyle.Rounded(box, SettingsStyle.Unit(this, 4)))
        {
            if (on)
            {
                using var fill = new SolidBrush(Enabled ? SettingsStyle.Accent : SettingsStyle.Disabled);
                g.FillPath(fill, path);
                using var tick = new Pen(SettingsStyle.OnAccent, Math.Max(1.4f, DeviceDpi / 96f * 1.4f))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick,
                [
                    new PointF(box.Left + box.Width * 0.25f, box.Top + box.Height * 0.52f),
                    new PointF(box.Left + box.Width * 0.43f, box.Top + box.Height * 0.70f),
                    new PointF(box.Left + box.Width * 0.76f, box.Top + box.Height * 0.32f),
                ]);
            }
            else
            {
                using var fill = new SolidBrush(SettingsStyle.FillOn(BackColor));
                using var edge = new Pen(Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled);
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
        }
        int textLeft = box.Right + SettingsStyle.Unit(this, 10);
        string text = GetItemText(Items[e.Index]) ?? "";
        const TextFormatFlags format = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, text, Font, new Rectangle(textLeft, row.Top, row.Right - textLeft, row.Height),
            Enabled ? SettingsStyle.Text : SettingsStyle.Disabled, format);
        if (Tagged?.Invoke(Items[e.Index]) == true)
            MenuParts.DrawTag(g, textLeft + TextRenderer.MeasureText(g, text, Font, Size.Empty, format & ~TextFormatFlags.EndEllipsis).Width + SettingsStyle.Unit(this, 8),
                row.Top + row.Height / 2, value => SettingsStyle.Unit(this, value), Enabled);
        if ((e.State & DrawItemState.Focus) != 0 && Focused && ShowFocusCues) SettingsStyle.FocusRing(g, row, SettingsStyle.Unit(this, 4), this);
    }

    protected override void OnItemCheck(ItemCheckEventArgs ice)
    {
        base.OnItemCheck(ice);
        int index = ice.Index;
        if (!IsHandleCreated) return; // Drawn as it is when it first shows.
        BeginInvoke(() => { if (index < Items.Count) Invalidate(GetItemRectangle(index)); }); // After the tick lands.
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = ListRows.HotRow(this, e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }
}

/// <summary>
/// A list of programs with a way to add one and remove one: the same on every page that has one. It is as tall as its
/// programs need, up to six rows, and says so when it is empty.
/// </summary>
internal sealed class ProgramList : Panel
{
    private const int MostRows = 6;
    private readonly Label? _title;
    private readonly ThemedList _list;
    private readonly InputFrame _frame;
    private readonly ComboBox? _picker;
    private readonly PlainButton? _add;
    private readonly PlainButton _remove;

    /// <param name="list">The list the page reads and fills.</param>
    /// <param name="picker">Where a name is typed or chosen, or null for a list nothing is added to by hand.</param>
    /// <param name="remove">What removing the chosen entry does; by default it leaves the list.</param>
    internal ProgramList(ThemedList list, ComboBox? picker, string? title, string empty, string addText, string removeText,
        Action<object>? remove = null)
    {
        _list = list;
        _list.EmptyText = empty;
        _frame = new InputFrame(list, InputFrame.Look.List);
        if (title is not null)
        {
            _title = new SoftLabel { Text = title, AutoSize = true, UseMnemonic = false, Margin = Padding.Empty, ForeColor = SettingsStyle.Text };
            Controls.Add(_title);
        }
        Controls.Add(_frame);
        remove ??= item => _list.Items.Remove(item);
        _remove = new PlainButton { Text = removeText, Enabled = false };
        _remove.Click += (_, _) => { if (_list.SelectedItem is { } item) remove(item); };
        if (picker is not null)
        {
            _picker = picker;
            _picker.Dock = DockStyle.None;
            _picker.Anchor = AnchorStyles.None;
            _add = new PlainButton { Text = addText, Enabled = false };
            _add.Click += (_, _) => Add();
            _picker.TextChanged += (_, _) => _add.Enabled = _picker.Text.Trim().Length > 0; // Nothing to add until a name is typed.
            _picker.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (_picker.DroppedDown) _picker.DroppedDown = false; // Enter on a suggestion takes it and adds it.
                Add();
            };
            Controls.Add(_picker);
            Controls.Add(_add);
        }
        Controls.Add(_remove);
        _list.SelectedIndexChanged += (_, _) => _remove.Enabled = _list.SelectedIndex >= 0;
        _list.ItemsChanged += (_, _) => { _remove.Enabled = _list.SelectedIndex >= 0; PerformLayout(); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || !_remove.Enabled) return;
            e.Handled = true;
            _remove.PerformClick();
        };
        Margin = new Padding(3, 6, 3, 8);
        Width = SwitchBox.RowWidth; // Last: this lays the parts out.
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    private void Add()
    {
        if (_picker is null) return;
        string name = _picker.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length > 0 && !_list.Items.Contains(name)) _list.Items.Add(name);
        _picker.Text = "";
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_remove is null) return; // Still being built.
        int y = 0;
        if (_title is not null)
        {
            _title.Location = new Point(Unit(1), 0);
            y = _title.Bottom + Unit(6);
        }
        int rows = Math.Clamp(_list.Items.Count, 1, MostRows);
        int listHeight = rows * _list.ItemHeight + Unit(8);
        _frame.SetBounds(0, y, Width, listHeight);
        y = _frame.Bottom + Unit(8);
        Size removeSize = _remove.GetPreferredSize(Size.Empty);
        int rowHeight = removeSize.Height;
        int right = Width;
        _remove.SetBounds(right - removeSize.Width, y, removeSize.Width, removeSize.Height);
        right = _remove.Left - Unit(6);
        if (_add is not null && _picker is not null)
        {
            Size addSize = _add.GetPreferredSize(Size.Empty);
            _add.SetBounds(right - addSize.Width, y, addSize.Width, addSize.Height);
            _picker.SetBounds(0, y + (rowHeight - _picker.Height) / 2, _add.Left - Unit(8), _picker.Height);
            rowHeight = Math.Max(rowHeight, _picker.Height);
        }
        int height = y + rowHeight;
        if (Height != height) Height = height;
    }
}
