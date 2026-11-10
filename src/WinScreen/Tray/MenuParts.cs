using System.Runtime.CompilerServices;
using WinScreen.Settings;

namespace WinScreen.Tray;

/// <summary>What WinScreen's menus are built from besides plain commands: group headings, icons, and two rows that
/// hold a control (an opacity slider and a grid of places).</summary>
internal static class MenuParts
{
    // Windows 11's icon font, or on Windows 10 the older one it grew from: the same glyphs at the same code points.
    // A glyph the font lacks would draw as an empty box, so only long-standing ones are used.
    private static readonly string? IconFont =
        new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(name => FontFamily.Families.Any(family => family.Name == name));
    private static readonly Dictionary<(char, int, int, bool), Image> Icons = [];

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

    /// <summary>A small picture of a glyph in its own colour, or null where the icon font is missing.</summary>
    internal static Image? Icon(char glyph)
    {
        Image? picture = Icon(glyph, ColorOf(glyph), twoTone: true);
        if (picture is not null) Themed.AddOrUpdate(picture, new StrongBox<char>(glyph));
        return picture;
    }

    /// <summary>Icons drawn in their own colour, by glyph: their colour depends on the light or dark look.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, StrongBox<char>> Themed = [];

    /// <summary>The same icon in the look in use now, for one made before the look changed.</summary>
    internal static Image ForCurrentTheme(Image image) =>
        Themed.TryGetValue(image, out StrongBox<char>? glyph) && Icon(glyph.Value) is { } current ? current : image;

