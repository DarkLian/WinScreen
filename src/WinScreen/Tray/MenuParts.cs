using System.Drawing.Text;
using WinScreen.Settings;

namespace WinScreen.Tray;

/// <summary>What WinScreen's menus are built from besides plain commands: group headings, icons, and two rows that
/// hold a control (an opacity slider and a grid of places).</summary>
internal static class MenuParts
{
    // In Windows 10 and 11 alike. A glyph the font lacks would draw as an empty box, so only long-standing ones are used.
    private const string IconFont = "Segoe MDL2 Assets";
    private static readonly Dictionary<(char, int, int), Image> Icons = [];
    private static bool? _hasFont;

    internal const char Pin = '', Pointer = '', NewWindow = '', Pinned = '', Moon = '',
        FullScreen = '', Undo = '', Copy = '', Folder = '', Save = '', More = '',
        Settings = '', Sync = '', Info = '', Power = '', View = '', Crop = '',
        Star = '', Help = '', Minimize = '', Close = '', Warning = '', Page = '',
        Monitor = '', Home = '', Keyboard = '', Mouse = '', Bar = '', Palette = '', Apps = '', Search = '', Sliders = '', Brightness = '', Repair = '', History = '';

    internal static int SystemDpi
    {
        get { using Graphics graphics = Graphics.FromHwnd(0); return (int)Math.Round(graphics.DpiX); }
    }

    internal static int Scale(int value) => (int)Math.Round(value * SystemDpi / 96.0);

    /// <summary>A small picture of a glyph in the menu's text colour, or null where the icon font is missing.</summary>
    internal static Image? Icon(char glyph) => Icon(glyph, MenuStyle.Text);

    internal static Image? Icon(char glyph, Color color)
    {
        _hasFont ??= FontFamily.Families.Any(family => family.Name == IconFont);
        if (_hasFont != true) return null;
        int size = Scale(16);
        if (Icons.TryGetValue((glyph, size, color.ToArgb()), out Image? cached)) return cached;
        var picture = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(picture))
        using (var font = new Font(IconFont, size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
        }
        return Icons[(glyph, size, color.ToArgb())] = picture;
    }

    internal static T WithIcon<T>(this T item, char glyph) where T : ToolStripItem
    {
        item.Image = Icon(glyph);
        return item;
    }

    /// <summary>A quiet line naming the group of commands under it. It can't be chosen.</summary>
    internal static ToolStripLabel Header(string text) => new(text)
    {
        ForeColor = MenuStyle.Muted,
        Font = MenuStyle.SmallFont,
        Margin = new Padding(0, Scale(8), 0, 0),
        AccessibleRole = AccessibleRole.Grouping,
        Tag = HeaderTag,
    };

    internal static readonly object HeaderTag = new();

    internal static char? IconOf(AppCommand command) => command switch
    {
        AppCommand.TogglePin => Pin,
        AppCommand.ToggleClickThrough => Pointer,
        AppCommand.OpenPip or AppCommand.PickPipSource => NewWindow,
        AppCommand.ToggleAnchor => Pinned,
        AppCommand.ToggleDarkTitleBar => Moon,
        AppCommand.RestoreWindow or AppCommand.RestoreAll => Undo,
        AppCommand.SaveWindowSetup => Save,
        AppCommand.ToggleDefaultOpacity => Brightness,
        AppCommand.ShowWindowInfo => Info,
        _ => null,
    };
}

/// <summary>A menu row with a slider: the window's opacity, changed as the slider moves.</summary>
internal sealed class OpacityRow : ToolStripControlHost
{
    private readonly Slider _slider;
    private bool _setting;

    public OpacityRow(string title, Action<int> changed) : base(new Slider(title))
    {
        _slider = (Slider)Control;
        AutoSize = false;
        Size = _slider.Size = new Size(MenuParts.Scale(236), MenuParts.Scale(46));
        Margin = Padding.Empty;
        _slider.ValueChanged += () => { if (!_setting) changed(_slider.Value); };
    }

    /// <summary>Percent solid, 10 to 100.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int Value
    {
        get => _slider.Value;
        set
        {
            _setting = true;
            try { _slider.Value = value; }
            finally { _setting = false; }
        }
    }

    /// <summary>Drawn here rather than by Windows, so it sits in the menu like the rows around it.</summary>
    private sealed class Slider : Control
    {
        private const int Minimum = 10, Maximum = 100, Step = 5;
        private readonly string _title;
        private int _value = Maximum;
        private bool _dragging;
        internal event Action? ValueChanged;

