using static WinScreen.Localization.UiText;
using WinScreen.Tray;
using WinScreen.Windows;
using WinScreen.Settings;

namespace WinScreen.Pip;

/// <summary>
/// Lets a person choose what a view shows without switching to it. It only exists while open. It is drawn as Settings
/// is: the same colours, boxes, list rows and buttons.
/// </summary>
internal sealed class PipSourcePicker : Form
{
    private readonly ThemedCombo _kind = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
    private readonly Func<nint, bool> _allowed;
    private WindowIdentity? _confirmedWindow;
    internal bool ChosenWindowMatches => _confirmedWindow?.Matches() == true;
    private readonly HintTextBox _search = new() { Hint = T("pip.sources.search") };
    private readonly ThemedTable _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, ShowGroups = false,
        HeaderStyle = ColumnHeaderStyle.None, HideSelection = false, ShowItemToolTips = true,
    };
    private int _hot = -1;

    private readonly ListViewGroup _windowsGroup = new(T("settings.windows"));
    private readonly ListViewGroup _screensGroup = new(T("pip.sources.displays"));
    private readonly ListViewGroup _bookmarksGroup = new(T("pip.bookmarks.title"));
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<PipSourceBookmark>>? Bookmarks { get; init; }
    private readonly PlainButton _open = new() { Text = T("common.open"), Primary = true, Enabled = false };
    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    /// <summary>Sets how tall the rows are; the icons are drawn from <see cref="_icons"/>.</summary>
    private readonly ImageList _rowHeight = new();
    private List<PipChoice> _windows;
    private List<PipChoice> _screens = PipSourceList.Screens();

    public PipSourcePicker(Func<nint, bool> allowed)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = T("pip.chooseSource");
        Icon = TrayApplication.LoadIcon();
        ClientSize = new Size(480, 460);
        MinimumSize = new Size(380, 320);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = MenuStyle.Font;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;
        Padding = new Padding(14, 12, 14, 12);
        _allowed = allowed;
        _windows = PipSourceList.Windows(allowed);
        // Choosing part of a display is one more kind of thing to show, not a button of its own.
        _kind.Items.AddRange([T("pip.sources.window"), T("pip.sources.displays"), T("pip.sources.region"), T("pip.bookmarks.title")]);
        _kind.AccessibleName = T("pip.sources.kind");
        _kind.SelectedIndex = 0;
        _kind.SelectedIndexChanged += (_, _) => { ShowActions(); Fill(); };

        _rowHeight.ImageSize = new Size(1, Unit(46));
        _list.SmallImageList = _rowHeight;
        _list.Columns.Add("", 420);
        DrawRows();
        _list.Groups.AddRange([_windowsGroup, _screensGroup, _bookmarksGroup]);
        _list.SelectedIndexChanged += (_, _) =>
        {
            _open.Enabled = Selected is not null || SelectedBookmark is not null;
            _exact.Enabled = Selected?.Device is not null;
            if (Selected is not { Window: not 0 } choice) return;
            try
            {
                if (choice.Identity?.IsSameWindow() != true || !_allowed(choice.Window)) { _open.Enabled = false; return; }
                _list.SelectedItems[0].Tag = choice with { Identity = WindowIdentity.Capture(choice.Window) };
            }
            catch (WindowOperationException) { _open.Enabled = false; }
        };
        _list.ItemActivate += (_, _) => Choose();
        _list.Resize += (_, _) => FitColumn();
        _list.ClientSizeChanged += (_, _) => FitColumn();
        // A saved source is forgotten from its own row: the button on it, or Delete.
        _list.MouseClick += (_, e) =>
        {
            if (_list.GetItemAt(e.X, e.Y) is { Tag: PipSourceBookmark bookmark } row && RemoveSpot(row.Bounds).Contains(e.Location)) Forget(bookmark);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || SelectedBookmark is not { } bookmark) return;
            e.Handled = true;
            Forget(bookmark);
        };
        _search.AccessibleName = T("pip.sources.search");
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
            e.Handled = true;
            _list.Focus();
            _list.Items[0].Selected = true;
        };

        // Reading the list again belongs with the search box; F5 does it too.
        var refresh = new PlainButton { Glyph = MenuParts.Sync, AccessibleName = T("pip.sources.refresh"), Margin = new Padding(8, 0, 0, 0) };
        refresh.Click += (_, _) => RefreshSources();
        _tips.SetToolTip(refresh, T("pip.sources.refresh"));
        _preview.Click += (_, _) => Preview();
        _exact.Click += (_, _) => ChooseExactArea();
        _open.Click += (_, _) => Choose();
        var cancel = new PlainButton { Text = T("common.cancel") };
        cancel.Click += (_, _) => Close();
        AcceptButton = _open;
        CancelButton = cancel;

        // How many there are on the left; on the right what to do with the one chosen, the main action last.
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Anchor = AnchorStyles.Right, Margin = Padding.Empty, BackColor = SettingsStyle.Page,
        };
        foreach (Button button in new Button[] { _open, cancel, _preview, _exact })
        {
            button.Margin = new Padding(8, 0, 0, 0);
            actions.Controls.Add(button);
        }
        // Laid out from the right; the keyboard still goes through them left to right.
        (_exact.TabIndex, _preview.TabIndex, cancel.TabIndex, _open.TabIndex) = (0, 1, 2, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0), BackColor = SettingsStyle.Page };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _count.Anchor = AnchorStyles.Left;
        footer.Controls.Add(_count, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 8), BackColor = SettingsStyle.Page };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(new InputFrame(_search) { Glyph = MenuParts.Search, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty }, 0, 0);
        searchRow.Controls.Add(refresh, 1, 0);

        // The kind of thing, the search box and the list, one under another, with the actions below.
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = SettingsStyle.Page };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_kind, 0, 0);
        layout.Controls.Add(searchRow, 0, 1);
        layout.Controls.Add(new InputFrame(_list, InputFrame.Look.List) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        ShowActions();
        Fill();
        Shown += (_, _) => _search.Focus();
        ResumeLayout(true);
    }

    private readonly PlainButton _preview = new() { Text = T("pip.sources.preview") };
    private readonly PlainButton _exact = new() { Text = T("pip.sources.exactArea"), Enabled = false };
    private readonly SoftLabel _count = new() { AutoSize = true, ForeColor = SettingsStyle.Secondary, Margin = Padding.Empty };
    private readonly ToolTip _tips = new();

    private bool ChoosingRegion => _kind.SelectedIndex == 2;

    /// <summary>
    /// What the buttons offer for this kind of thing. Part of a display is dragged out on it, or typed in as exact
    /// numbers; anything else opens as it is, after a look at it if wanted.
    /// </summary>
    private void ShowActions()
    {
        _open.Text = ChoosingRegion ? T("pip.sources.selectRegion") : T("common.open");
        _exact.Visible = ChoosingRegion;
        _preview.Visible = !ChoosingRegion;
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    /// <summary>The title bar in the window's own colours, as Settings has it.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
        int dark = MenuStyle.Dark ? 1 : 0, caption = ColorRef(SettingsStyle.Page), text = ColorRef(SettingsStyle.Text);
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, in dark, sizeof(int));
        // Windows 11 only; Windows 10 refuses these and keeps its own caption colour.
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }

    /// <summary>The one column as wide as what shows: Windows' own scroll bar is hidden past the list's edge.</summary>
    private void FitColumn()
    {
        if (_list.Columns.Count == 0 || _list.Parent is not { } shown) return;
        int width = Math.Max(Unit(100), Math.Min(_list.ClientSize.Width, shown.ClientSize.Width) - Unit(ThinScrollBar.BarWidth));
        if (_list.Columns[0].Width != width) _list.Columns[0].Width = width;
    }

    /// <summary>The button on a saved source's row that forgets it.</summary>
    private Rectangle RemoveSpot(Rectangle row)
    {
        int size = Unit(28);
        return new Rectangle(row.Right - Unit(10) - size, row.Top + (row.Height - size) / 2, size, size);
    }

    private void Forget(PipSourceBookmark bookmark)
    {
        BookmarkRemoved?.Invoke(bookmark.Name);
        Fill();
    }

    /// <summary>The line under a row's name: the program, the display, or where a saved source was.</summary>
    private static string Detail(object? tag) => tag switch
    {
        PipChoice { Device: null } window => window.Minimized ? window.Program + " " + T("pip.sources.minimized") : window.Program,
        PipChoice display => display.Program,
        PipSourceBookmark bookmark => bookmark.Device,
        _ => "",
    };

    /// <summary>
    /// Rows as Settings draws them: a wash under the pointer, the accent wash and bar when chosen; the program's icon,
    /// the name, and under it in smaller words what it is. A row is drawn whole whenever any part of it is asked for.
    /// </summary>
    private void DrawRows()
    {
        _list.OwnerDraw = true;
        _list.BackColor = SettingsStyle.Card;
        _list.ForeColor = SettingsStyle.Text;
        _list.Font = MenuStyle.Font;
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, _) => { };
        _list.DrawItem += (_, e) =>
        {
            if (e.Item is not { } item) return;
            Graphics g = e.Graphics;
            SettingsStyle.SoftText(g);
            Rectangle row = item.Bounds;
            using (var back = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(back, row);
            var wash = new Rectangle(row.Left + Unit(4), row.Top + Unit(2), row.Width - Unit(8), row.Height - Unit(4));
            bool hot = item.Index == _hot;
            if (item.Selected || hot)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = SettingsStyle.Rounded(wash, Unit(4));
                using var brush = new SolidBrush(item.Selected ? SettingsStyle.RowSelectedOn(SettingsStyle.Card) : SettingsStyle.RowHoverOn(SettingsStyle.Card));
                g.FillPath(brush, path);
                if (item.Selected) SettingsStyle.SelectionBar(g, wash, _list);
                if (item.Selected && item.Focused && _list.Focused) SettingsStyle.FocusRing(g, wash, Unit(4), _list);
            }
            int left = row.Left + Unit(16), size = Unit(20);
            Image? icon = item.ImageKey is { Length: > 0 } key && _icons.Images.ContainsKey(key) ? _icons.Images[key]
                : MenuParts.Icon(item.Tag switch
                {
                    PipSourceBookmark => MenuParts.Star,
                    PipChoice { Device: not null } => MenuParts.Monitor,
                    _ => MenuParts.Page,
                }, SettingsStyle.Secondary);
            if (icon is not null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new Rectangle(left, row.Top + (row.Height - size) / 2, size, size));
            }
            left += size + Unit(12);
            int right = row.Right - Unit(12);
            if (item.Tag is PipSourceBookmark && (item.Selected || hot))
            {
                Rectangle remove = RemoveSpot(row);
                if (MenuParts.Icon(MenuParts.Close, SettingsStyle.Secondary) is { } close)
                    g.DrawImage(close, new Rectangle(remove.Left + (remove.Width - Unit(14)) / 2, remove.Top + (remove.Height - Unit(14)) / 2, Unit(14), Unit(14)));
                right = remove.Left - Unit(4);
            }
            const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int nameHeight = TextRenderer.MeasureText(g, "Ag", _list.Font, Size.Empty, line).Height;
            int detailHeight = TextRenderer.MeasureText(g, "Ag", MenuStyle.SmallFont, Size.Empty, line).Height;
            int top = row.Top + (row.Height - nameHeight - Unit(2) - detailHeight) / 2;
            int width = Math.Max(0, right - left);
            TextRenderer.DrawText(g, item.Text, _list.Font, new Rectangle(left, top, width, nameHeight), SettingsStyle.Text, line);
            TextRenderer.DrawText(g, Detail(item.Tag), MenuStyle.SmallFont, new Rectangle(left, top + nameHeight + Unit(2), width, detailHeight),
                SettingsStyle.Secondary, line);
        };
        _list.MouseMove += (_, e) => Hover(_list.GetItemAt(e.X, e.Y)?.Index ?? -1);
        _list.MouseLeave += (_, _) => Hover(-1);
    }

    private void Hover(int row)
    {
        if (row == _hot) return;
        foreach (int index in new[] { _hot, row })
            if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.Items[index].Bounds);
        _hot = row;
    }

    public event Action<nint>? WindowChosen;
    public event Action<string>? ScreenChosen;
    public event Action<string, Rectangle>? RegionChosen;
    public event Action<PipSourceBookmark>? BookmarkChosen;
    public event Action<PipSourceBookmark>? BookmarkPreviewRequested;
    public event Action<string>? BookmarkRemoved;

    /// <summary>The person wants to drag out a region of this display.</summary>
    public event Action<string>? RegionWanted;

    private PipChoice? Selected => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as PipChoice;
    private PipSourceBookmark? SelectedBookmark => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as PipSourceBookmark;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icons.Dispose(); _rowHeight.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
        else if (e.KeyCode == Keys.F5)
        {
            e.SuppressKeyPress = true;
            RefreshSources();
        }
        else if (e.KeyCode == Keys.Enter && _list.ContainsFocus)
        {
            e.SuppressKeyPress = true;
            if (Selected is null && _list.Items.Count > 0)
                _list.Items[0].Selected = true;
            Choose();
        }
    }

    /// <summary>Opens the selection before closing, while WinScreen still has the focus Windows wants to see.</summary>
    internal void AskForRegion()
    {
        RegionWanted?.Invoke(Selected?.Device ?? Screen.FromPoint(Cursor.Position).DeviceName);
        Close();
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _hot = -1;
        IEnumerable<PipChoice> choices = _kind.SelectedIndex == 0 ? _windows : _kind.SelectedIndex == 3 ? [] : _screens;
        foreach (PipChoice choice in choices.Where(choice => PipSourceList.Matches(choice, _search.Text)))
            _list.Items.Add(Row(choice, _kind.SelectedIndex == 0 ? _windowsGroup : _screensGroup));
        if (_kind.SelectedIndex == 3)
            foreach (PipSourceBookmark bookmark in (Bookmarks?.Invoke() ?? []).Where(bookmark =>
                bookmark.Name.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase)))
                _list.Items.Add(new ListViewItem(bookmark.Name, _bookmarksGroup) { Tag = bookmark, ToolTipText = bookmark.Device });
        _list.EndUpdate();
        _open.Enabled = _exact.Enabled = false;
        int count = _list.Items.Count;
        // With one display there is nothing to choose between.
        if (count == 1 && _kind.SelectedIndex is 1 or 2) _list.Items[0].Selected = true;
        _count.Text = _kind.SelectedIndex switch
        {
            0 => count == 1 ? T("pip.sources.count.oneWindow") : string.Format(T("pip.sources.count.windows"), count),
            3 => count == 1 ? T("pip.sources.count.oneSaved") : string.Format(T("pip.sources.count.saved"), count),
            _ => count == 1 ? T("pip.sources.count.oneDisplay") : string.Format(T("pip.sources.count.displays"), count),
        };
    }

    private ListViewItem Row(PipChoice choice, ListViewGroup group) => new(choice.Title, group)
    {
        Tag = choice, ToolTipText = choice.Title, ImageKey = IconFor(choice),
    };

    private string IconFor(PipChoice choice)
    {
        if (choice.Window == 0 || _icons.Images.ContainsKey(choice.Program))
            return choice.Program;
        try
        {
            string path = WindowInfo.ExecutablePath(choice.Window);
            if (path.Length > 0 && Icon.ExtractAssociatedIcon(path) is { } icon)
            {
                using (icon) _icons.Images.Add(choice.Program, icon.ToBitmap());
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
                                      System.Runtime.InteropServices.ExternalException)
        {
            // A row without an icon is still a row.
        }

        return choice.Program;
    }

    internal void RefreshSources()
    {
        PipChoice? selected = Selected;
        _windows = PipSourceList.Windows(_allowed);
        _screens = PipSourceList.Screens();
        Fill();
        foreach (ListViewItem row in _list.Items)
            if (row.Tag is PipChoice choice && choice.Window == selected?.Window && choice.Device == selected?.Device)
                row.Selected = true;
    }

    private bool Validate(PipChoice choice)
    {
        bool valid = choice.Device is { } device
            ? new ScreenPipSource(device, null).IsAvailable
            : choice.Identity?.Matches() == true && _allowed(choice.Window);
        if (valid) return true;
        MessageBox.Show(this, T("errors.window.targetUnavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshSources();
        return false;
    }

    private void Preview()
    {
        if (SelectedBookmark is { } bookmark) { BookmarkPreviewRequested?.Invoke(bookmark); return; }
        if (Selected is not { } choice || !Validate(choice)) return;
        try
        {
            using var preview = new Form { Text = T("pip.sources.preview") + " - " + choice.Title,
                Size = new Size(640, 420), StartPosition = FormStartPosition.CenterParent, TopMost = true };
            if (choice.Device is { } device)
            {
                var source = new ScreenPipSource(device, null);
                source.Require();
                Rectangle bounds = source.Screen.Bounds;
                using var picture = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics graphics = Graphics.FromImage(picture))
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                preview.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = picture, SizeMode = PictureBoxSizeMode.Zoom });
                preview.ShowDialog(this);
            }
            else
            {
                if (Native.User32.IsIconic(choice.Window))
                {
                    MessageBox.Show(this, T("pip.sources.previewMinimized"), Text);
                    return;
                }
                WindowIdentity.Capture(choice.Window).Require();
                using var thumbnail = DwmThumbnail.TryCreate(preview.Handle, choice.Window);
                if (thumbnail is null) return;
                void Update() => thumbnail.Show(preview.ClientRectangle, WindowInfo.VisiblePart(choice.Window, thumbnail.SourceSize));
                preview.Resize += (_, _) => Update();
                Update();
                preview.ShowDialog(this);
            }
        }
        catch (Exception failure) when (failure is WindowOperationException or System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, failure.Message, Text);
        }
    }

    private void Choose()
    {
        if (SelectedBookmark is { } bookmark)
        {
            Close();
            BookmarkChosen?.Invoke(bookmark);
            return;
        }
        if (Selected is not { } choice || !Validate(choice)) return;
        if (choice.Device is { } device)
        {
            // Part of a display is dragged out on the display itself.
            if (ChoosingRegion) { AskForRegion(); return; }
            Close();
            ScreenChosen?.Invoke(device);
        }
        else
        {
            _confirmedWindow = choice.Identity;
            Close();
            WindowChosen?.Invoke(choice.Window);
        }
    }

    /// <summary>Part of the chosen display, typed in as numbers instead of dragged out.</summary>
    private void ChooseExactArea()
    {
        if (Selected is not { Device: { } device } choice || !Validate(choice)) return;
        var source = new ScreenPipSource(device, null);
        Rectangle display = source.Screen.Bounds;
        using var dialog = new PlacementDialog(T("pip.sources.regionBounds"), new Rectangle(Point.Empty, display.Size));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Validate(choice) || source.Screen.Bounds != display) { RefreshSources(); return; }
        Rectangle region = Rectangle.Intersect(dialog.Frame, new Rectangle(Point.Empty, display.Size));
        if (region.Width <= 0 || region.Height <= 0) return;
        Close();
        RegionChosen?.Invoke(device, region);
    }
}
