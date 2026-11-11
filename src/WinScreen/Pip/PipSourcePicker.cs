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
        ClientSize = new Size(460, 420);
        MinimumSize = new Size(320, 280);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = MenuStyle.Font;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;
        Padding = new Padding(12, 12, 12, 10);
        _allowed = allowed;
        _windows = PipSourceList.Windows(allowed);
        _kind.Items.AddRange([T("pip.sources.window"), T("pip.sources.displays"), T("pip.sources.region"), T("pip.bookmarks.title")]);
        _kind.AccessibleName = T("pip.sources.kind");
        _kind.TabIndex = 0;
        _search.TabIndex = 1;
        _list.TabIndex = 2;
        _kind.SelectedIndex = 0;
        _kind.SelectedIndexChanged += (_, _) => Fill();

        _rowHeight.ImageSize = new Size(1, Unit(34));
        _list.SmallImageList = _rowHeight;
        _list.Columns.Add("", 420);
        DrawRows();
        _list.Groups.AddRange([_windowsGroup, _screensGroup, _bookmarksGroup]);
        _list.SelectedIndexChanged += (_, _) =>
        {
            _open.Enabled = Selected is not null || SelectedBookmark is not null;
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
        _search.AccessibleName = T("pip.sources.search");
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
            e.Handled = true;
            _list.Focus();
            _list.Items[0].Selected = true;
        };

        var refresh = new PlainButton { Text = T("pip.sources.refresh") };
        refresh.Click += (_, _) => RefreshSources();
        var preview = new PlainButton { Text = T("pip.sources.preview") };
        preview.Click += (_, _) => Preview();
        var region = new PlainButton { Text = T("pip.sources.selectRegion") };
        region.Click += (_, _) => AskForRegion();
        var remove = new PlainButton { Text = T("common.remove") };
        remove.Click += (_, _) =>
        {
            if (SelectedBookmark is not { } bookmark) return;
            BookmarkRemoved?.Invoke(bookmark.Name);
            Fill();
        };
        _open.Click += (_, _) => Choose();
        var cancel = new PlainButton { Text = T("common.cancel") };
        cancel.Click += (_, _) => Close();
        AcceptButton = _open;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0),
            BackColor = SettingsStyle.Page,
        };
        buttons.WrapContents = true;
        foreach (Button button in new Button[] { cancel, _open, preview, refresh, region, remove })
        {
            button.Margin = new Padding(6, 0, 0, 6);
            buttons.Controls.Add(button);
        }
        buttons.TabIndex = 3;

        // The kind of thing, the search box and the list, one under another, with the buttons below.
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = SettingsStyle.Page };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var search = new InputFrame(_search) { Glyph = MenuParts.Search, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_kind, 0, 0);
        layout.Controls.Add(search, 0, 1);
        layout.Controls.Add(new InputFrame(_list, InputFrame.Look.List) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        Fill();
        Shown += (_, _) => _search.Focus();
        ResumeLayout(true);
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

    /// <summary>
    /// Rows as Settings draws them: a wash under the pointer, the accent wash and bar when chosen, and the program's
    /// icon. A row is drawn whole whenever any part of it is asked for.
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
            var wash = new Rectangle(row.Left + Unit(2), row.Top + Unit(1), row.Width - Unit(4), row.Height - Unit(2));
            if (item.Selected || item.Index == _hot)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = SettingsStyle.Rounded(wash, Unit(4));
                using var brush = new SolidBrush(item.Selected ? SettingsStyle.RowSelectedOn(SettingsStyle.Card) : SettingsStyle.RowHoverOn(SettingsStyle.Card));
                g.FillPath(brush, path);
                if (item.Selected) SettingsStyle.SelectionBar(g, wash, _list);
                if (item.Selected && item.Focused && _list.Focused) SettingsStyle.FocusRing(g, wash, Unit(4), _list);
            }
            int left = row.Left + Unit(12), size = Unit(16);
            if (item.ImageKey is { Length: > 0 } key && _icons.Images.ContainsKey(key) && _icons.Images[key] is { } icon)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new Rectangle(left, row.Top + (row.Height - size) / 2, size, size));
            }
            left += size + Unit(10);
            TextRenderer.DrawText(g, item.Text, _list.Font, new Rectangle(left, row.Top, Math.Max(0, row.Right - left - Unit(8)), row.Height),
                SettingsStyle.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
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
        if (disposing) { _icons.Dispose(); _rowHeight.Dispose(); }
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
        _open.Enabled = false;
    }

    private ListViewItem Row(PipChoice choice, ListViewGroup group)
    {
        string note = choice.Minimized ? " " + T("pip.sources.minimized") : "";
        return new ListViewItem($"{choice.Title} ({choice.Program}){note}", group)
        {
            Tag = choice, ToolTipText = choice.Title, ImageKey = IconFor(choice),
        };
    }

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
            if (_kind.SelectedIndex == 2)
            {
                var source = new ScreenPipSource(device, null);
                Rectangle display = source.Screen.Bounds;
                using var dialog = new PlacementDialog(T("pip.sources.regionBounds"), new Rectangle(Point.Empty, display.Size));
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!Validate(choice) || source.Screen.Bounds != display) { RefreshSources(); return; }
                Rectangle region = Rectangle.Intersect(dialog.Frame, new Rectangle(Point.Empty, display.Size));
                if (region.Width <= 0 || region.Height <= 0) return;
                Close();
                RegionChosen?.Invoke(device, region);
                return;
            }
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
}
