using System.Diagnostics;
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
        Size = new Size(inner?.Width ?? 200, look == Look.Text ? BoxHeight.Standard : inner?.Height ?? 100);
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
        // A text box writes at its top and keeps room below for descenders; centring the box left its words low.
        int top = Math.Clamp(Height / 2 - ThemedPicker.CapitalsMiddle(_inner.Font), 0, Math.Max(0, Height - height));
        if (_inner is NumericUpDown)
        {
            _clip.SetBounds(StartRoom, top, width, height);
            _inner.SetBounds(0, 0, width + SystemInformation.VerticalScrollBarWidth + Unit(4), height);
        }
        else if (_inner is TextBox) _inner.SetBounds(StartRoom, top, width, height);
        else _inner.SetBounds(StartRoom, (Height - height) / 2, width, height);
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Place(); }
    // Also after the window scales what's in it, which moves the control inside after the box placed it.
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Place(); }
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
        // A list keeps its quiet edge while it has the keyboard, as in Windows 11: its chosen row shows where it is.
        Color edge = _look == Look.List ? SettingsStyle.StrokeOn(SettingsStyle.Blend(SettingsStyle.Text, fill, 0.03)) : SettingsStyle.StrokeOn(fill);
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

/// <summary>
/// A text box that says what to type while it is empty, also while it has the keyboard, as Windows 11's search boxes
/// do. WinForms' own waiting words go when the box is clicked, and Windows' own are not drawn in a box with no border.
/// </summary>
internal sealed class HintTextBox : TextBox
{
    private const int WM_PAINT = 0x000F, EM_GETMARGINS = 0x00D4;
    private bool _wasEmpty = true;

    /// <summary>What the box says while nothing is typed in it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Hint { get; set { field = value; Invalidate(); } } = "";

    private const int WM_ERASEBKGND = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Dc;
        public int Erase, Left, Top, Right, Bottom, Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintStruct paint);

    /// <summary>
    /// An empty box is drawn whole by itself, background and hint in one go: Windows wiping it first and the hint
    /// following made it blink as the pointer passed and its colour changed.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // Asked only for these two: asking how long the text is sends a message of its own.
        bool hinting = m.Msg is WM_ERASEBKGND or WM_PAINT && Hint.Length > 0 && IsHandleCreated && TextLength == 0;
        if (hinting && m.Msg == WM_ERASEBKGND) { m.Result = 1; return; }
        if (hinting && m.Msg == WM_PAINT)
        {
            BeginPaint(Handle, out PaintStruct paint);
            try { DrawHint(paint.Dc); }
            finally { EndPaint(Handle, ref paint); }
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }

    // Typing writes straight onto the box, so it is drawn again when it stops or starts being empty.
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        bool empty = TextLength == 0;
        if (empty != _wasEmpty) Invalidate();
        _wasEmpty = empty;
    }

    private void DrawHint(nint dc)
    {
        int left = (int)(Native.User32.SendMessage(Handle, EM_GETMARGINS, 0, 0) & 0xFFFF);
        // The blinking caret is drawn by inverting what's under it, so it is taken away while the words are drawn.
        bool caret = Native.User32.HideCaret(Handle);
        try
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using var picture = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (Graphics g = Graphics.FromImage(picture))
            {
                g.Clear(BackColor);
                SettingsStyle.SoftText(g);
                Native.DirectText.DrawText(g, Hint, Font, Rectangle.FromLTRB(left, 0, ClientSize.Width, ClientSize.Height),
                    Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            using Graphics screen = Graphics.FromHdc(dc);
            screen.DrawImage(picture, new Rectangle(Point.Empty, picture.Size));
        }
        finally
        {
            if (caret) Native.User32.ShowCaret(Handle);
        }
    }
}

/// <summary>A list that drops down to choose one thing, drawn as Windows 11 draws one.</summary>
internal sealed class ThemedCombo : ComboBox
{
    private bool _hot;
    private int _chosen = -1;

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