    /// <summary>
    /// The colour an icon is drawn in, as Windows 11 colours its own: a few families of colour by what the icon is
    /// about, a lighter shade of each in the dark theme, and the text colour for plain ones.
    /// </summary>
    internal static Color ColorOf(char glyph)
    {
        bool dark = MenuStyle.Dark;
        Color blue = dark ? Color.FromArgb(76, 194, 255) : Color.FromArgb(15, 108, 189);
        Color teal = dark ? Color.FromArgb(68, 205, 196) : Color.FromArgb(3, 131, 135);
        Color green = dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(16, 124, 16);
        Color amber = dark ? Color.FromArgb(255, 200, 61) : Color.FromArgb(194, 124, 14);
        Color orange = dark ? Color.FromArgb(255, 156, 90) : Color.FromArgb(202, 80, 16);
        Color red = dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28);
        Color purple = dark ? Color.FromArgb(195, 166, 255) : Color.FromArgb(135, 100, 184);
        return glyph switch
        {
            Pin or NewWindow or Copy or Save or Info or Help or Monitor or Home or Apps or Keyboard or Page => blue,
            Pointer or FullScreen or View or Bar or Mouse => teal,
            Undo or Sync or History => green,
            Moon or Star or Folder or Brightness or Warning => amber,
            Pinned or Repair => orange,
            Power or Close => red,
            Palette or Crop or Sliders => purple,
            _ => MenuStyle.Text,
        };
    }

    /// <summary>A glyph in one colour; with <paramref name="twoTone"/>, its inside washed with that colour too.</summary>
    internal static Image? Icon(char glyph, Color color, bool twoTone = false)
    {
        if (IconFont is null) return null;
        int size = Scale(16);
        twoTone &= color != MenuStyle.Text;
        if (Icons.TryGetValue((glyph, size, color.ToArgb(), twoTone), out Image? cached)) return cached;
        var picture = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(picture))
        using (var family = new FontFamily(IconFont))
        using (var shape = new System.Drawing.Drawing2D.GraphicsPath())
        using (var brush = new SolidBrush(color))
        {
            // The glyph at the size it was drawn for, placed by its own outline rather than by the font's line height
            // (which sits it high), with its edges on whole pixels so it stays sharp.
            shape.AddString(glyph.ToString(), family, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF ink = shape.GetBounds();
            if (ink.Width > 0 && ink.Height > 0)
            {
                using var move = new System.Drawing.Drawing2D.Matrix();
                move.Translate(MathF.Round((size - ink.Width) / 2) - ink.Left, MathF.Round((size - ink.Height) / 2) - ink.Top);
                shape.Transform(move);
            }
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            // Two tones, as Windows 11 draws its own: the shape filled with a soft wash of the colour, the outline over it.
            if (twoTone && Silhouette(shape) is { } inside)
            {
                using (inside)
                using (var wash = new SolidBrush(Color.FromArgb(MenuStyle.Dark ? 70 : 52, color)))
                    graphics.FillPath(wash, inside);
            }
            graphics.FillPath(brush, shape);
        }
        return Icons[(glyph, size, color.ToArgb(), twoTone)] = picture;
    }

    /// <summary>
    /// The inside of an outline icon: its outer edges only, with the gaps between the lines filled. An outline glyph is
    /// rings of ink; the rings' outer edges wind one way and their inner edges the other, so keeping the edges that wind
    /// like the largest one keeps the outer ones.
    /// </summary>
    private static System.Drawing.Drawing2D.GraphicsPath? Silhouette(System.Drawing.Drawing2D.GraphicsPath glyph)
    {
        using var flat = (System.Drawing.Drawing2D.GraphicsPath)glyph.Clone();
        flat.Flatten(null, 0.1f);
        PointF[] points = flat.PathPoints;
        var parts = new List<(PointF[] Points, double Area)>();
        using (var walk = new System.Drawing.Drawing2D.GraphicsPathIterator(flat))
        {
            while (walk.NextSubpath(out int start, out int end, out _) > 0)
            {
                PointF[] part = points[start..(end + 1)];
                double area = 0;
                for (int i = 0; i < part.Length; i++)
                {
                    PointF a = part[i], b = part[(i + 1) % part.Length];
                    area += (double)a.X * b.Y - (double)b.X * a.Y;
                }
                if (part.Length >= 3) parts.Add((part, area / 2));
            }
        }
        if (parts.Count < 2) return null; // A solid glyph has no inside to fill.
        double outer = Math.Sign(parts.MaxBy(part => Math.Abs(part.Area)).Area);
        var inside = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Winding);
        foreach (var (part, area) in parts)
            if (Math.Sign(area) == outer && Math.Abs(area) > 1.5) inside.AddPolygon(part);
        return inside;
    }

    internal static T WithIcon<T>(this T item, char glyph) where T : ToolStripItem
    {
        item.Image = Icon(glyph);
        return item;
    }

    private static readonly ConditionalWeakTable<ToolStripItem, object> Experimentals = [];

    /// <summary>Marks a command as experimental: it shows the same tag after its words as Settings does.</summary>
    internal static T Experimental<T>(this T item) where T : ToolStripItem
    {
        Experimentals.AddOrUpdate(item, item);
        if (item is ToolStripMenuItem entry) ShowKeys(entry, entry.ShortcutKeyDisplayString);
        return item;
    }

    /// <summary>
    /// Shows a command's shortcut keys. A menu is only as wide as its words and keys, so an experimental command's keys
    /// carry the tag's words in front, unseen, to leave room for the tag; only the keys after them are drawn.
    /// </summary>
    internal static void ShowKeys(ToolStripMenuItem item, string? keys)
    {
        keys = KeysOnly(keys);
        item.ShortcutKeyDisplayString = IsExperimental(item) ? TagRoom + keys : keys.Length > 0 ? keys : null;
    }

    /// <summary>The keys a command shows, without the room kept for a tag.</summary>
    internal static string KeysOnly(string? shown) => shown is null ? "" : shown.StartsWith(TagRoom, StringComparison.Ordinal) ? shown[TagRoom.Length..] : shown;

    private static string TagRoom => TagText + "\u2003\u2003"; // Two wide spaces: the gap before the tag.

    internal static bool IsExperimental(ToolStripItem item) => Experimentals.TryGetValue(item, out _);

    /// <summary>The words on the tag, the same everywhere: in every menu and in Settings.</summary>
    internal static string TagText => Localization.UiText.T("common.experimental");

    private const TextFormatFlags TagFormat = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>How big the tag is, for a size in pixels at 100% scaling turned into this display's.</summary>
    internal static Size TagSize(Func<int, int> unit)
    {
        Size text = TextRenderer.MeasureText(TagText, MenuStyle.SmallFont, Size.Empty, TagFormat);
        return new Size(text.Width + unit(12), text.Height + unit(4));
    }

    /// <summary>Draws the tag with its left edge at <paramref name="left"/>, centred on <paramref name="middle"/>.</summary>
    internal static void DrawTag(Graphics g, int left, int middle, Func<int, int> unit, bool enabled = true)
    {
        Size size = TagSize(unit);
        var area = new Rectangle(left, middle - size.Height / 2, size.Width, size.Height);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (System.Drawing.Drawing2D.GraphicsPath path = SettingsStyle.Rounded(area, unit(4)))
        using (var back = new SolidBrush(enabled ? MenuStyle.TagBack : MenuStyle.Hover))
            g.FillPath(back, path);
        g.SmoothingMode = smoothing;
        TextRenderer.DrawText(g, TagText, MenuStyle.SmallFont, area, enabled ? MenuStyle.TagText : MenuStyle.Disabled, TagFormat);
    }

    /// <summary>A quiet line naming the group of commands under it. It can't be chosen.</summary>
    internal static ToolStripLabel Header(string text) => new(text)
    {
        ForeColor = MenuStyle.Muted,
        Font = MenuStyle.MenuHeaderFont,
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
            TextRenderer.DrawText(g, _title, MenuStyle.MenuFont, top, text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _value + "%", MenuStyle.MenuFont, top, Enabled ? MenuStyle.Muted : MenuStyle.Disabled,
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
