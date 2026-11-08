using static WinScreen.Localization.UiText;
using WinScreen.Tray;
using WinScreen.Windows;
using WinScreen.Settings;

namespace WinScreen.Pip;

/// <summary>Lets a person choose what a view shows without switching to it. It only exists while open.</summary>
internal sealed class PipSourcePicker : Form
{
    private readonly ComboBox _kind = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Func<nint, bool> _allowed;
    private WindowIdentity? _confirmedWindow;
    internal bool ChosenWindowMatches => _confirmedWindow?.Matches() == true;
    private readonly TextBox _search = new() { Dock = DockStyle.Top };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.None, HideSelection = false, ShowItemToolTips = true,
    };

    private readonly ListViewGroup _windowsGroup = new(T("settings.windows"));
    private readonly ListViewGroup _screensGroup = new(T("pip.sources.displays"));
    private readonly ListViewGroup _bookmarksGroup = new(T("pip.bookmarks.title"));
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<PipSourceBookmark>>? Bookmarks { get; init; }
    private readonly Button _open = new() { Text = T("common.open"), AutoSize = true, Enabled = false };
    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
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
        _allowed = allowed;
        _windows = PipSourceList.Windows(allowed);
        _kind.Items.AddRange([T("pip.sources.window"), T("pip.sources.displays"), T("pip.sources.region"), T("pip.bookmarks.title")]);
        _kind.AccessibleName = T("pip.sources.kind");
        _kind.TabIndex = 0;
        _search.TabIndex = 1;
        _list.TabIndex = 2;
        _kind.SelectedIndex = 0;
        _kind.SelectedIndexChanged += (_, _) => Fill();

        Native.User32.SendTextMessage(_search.Handle, EM_SETCUEBANNER, 1, T("pip.sources.search"));
        _list.SmallImageList = _icons;
        _list.Columns.Add("", 420);
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
        _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - 4);
        _search.AccessibleName = T("pip.sources.search");
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
            e.Handled = true;
            _list.Focus();
            _list.Items[0].Selected = true;
        };

        var refresh = new Button { Text = T("pip.sources.refresh"), AutoSize = true };
        refresh.Click += (_, _) => RefreshSources();
        var preview = new Button { Text = T("pip.sources.preview"), AutoSize = true };
        preview.Click += (_, _) => Preview();
        var region = new Button { Text = T("pip.sources.selectRegion"), AutoSize = true };
        region.Click += (_, _) => AskForRegion();
        var remove = new Button { Text = T("common.remove"), AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (SelectedBookmark is not { } bookmark) return;
            BookmarkRemoved?.Invoke(bookmark.Name);
            Fill();
        };
        _open.Click += (_, _) => Choose();
        var cancel = new Button { Text = T("common.cancel"), AutoSize = true };
        cancel.Click += (_, _) => Close();
        AcceptButton = _open;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4),
        };
        buttons.WrapContents = true;
        buttons.Controls.AddRange([cancel, _open, preview, refresh, region, remove]);
        buttons.TabIndex = 3;

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(_kind);
        Controls.Add(buttons);
        Fill();
        Shown += (_, _) => _search.Focus();
        ResumeLayout(true);
    }

    private const int EM_SETCUEBANNER = 0x1501;

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
        if (disposing) _icons.Dispose();
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