        internal Slider(string title)
        {
            _title = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = title;
            AccessibleRole = AccessibleRole.Slider;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int Value
        {
            get => _value;
            set
            {
                value = Math.Clamp(value, Minimum, Maximum);
                if (value == _value) return;
                _value = value;
                Invalidate();
                ValueChanged?.Invoke();
            }
        }

        private int Left0 => MenuParts.Scale(8);
        private int Right0 => Width - MenuParts.Scale(14);
        private int TrackY => MenuParts.Scale(32);
        private int ThumbX => Left0 + (int)Math.Round((Right0 - Left0) * (_value - Minimum) / (double)(Maximum - Minimum));

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(MenuStyle.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color text = Enabled ? MenuStyle.Text : MenuStyle.Disabled, accent = Enabled ? MenuStyle.Accent : MenuStyle.Disabled;
            var top = new Rectangle(Left0 - MenuParts.Scale(4), 0, Right0 - Left0 + MenuParts.Scale(8), MenuParts.Scale(22));
            TextRenderer.DrawText(g, _title, MenuStyle.Font, top, text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _value + "%", MenuStyle.Font, top, Enabled ? MenuStyle.Muted : MenuStyle.Disabled,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int thick = MenuParts.Scale(4), radius = MenuParts.Scale(Focused ? 8 : 7);
            using (var rest = new Pen(MenuStyle.Line, thick) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawLine(rest, Left0, TrackY, Right0, TrackY);
            using (var done = new Pen(accent, thick) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawLine(done, Left0, TrackY, ThumbX, TrackY);
            using var knob = new SolidBrush(accent);
            using var rim = new Pen(MenuStyle.Back, MenuParts.Scale(2));
            g.FillEllipse(knob, ThumbX - radius, TrackY - radius, radius * 2, radius * 2);
            g.DrawEllipse(rim, ThumbX - radius, TrackY - radius, radius * 2, radius * 2);
        }

        private void Follow(int x)
        {
            double share = Math.Clamp((x - Left0) / (double)Math.Max(1, Right0 - Left0), 0, 1);
            Value = (int)Math.Round((Minimum + share * (Maximum - Minimum)) / Step) * Step;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = Capture = true;
            Follow(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) Follow(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = Capture = false;
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: Value -= Step; e.Handled = true; break;
                case Keys.Right: Value += Step; e.Handled = true; break;
                case Keys.Home: Value = Minimum; e.Handled = true; break;
                case Keys.End: Value = Maximum; e.Handled = true; break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }
}

/// <summary>A menu row with nine small places to put the window: each corner, each edge and the centre.</summary>
internal sealed class PlacementRow : ToolStripControlHost
{
    public PlacementRow(Action<AppCommand> chosen) : base(new Grid())
    {
        var grid = (Grid)Control;
        AutoSize = false;
        Size = grid.Size = new Size(MenuParts.Scale(236), MenuParts.Scale(66));
        Margin = Padding.Empty;
        grid.Chosen += place =>
        {
            // Close first: the command may move the window the menu belongs to.
            (Owner as ToolStripDropDown)?.Close(ToolStripDropDownCloseReason.ItemClicked);
            chosen(place);
        };
    }

    private sealed class Grid : Control
    {
        private static readonly AppCommand[] Places =
        [
            AppCommand.AlignTopLeft, AppCommand.AlignTop, AppCommand.AlignTopRight,
            AppCommand.AlignLeft, AppCommand.AlignCenter, AppCommand.AlignRight,
            AppCommand.AlignBottomLeft, AppCommand.AlignBottom, AppCommand.AlignBottomRight,
        ];
        private readonly ToolTip _tips = new();
        private int _hot = -1, _chosen = 4;
        internal event Action<AppCommand>? Chosen;

        internal Grid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = Localization.UiText.T("menu.group.place");
            AccessibleRole = AccessibleRole.Grouping;
        }

        private Rectangle Cell(int index)
        {
            int width = MenuParts.Scale(34), height = MenuParts.Scale(18), gap = MenuParts.Scale(3);
            return new Rectangle(MenuParts.Scale(4) + index % 3 * (width + gap), MenuParts.Scale(3) + index / 3 * (height + gap), width, height);
        }

        private int At(Point point)
        {
            for (int index = 0; index < Places.Length; index++)
                if (Cell(index).Contains(point)) return index;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(MenuStyle.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (int index = 0; index < Places.Length; index++)
            {
                Rectangle cell = Cell(index);
                bool lit = Enabled && (index == _hot || Focused && index == _chosen);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = MenuParts.Scale(6);
                path.AddArc(cell.Left, cell.Top, d, d, 180, 90);
                path.AddArc(cell.Right - d, cell.Top, d, d, 270, 90);
                path.AddArc(cell.Right - d, cell.Bottom - d, d, d, 0, 90);
                path.AddArc(cell.Left, cell.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                if (lit) { using var fill = new SolidBrush(MenuStyle.AccentBack); g.FillPath(fill, path); }
                using var edge = new Pen(!Enabled ? MenuStyle.Line : lit ? MenuStyle.Accent : MenuStyle.Disabled);
                g.DrawPath(edge, path);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hot = At(e.Location);
            if (hot == _hot) return;
            _hot = hot;
            _tips.SetToolTip(this, hot < 0 ? "" : CommandCatalog.Name(Places[hot]));
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && At(e.Location) is >= 0 and var index) Chosen?.Invoke(Places[index]);
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: _chosen = (_chosen + 8) % 9; break;
                case Keys.Right: _chosen = (_chosen + 1) % 9; break;
                case Keys.Enter or Keys.Space: e.Handled = true; Chosen?.Invoke(Places[_chosen]); return;
                default: return;
            }
            e.Handled = true;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