    /// <summary>Rows of the open list with room around the words; the box itself is as tall as every other box.</summary>
    private void FitHeight()
    {
        ItemHeight = Math.Max(Native.DirectText.MeasureText("Ag", Font).Height + Unit(12), Unit(26));
        BoxHeight.Fit(this);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); BoxHeight.Fit(this); }
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
        Native.DirectText.DrawText(g, text, Font, new Rectangle(Unit(11), 0, Width - Unit(44), Height), Enabled ? SettingsStyle.Text : SettingsStyle.Disabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        SettingsStyle.Chevron(g, new Point(Width - Unit(18), Height / 2), Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, this);
        if (SettingsStyle.ShowsFocus(this) && !DroppedDown) SettingsStyle.FocusRing(g, area, Unit(4), this);
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
        // While the list is open, Windows reports the row under the pointer as the chosen one; the bar stays on the
        // choice made before it opened, as in Windows 11, instead of being left on every row the pointer passed.
        if (e.Index == (DroppedDown ? _chosen : SelectedIndex)) SettingsStyle.SelectionBar(g, row, this);
        Native.DirectText.DrawText(g, GetItemText(Items[e.Index]), Font, new Rectangle(row.Left + Unit(10), row.Top, row.Width - Unit(12), row.Height),
            SettingsStyle.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnDropDown(EventArgs e) { _chosen = SelectedIndex; base.OnDropDown(e); Invalidate(); }
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

    /// <summary>Rows of the open list with room around the words; the box itself is as tall as every other box.</summary>
    private void FitHeight()
    {
        ItemHeight = Math.Max(Native.DirectText.MeasureText("Ag", Font).Height + Unit(12), Unit(26));
        BoxHeight.Fit(this);
    }

    private void Restyle()
    {
        if (BackColor != Fill) BackColor = Fill;
        Invalidate();
        _typing?.Redraw();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BoxHeight.Fit(this);
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
        // The box is painted here alone, in one go: Windows' own box, painted first and covered after, showed as a blink.
        if (m.Msg == ListRows.WM_ERASEBKGND && IsHandleCreated) { m.Result = 1; return; }
        if (m.Msg == ListRows.WM_PAINT && IsHandleCreated)
        {
            DrawBox();
            Native.User32.ValidateRect(Handle, 0);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }

    private void DrawBox()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var picture = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(picture)) DrawBox(g);
        using Graphics screen = CreateGraphics();
        if (_typing is { Handle: not 0 } typing && Native.User32.GetWindowRect(typing.Handle, out Native.Rect r))
            screen.ExcludeClip(RectangleToClient(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
        screen.DrawImage(picture, new Rectangle(Point.Empty, picture.Size));
    }

    private void DrawBox(Graphics g)
    {
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

    /// <summary>
    /// Paints the typing part off screen and puts it up whole: its words as Windows writes them, then the search icon
    /// and, while nothing is typed, the waiting words. Painted in steps on the screen, the icon and words blinked.
    /// </summary>
    private void PaintTyping(nint typing)
    {
        const int WM_PRINTCLIENT = 0x0318, PRF_CLIENT = 0x4, WM_GETTEXTLENGTH = 0x000E;
        if (!Native.User32.GetClientRect(typing, out Native.Rect r) || r.Right <= 0 || r.Bottom <= 0)
        {
            Native.User32.ValidateRect(typing, 0);
            return;
        }
        // The blinking caret is drawn by inverting what's under it, so it is taken away while anything is drawn there.
        bool caret = Native.User32.HideCaret(typing);
        try
        {
            using var picture = new Bitmap(r.Right, r.Bottom, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // Text blends over it as drawn.
            using (Graphics g = Graphics.FromImage(picture))
            {
                g.Clear(BackColor);
                nint dc = g.GetHdc();
                try { Native.User32.SendMessage(typing, WM_PRINTCLIENT, dc, PRF_CLIENT); }
                finally { g.ReleaseHdc(dc); }
                // Read from the typing part itself: the box's own words can point at a row of a list just emptied.
                bool typed = Native.User32.SendMessage(typing, WM_GETTEXTLENGTH, 0, 0) > 0;
                DrawInTyping(g, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), typed);
            }
            using Graphics screen = Graphics.FromHwnd(typing);
            screen.DrawImage(picture, new Rectangle(Point.Empty, picture.Size));
        }
        finally
        {
            Native.User32.ValidateRect(typing, 0);
            if (caret) Native.User32.ShowCaret(typing);
        }
    }

    /// <summary>The search icon, and the waiting words while nothing is typed.</summary>
    private void DrawInTyping(Graphics g, Rectangle area, bool typed)
    {
        Color quiet = Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled;
        if (MenuParts.Icon(MenuParts.Search, quiet) is { } icon)
        {
            int size = Math.Min(icon.Height, area.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(Unit(6), area.Top + (area.Height - size) / 2, size, size));
        }
        // Where typed words start, so they take the waiting words' place; gone while a character is being composed.
        if (typed || Placeholder.Length == 0 || _typing?.Composing == true) return;
        Native.DirectText.DrawText(g, Placeholder, Font, Rectangle.FromLTRB(IconRoom, area.Top, area.Right, area.Bottom), quiet, BackColor,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
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
        int line = Math.Min(position.Height, Native.DirectText.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height);
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
    internal static int CapitalsMiddle(Font font)
    {
        if (Capitals.TryGetValue((font.Name, font.Size, font.Style), out int middle)) return middle;
        Size cell = Native.DirectText.MeasureText("H", font, Size.Empty, TextFormatFlags.NoPadding);
        using var picture = new Bitmap(Math.Max(1, cell.Width), Math.Max(1, cell.Height));
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.White);
            Native.DirectText.DrawText(g, "H", font, Point.Empty, Color.Black, Color.White, TextFormatFlags.NoPadding);
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
            else if (m.Msg == ListRows.WM_ERASEBKGND) { m.Result = 1; return; } // Painted whole below.
            else if (m.Msg == ListRows.WM_PAINT) { owner.PaintTyping(Handle); m.Result = 0; return; }
            base.WndProc(ref m);
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
            // Closing a list just emptied can leave the box pointing at a row no longer there; reading its words then fails.
            if (SelectedIndex >= Items.Count) SelectedIndex = -1;
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
        Native.DirectText.DrawText(g, name, Font, new Rectangle(left, row.Top, row.Right - left - Unit(4), row.Height), SettingsStyle.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); SetMargin(); PlaceTyping(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitHeight(); SetMargin(); PlaceTyping(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Restyle(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Restyle(); } // Placed on a card.
    // Moving between the box's edge and its typing part says the pointer came in again; it is redrawn only when that changes something.
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover(true); }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return; // Onto the typing part.
        Hover(false);
    }

    private void Hover(bool on)
    {
        if (_hot == on) return;
        _hot = on;
        Restyle();
    }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Restyle(); }
    // Entering comes just before the keyboard arrives; the typing part's colour follows once it has.
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Restyle(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Restyle(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Restyle(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
}

/// <summary>
/// The height every box in Settings has, at 100% scaling, as Windows 11 makes its text boxes, lists and buttons; and a
/// way to give it to a list that drops down, whose own height follows its rows.
/// </summary>
internal static class BoxHeight
{
    internal const int Standard = 32;
    private const int CB_SETITEMHEIGHT = 0x0153, CB_GETITEMHEIGHT = 0x0154;

    /// <summary>Sets the height of the part that shows the choice, leaving the rows of the open list as they are.</summary>
    internal static void Fit(ComboBox box)
    {
        if (!box.IsHandleCreated) return;
        int field = (int)Native.User32.SendMessage(box.Handle, CB_GETITEMHEIGHT, -1, 0);
        if (!Native.User32.GetWindowRect(box.Handle, out Native.Rect r) || field <= 0) return;
        int edges = r.Bottom - r.Top - field; // Its frame, above and below the choice.
        int wanted = SettingsStyle.Unit(box, Standard) - edges;
        if (wanted > 0 && wanted != field) Native.User32.SendMessage(box.Handle, CB_SETITEMHEIGHT, -1, wanted);
    }
}

/// <summary>
/// The pictures of programs, by the names the program lists use, each made once. A program's file is found from its
/// window while it runs, and remembered in a small file so its picture still shows after it has closed.
/// </summary>
internal static class ProgramIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, int), Image?> Made = [];
    private static readonly Dictionary<string, long> Missed = new(StringComparer.OrdinalIgnoreCase);
    private static string? _file;

    /// <summary>Where the programs' files are remembered between runs; read when set.</summary>
    internal static string? File
    {
        get => _file;
        set
        {
            _file = value;
            if (value is null || !System.IO.File.Exists(value)) return;
            try
            {
                var known = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(value));
                foreach ((string name, string path) in known ?? []) Paths.TryAdd(name, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    /// <summary>Remembers where a running program's file is, from one of its windows.</summary>
    internal static void Remember(string name, nint window)
    {
        string path = WinScreen.Windows.WindowInfo.ExecutablePath(window);
        if (path.Length == 0 || Paths.TryGetValue(name, out string? known) && string.Equals(known, path, StringComparison.OrdinalIgnoreCase)) return;
        Paths[name] = path;
        Missed.Remove(name);
        if (_file is null) return;
        try { System.IO.File.WriteAllText(_file, System.Text.Json.JsonSerializer.Serialize(Paths)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Found again next time it runs.
    }

    private static Dictionary<string, nint> _running = new(StringComparer.OrdinalIgnoreCase);
    private static long _runningAt = -100_000; // Long enough ago that the first look runs.

    /// <summary>A window of each program running now, looked up at most every half minute: rows are drawn often.</summary>
    private static Dictionary<string, nint> Running()
    {
        if (Environment.TickCount64 - _runningAt < 30000) return _running;
        _runningAt = Environment.TickCount64;
        var found = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
            using (process)
            {
                try { if (process.MainWindowHandle != 0) found.TryAdd(process.ProcessName, process.MainWindowHandle); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        return _running = found;
    }

    /// <summary>The program's file: remembered, from a copy running now, or from where Windows says it is installed.</summary>
    private static string? Find(string name)
    {
        if (Paths.TryGetValue(name, out string? known) && System.IO.File.Exists(known)) return known;
        if (Running().TryGetValue(name, out nint window))
        {
            Remember(name, window);
            if (Paths.TryGetValue(name, out known)) return known;
        }
        foreach (Microsoft.Win32.RegistryKey root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using Microsoft.Win32.RegistryKey? key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}.exe");
            if (key?.GetValue(null) is string path && System.IO.File.Exists(path = path.Trim('"'))) return path;
        }
        return null;
    }

    internal static Image? Of(string name, int size)
    {
        if (Made.TryGetValue((name, size), out Image? made)) return made;
        // Looking costs a little, and rows are drawn often: a program not found is looked for again a while later.
        if (Missed.TryGetValue(name, out long when) && Environment.TickCount64 - when < 15000) return null;
        if (Find(name) is not { } path) { Missed[name] = Environment.TickCount64; return null; }
        Image? picture = null;
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

    internal static int RowHeight(Control list) => Math.Max(Native.DirectText.MeasureText("Ag", list.Font).Height + SettingsStyle.Unit(list, 12), SettingsStyle.Unit(list, 30));

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
        Native.DirectText.DrawText(g, text, list.Font, area, list.Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    internal static int HotRow(ListBox list, Point point)
    {
        int index = list.IndexFromPoint(point);
        return index >= 0 && list.GetItemRectangle(index).Contains(point) ? index : -1;
    }

    internal const int WM_ERASEBKGND = 0x0014;

    /// <summary>
    /// Fills only the part no row covers: each row paints its own background, and Windows filling under all of them
    /// first showed as a blink.
    /// </summary>
    internal static void EraseBelowRows(ListBox list, ref Message m)
    {
        int bottom = list.Items.Count == 0 ? 0 : Math.Max(0, list.GetItemRectangle(list.Items.Count - 1).Bottom);
        var below = Rectangle.FromLTRB(0, bottom, list.ClientSize.Width, list.ClientSize.Height);
        if (below.Width > 0 && below.Height > 0)
        {
            using Graphics g = Graphics.FromHdc(m.WParam);
            using var back = new SolidBrush(list.BackColor);
            g.FillRectangle(back, below);
        }
        m.Result = 1;
    }

    /// <summary>Moves the pointer's wash to another row, redrawing just the two rows.</summary>
    internal static void MoveHot(ListBox list, ref int hot, int now)
    {
        if (now == hot) return;
        foreach (int row in new[] { hot, now })
            if (row >= 0 && row < list.Items.Count) list.Invalidate(list.GetItemRectangle(row));
        hot = now;
    }

    /// <summary>
    /// Draws a row off screen and puts it up whole: drawn straight onto the screen, the background showed for a moment
    /// before the words. The row is drawn at the buffer's own top left, since words go through Windows, which does not
    /// follow the buffer's shift to where the row sits.
    /// </summary>
    internal static void DrawBuffered(DrawItemEventArgs e, Action<Graphics, Rectangle> draw)
    {
        using BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds);
        buffer.Graphics.ResetTransform();
        SettingsStyle.SoftText(buffer.Graphics);
        draw(buffer.Graphics, new Rectangle(Point.Empty, e.Bounds.Size));
        buffer.Render(e.Graphics);
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

    /// <summary>A picture before each entry, such as its program's icon, at the size asked for; none when null.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<object, int, Image?>? IconOf { get; set; }

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
        if (e.Index < 0 || e.Index >= Items.Count) return;
        ListRows.DrawBuffered(e, (g, bounds) =>
        {
            Rectangle row = ListRows.Background(g, this, bounds, (e.State & DrawItemState.Selected) != 0, e.Index == _hot);
            int left = row.Left + SettingsStyle.Unit(this, 12);
            if (IconOf is not null)
            {
                int size = SettingsStyle.Unit(this, 16);
                if (IconOf(Items[e.Index], size) is { } icon)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, new Rectangle(left, row.Top + (row.Height - size) / 2, size, size));
                }
                left += size + SettingsStyle.Unit(this, 10); // The words line up whether or not a picture was found.
            }
            Native.DirectText.DrawText(g, GetItemText(Items[e.Index]), Font,
                new Rectangle(left, row.Top, row.Right - left - SettingsStyle.Unit(this, 4), row.Height),
                Enabled ? SettingsStyle.Text : SettingsStyle.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ListRows.WM_ERASEBKGND && m.WParam != 0) { ListRows.EraseBelowRows(this, ref m); return; }
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
        ListRows.MoveHot(this, ref _hot, ListRows.HotRow(this, e.Location));
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ListRows.MoveHot(this, ref _hot, -1); }
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
        if (e.Index < 0 || e.Index >= Items.Count) return;
        ListRows.DrawBuffered(e, (g, bounds) => DrawRow(g, bounds, e));
    }

    private void DrawRow(Graphics g, Rectangle bounds, DrawItemEventArgs e)
    {
        Rectangle row = ListRows.Background(g, this, bounds, false, e.Index == _hot);
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
        Native.DirectText.DrawText(g, text, Font, new Rectangle(textLeft, row.Top, row.Right - textLeft, row.Height),
            Enabled ? SettingsStyle.Text : SettingsStyle.Disabled, format);
        if (Tagged?.Invoke(Items[e.Index]) == true)
            MenuParts.DrawTag(g, textLeft + Native.DirectText.MeasureText(g, text, Font, Size.Empty, format & ~TextFormatFlags.EndEllipsis).Width + SettingsStyle.Unit(this, 8),
                row.Top + row.Height / 2, value => SettingsStyle.Unit(this, value), Enabled);
        if ((e.State & DrawItemState.Focus) != 0 && SettingsStyle.ShowsFocus(this)) SettingsStyle.FocusRing(g, row, SettingsStyle.Unit(this, 4), this);
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
        ListRows.MoveHot(this, ref _hot, ListRows.HotRow(this, e.Location));
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ListRows.MoveHot(this, ref _hot, -1); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ListRows.WM_ERASEBKGND && m.WParam != 0) { ListRows.EraseBelowRows(this, ref m); return; }
        base.WndProc(ref m);
    }
}

/// <summary>
/// A list of programs with a way to add one and remove one: the same on every page that has one. It is as tall as its
/// programs need, up to six rows, and says so when it is empty.
/// </summary>
internal sealed class ProgramList : Panel
{
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
        _list.IconOf ??= (item, size) => item is string name ? ProgramIcons.Of(name, size) : null;
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

    /// <summary>The most rows it shows before it scrolls: more in a taller Settings window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int MostRows
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PerformLayout();
        }
    } = 6;

    private void Add()
    {
        if (_picker is null) return;
        string name = _picker.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length > 0 && !_list.Items.Contains(name)) _list.Items.Add(name);
        _list.ClearSelected(); // Adding chooses nothing: there is nothing to do with the new entry yet.
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

/// <summary>
/// A number chosen by sliding, as in Windows 11's Settings: the value beside a track that fills up to a round thumb.
/// The thumb's dot grows under the pointer and shrinks while held. The wheel and the arrow keys move it a step at a
/// time. The number box it stands for stays inside, hidden, and holds the value.
/// </summary>
internal sealed class NumberSlider : Control
{
    private readonly NumericUpDown _number;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private bool _hot, _held;
    private float _scale = Rest, _from = Rest, _to = Rest;
    private long _since;
    private const float Rest = 0.86f, Hover = 1.167f, Pressed = 0.71f;
    private const int Duration = 167;

    internal NumericUpDown Number => _number;

    internal NumberSlider(NumericUpDown number)
    {
        _number = number;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        number.Visible = false;
        number.TabStop = false;
        Controls.Add(number);
        number.ValueChanged += (_, _) => { Invalidate(); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); };
        number.EnabledChanged += (_, _) => Invalidate();
        _animation.Tick += (_, _) => Animate();
        Fit();
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);
    private bool Usable => Enabled && _number.Enabled;

    /// <summary>Room for the longest value, then a track about as long as Windows 11's.</summary>
    internal void Fit()
    {
        var size = new Size(NumberRoom + Unit(TrackLength + 20), Unit(BoxHeight.Standard));
        if (Size != size) Size = size;
    }

    private const int TrackLength = 160;

    private int NumberRoom => Math.Max(Native.DirectText.MeasureText(Shown(_number.Maximum), Font).Width,
        Native.DirectText.MeasureText(Shown(_number.Minimum), Font).Width) + Unit(4);

    private static string Shown(decimal value) => ((int)value).ToString(System.Globalization.CultureInfo.CurrentCulture);

    private (int Left, int Right) Track => (NumberRoom + Unit(18), Width - Unit(10));

    private float Share => _number.Maximum <= _number.Minimum ? 0 : (float)((_number.Value - _number.Minimum) / (_number.Maximum - _number.Minimum));

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SettingsStyle.Card);
        SettingsStyle.SoftText(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool usable = Usable;
        Native.DirectText.DrawText(g, Shown(_number.Value), Font, new Rectangle(0, 0, NumberRoom, Height),
            usable ? SettingsStyle.Text : SettingsStyle.Disabled,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        (int left, int right) = Track;
        float middle = Height / 2f, thick = Unit(4), x = left + (right - left) * Share;
        Color rest = MenuStyle.Dark ? Color.FromArgb(0x8B, 255, 255, 255) : Color.FromArgb(0x72, 0, 0, 0);
        Color fill = usable ? SettingsStyle.Accent : SettingsStyle.Disabled;
        using (var brush = new SolidBrush(usable ? rest : Color.FromArgb(0x50, rest)))
            FillBar(g, brush, left, right, middle, thick);
        using (var brush = new SolidBrush(fill))
            FillBar(g, brush, left, x, middle, thick);
        // The thumb: a round face with a faint edge, and the accent dot in it.
        float outer = Unit(10);
        var face = new RectangleF(x - outer, middle - outer, outer * 2, outer * 2);
        Color faceColor = MenuStyle.Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.White;
        using (var brush = new SolidBrush(faceColor)) g.FillEllipse(brush, face);
        using (var edge = new Pen(MenuStyle.Dark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x29, 0, 0, 0), 1f))
            g.DrawEllipse(edge, RectangleF.Inflate(face, -0.5f, -0.5f));
        float dot = Unit(6) * (usable ? _scale : Rest);
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, x - dot, middle - dot, dot * 2, dot * 2);
        if (SettingsStyle.ShowsFocus(this))
            SettingsStyle.FocusRing(g, Rectangle.Round(RectangleF.Inflate(face, Unit(3), Unit(3))), Unit(13), this);
    }

    private static void FillBar(Graphics g, Brush brush, float from, float to, float middle, float thick)
    {
        if (to - from < 1) return;
        using GraphicsPath path = SettingsStyle.Rounded(Rectangle.Round(new RectangleF(from, middle - thick / 2, to - from, thick)), Math.Max(1, (int)(thick / 2)));
        g.FillPath(brush, path);
    }

    /// <summary>Starts the dot growing or shrinking towards the size for the pointer's state.</summary>
    private void Become()
    {
        float target = !Usable ? Rest : _held ? Pressed : _hot ? Hover : Rest;
        if (target == _to) return;
        (_from, _to, _since) = (_scale, target, Environment.TickCount64);
        _animation.Start();
    }

    private void Animate()
    {
        float t = Math.Clamp((Environment.TickCount64 - _since) / (float)Duration, 0, 1);
        float eased = 1 - (1 - t) * (1 - t) * (1 - t);
        _scale = _from + (_to - _from) * eased;
        if (t >= 1) _animation.Stop();
        Invalidate();
    }

    private void Set(decimal value)
    {
        decimal step = Math.Max(1, _number.Increment);
        value = _number.Minimum + Math.Round((value - _number.Minimum) / step) * step;
        value = Math.Clamp(value, _number.Minimum, _number.Maximum);
        if (_number.Value != value) _number.Value = value;
    }

    private void SetFrom(int x)
    {
        (int left, int right) = Track;
        float share = Math.Clamp((x - left) / (float)Math.Max(1, right - left), 0, 1);
        Set(_number.Minimum + (decimal)share * (_number.Maximum - _number.Minimum));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Become(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Become(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Usable || e.X < Track.Left - Unit(10)) return;
        Focus();
        _held = Capture = true;
        SetFrom(e.X);
        Become();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_held) SetFrom(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_held) return;
        _held = Capture = false;
        _hot = ClientRectangle.Contains(e.Location);
        Become();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_held && !Capture) { _held = false; Become(); }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Usable) return;
        decimal step = Math.Max(1, _number.Increment);
        decimal big = Math.Max(step, Math.Round((_number.Maximum - _number.Minimum) / 10 / step) * step);
        decimal? to = e.KeyCode switch
        {
            Keys.Left or Keys.Down => _number.Value - step,
            Keys.Right or Keys.Up => _number.Value + step,
            Keys.PageDown => _number.Value - big,
            Keys.PageUp => _number.Value + big,
            Keys.Home => _number.Minimum,
            Keys.End => _number.Maximum,
            _ => null,
        };
        if (to is not { } value) return;
        Set(value);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Become(); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessible(this);

    private sealed class SliderAccessible(NumberSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => Shown(owner._number.Value);
            set { if (decimal.TryParse(value, out decimal number)) owner.Set(number); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}
