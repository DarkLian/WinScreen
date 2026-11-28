using static WinScreen.Localization.UiText;
using WinScreen.Tray;

namespace WinScreen.Settings;

// Actions and shortcuts: every action in the groups the menus use, its shortcut changed right in its row, and the ways
// to act with the mouse, whose switches are on their own pages.
public sealed partial class SettingsWindow
{
    internal const int NameColumn = 0, ShortcutColumn = 1, StateColumn = 2, FeatureColumnCount = 3;

    internal enum ActionFilter { All, WithShortcut, Changed, NotWorking }

    internal sealed record FeatureHeading(string Text);

    private static readonly IReadOnlyDictionary<AppCommand, string> DefaultHotkeys = new AppSettings().Hotkeys;

    private readonly ComboBox _actionFilter = new ThemedCombo { Width = 190 };
    private readonly CheckBox _shortcutSheet = new SwitchBox { Text = T("actions.sheet"), AutoSize = true };
    private readonly PlainButton _pressKeys = new() { AutoSize = true, Text = T("actions.pressKeys") };
    private readonly PlainButton _changeShortcut = new() { AutoSize = true, Text = T("actions.change.shortcut") };
    private readonly PlainButton _clearShortcut = new() { AutoSize = true, Text = T("actions.clear") };
    private readonly PlainButton _resetShortcut = new() { AutoSize = true, Text = T("actions.reset") };
    private readonly PlainButton _goToSetting = new() { AutoSize = true, Text = T("actions.goToSetting") };
    private readonly PlainButton _resetAll = new() { AutoSize = true, Text = T("actions.resetAll") };
    private readonly Label _alsoIn = new SoftLabel { AutoSize = true, UseMnemonic = false, ForeColor = SettingsStyle.Secondary };
    private readonly Label _featuresNote = new SoftLabel { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false, ForeColor = SettingsStyle.Secondary, Padding = new Padding(1, 8, 0, 8) };
    private AppCommand? _editing;
    private string _editingWas = "";
    private bool _listeningForSearch;
    private int _lastFeatureRow;
    private Action<string>? _goToPage;

    private int FeatureUnit(int value) => SettingsStyle.Unit(_features, value);

    private Control BuildFeatures()
    {
        _features.HideSelection = false;
        _features.Columns.Add(T("features.name"), 160);
        _features.Columns.Add(T("actions.shortcut"), 150);
        _features.Columns.Add(T("features.state"), 160);
        foreach (AppCommand command in CommandCatalog.All)
        {
            var box = new TextBox { ReadOnly = true, ShortcutsEnabled = false, AccessibleName = CommandCatalog.Name(command) };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Tab) return;
                e.SuppressKeyPress = true;
                if (_editing == command && e.Modifiers == Keys.None && e.KeyCode is Keys.Escape or Keys.Enter)
                {
                    EndShortcutEdit(cancel: e.KeyCode == Keys.Escape);
                    return;
                }
                string before = box.Text;
                CaptureHotkey(command, e.KeyData, HotkeyText.WinKeyDown);
                // Keys that made a shortcut, or Delete, are the whole change: the row shows it again.
                if (_editing == command && box.Text != before) EndShortcutEdit(cancel: false);
            };
            box.Leave += (_, _) => BeginInvoke(() =>
            {
                if (_editing == command && !box.Focused) EndShortcutEdit(cancel: false);
            });
            _hotkeys[command] = box;
        }
        _shortcutEditor.Visible = false;
        _features.Controls.Add(_shortcutEditor);
        _features.Moved += PlaceShortcutEditor;
        _features.MouseClick += (_, e) => ClickFeature(e);
        _features.KeyDown += (_, e) =>
        {
            if (SelectedFeature() is not AppCommand command) return;
            if (e.KeyCode is Keys.Delete or Keys.Back && e.Modifiers == Keys.None) { _hotkeys[command].Text = ""; e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { BeginShortcutEdit(command); e.Handled = true; }
        };
        _features.SelectedIndexChanged += (_, _) => SkipHeadings();

        _changeShortcut.Click += (_, _) => { if (SelectedFeature() is AppCommand command) BeginShortcutEdit(command); };
        _clearShortcut.Click += (_, _) => { if (SelectedFeature() is AppCommand command) _hotkeys[command].Text = ""; };
        _resetShortcut.Click += (_, _) => { if (SelectedFeature() is AppCommand command) ResetShortcut(command); };
        _goToSetting.Click += (_, _) => { if (SelectedFeature() is string page) _goToPage?.Invoke(page); };
        _resetAll.Click += (_, _) =>
        {
            if (ThemedMessage.Show(this, T("actions.resetAll.ask"), T("actions.resetAll"), okCancel: true) == DialogResult.OK)
                ResetAllShortcuts();
        };

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 3, BackColor = SettingsStyle.Card };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _shortcutLabel.Margin = new Padding(2, 0, 8, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right, BackColor = SettingsStyle.Card };
        foreach (PlainButton button in new[] { _changeShortcut, _clearShortcut, _resetShortcut, _goToSetting })
        {
            button.Margin = new Padding(6, 0, 0, 0);
            buttons.Controls.Add(button);
        }
        details.Controls.Add(_shortcutLabel, 0, 0);
        details.Controls.Add(buttons, 1, 0);
        _alsoIn.Margin = new Padding(2, 6, 0, 0);
        details.Controls.Add(_alsoIn, 0, 1);
        details.SetColumnSpan(_alsoIn, 2);
        Label help = Note(T("settings.hotkeys.captureHelp"));
        help.Margin = new Padding(2, 10, 8, 0);
        help.Anchor = AnchorStyles.Left;
        details.Controls.Add(help, 0, 2);
        _resetAll.Margin = new Padding(6, 10, 0, 0);
        _resetAll.Anchor = AnchorStyles.Right;
        details.Controls.Add(_resetAll, 1, 2);
        var card = new CardPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 10) };
        card.Controls.Add(details);

        _actionSearch.AccessibleName = T("actions.search");
        _actionSearch.Hint = T("actions.search");
        _actionSearch.TextChanged += (_, _) => RefreshFeatures();
        _actionSearch.KeyDown += (_, e) => ListenForSearch(e);
        _actionSearch.Leave += (_, _) => StopListening();
        _pressKeys.Click += (_, _) =>
        {
            _listeningForSearch = true;
            _actionSearch.Text = "";
            _actionSearch.Hint = T("actions.pressKeys.listening");
            _actionSearch.Focus();
            _actionSearch.Invalidate();
        };
        _actionFilter.Items.AddRange([T("actions.filter.all"), T("actions.filter.shortcut"), T("actions.filter.changed"), T("actions.filter.problems")]);
        _actionFilter.SelectedIndex = 0;
        _actionFilter.AccessibleName = T("actions.filter");
        _actionFilter.SelectedIndexChanged += (_, _) => RefreshFeatures();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var searchFrame = new InputFrame(_actionSearch) { Glyph = MenuParts.Search, Dock = DockStyle.Fill, Height = BoxHeight.Standard, Margin = Padding.Empty };
        top.Controls.Add(searchFrame, 0, 0);
        _pressKeys.Anchor = AnchorStyles.Left;
        _pressKeys.Margin = new Padding(8, 0, 0, 0);
        top.Controls.Add(_pressKeys, 1, 0);
        _actionFilter.Anchor = AnchorStyles.Left;
        _actionFilter.Margin = new Padding(8, 0, 0, 0);
        top.Controls.Add(_actionFilter, 2, 0);

        DrawFeatures();
        var table = new InputFrame(_features, InputFrame.Look.List) { Dock = DockStyle.Fill };
        var page = new ColumnPanel { Dock = DockStyle.Fill };
        page.Controls.Add(table);
        page.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        page.Controls.Add(card);
        // A settings card's insets: its 8 plus a switch's own margin of 3 at the sides.
        var sheet = new CardPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(11, 8, 11, 8) };
        _shortcutSheet.Dock = DockStyle.Top;
        sheet.Controls.Add(_shortcutSheet);
        page.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        page.Controls.Add(sheet);
        _featuresNote.Text = T("features.help");
        page.Controls.Add(_featuresNote);
        page.Controls.Add(top);
        page.Layout += (_, _) =>
        {
            var most = new Size(Math.Max(1, page.ClientSize.Width - page.Padding.Horizontal), 0);
            if (_featuresNote.MaximumSize != most) _featuresNote.MaximumSize = most;
            var room = new Size(Math.Max(1, details.ClientSize.Width - 8), 0);
            if (_alsoIn.MaximumSize != room) _alsoIn.MaximumSize = room;
            var beside = new Size(Math.Max(1, details.ClientSize.Width - _resetAll.Width - SettingsStyle.Unit(details, 24)), 0);
            if (help.MaximumSize != beside) help.MaximumSize = beside;
            // Its switch at the card's far edge, as every other setting's is.
            int extra = Math.Max(0, sheet.ClientSize.Width - sheet.Padding.Horizontal - SettingsStyle.Unit(sheet, SwitchBox.RowWidth));
            if (_shortcutSheet is SwitchBox option && option.Extra != extra) option.Extra = extra;
        };
        _features.Resize += (_, _) => FitFeatureColumns();
        _features.ClientSizeChanged += (_, _) => FitFeatureColumns();
        table.Resize += (_, _) => FitFeatureColumns();
        return page;
    }

    private object? SelectedFeature() => _features.SelectedItems.Count > 0 ? _features.SelectedItems[0].Tag : null;

    private ListViewItem? RowOf(object tag) => _features.Items.Cast<ListViewItem>().FirstOrDefault(row => Equals(row.Tag, tag));

    // A heading is never the chosen row: the choice moves on past it, the way the arrow keys were going.
    private void SkipHeadings()
    {
        if (_features.SelectedIndices.Count == 0) { ShowFeatureDetails(); return; }
        int at = _features.SelectedIndices[0];
        if (_features.Items[at].Tag is FeatureHeading)
        {
            int step = at >= _lastFeatureRow ? 1 : -1;
            int next = at + step;
            while (next >= 0 && next < _features.Items.Count && _features.Items[next].Tag is FeatureHeading) next += step;
            if (next < 0 || next >= _features.Items.Count)
            {
                next = at - step;
                while (next >= 0 && next < _features.Items.Count && _features.Items[next].Tag is FeatureHeading) next -= step;
            }
            if (next >= 0 && next < _features.Items.Count)
            {
                _features.Items[next].Selected = true;
                _features.Items[next].Focused = true;
                _features.Items[next].EnsureVisible();
            }
            return;
        }
        _lastFeatureRow = at;
        ShowFeatureDetails();
    }

    private void ClickFeature(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ListViewHitTestInfo hit = _features.HitTest(e.Location);
        if (hit.Item?.Tag is not AppCommand command || hit.SubItem is null) return;
        if (hit.Item.SubItems.IndexOf(hit.SubItem) != ShortcutColumn) return;
        if (ResetGlyph(hit.Item) is { } glyph && glyph.Contains(e.Location)) ResetShortcut(command);
        else BeginShortcutEdit(command);
    }

    internal bool ShortcutChanged(AppCommand command) =>
        _hotkeys.TryGetValue(command, out TextBox? box) && box.Text != DefaultHotkeys.GetValueOrDefault(command, "");

    internal void ResetShortcut(AppCommand command) => _hotkeys[command].Text = DefaultHotkeys.GetValueOrDefault(command, "");

    internal void ResetAllShortcuts()
    {
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            box.Text = DefaultHotkeys.GetValueOrDefault(command, "");
    }

    internal AppCommand? EditingShortcut => _editing;

    internal void BeginShortcutEdit(AppCommand command)
    {
        if (_editing is { } other && other != command) EndShortcutEdit(cancel: false);
        if (RowOf(command) is not { } row) return;
        row.Selected = true;
        row.Focused = true;
        row.EnsureVisible();
        if (_editing != command)
        {
            _editing = command;
            _editingWas = _hotkeys[command].Text;
            _shortcutEditor.Host(_hotkeys[command]);
        }
        _shortcutEditor.Visible = true;
        PlaceShortcutEditor();
        _hotkeys[command].Focus();
        _hotkeys[command].SelectAll();
    }

    internal void EndShortcutEdit(bool cancel)
    {
        if (_editing is not { } command) return;
        _editing = null;
        if (cancel) _hotkeys[command].Text = _editingWas;
        bool focused = _shortcutEditor.ContainsFocus;
        _shortcutEditor.Visible = false;
        _shortcutEditor.Host(null);
        if (focused && _features.CanFocus) _features.Focus();
        _features.Invalidate();
        ShowFeatureDetails();
    }

    // The box sits over its row's shortcut, and follows it as the table scrolls; out of sight, the change is done.
    private void PlaceShortcutEditor()
    {
        if (_editing is not { } command || !_shortcutEditor.Visible) return;
        if (RowOf(command) is not { } row) { EndShortcutEdit(cancel: false); return; }
        Rectangle cell = row.SubItems[ShortcutColumn].Bounds;
        int top = _features.TopItem?.Bounds.Top ?? 0;
        if (cell.Top < top || cell.Bottom > _features.ClientSize.Height)
        {
            if (!_shortcutEditor.ContainsFocus) EndShortcutEdit(cancel: false);
            else _shortcutEditor.Visible = cell.Bottom > top && cell.Top < _features.ClientSize.Height;
            return;
        }
        int height = Math.Min(cell.Height - FeatureUnit(2), Math.Max(FeatureUnit(28), _shortcutEditor.Height));
        var bounds = new Rectangle(cell.Left + FeatureUnit(4), cell.Top + (cell.Height - height) / 2,
            Math.Max(FeatureUnit(120), cell.Width - FeatureUnit(8)), height);
        if (_shortcutEditor.Bounds != bounds) _shortcutEditor.Bounds = bounds;
    }

    private void ListenForSearch(KeyEventArgs e)
    {
        if (!_listeningForSearch) return;
        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) { e.SuppressKeyPress = true; return; }
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (key == Keys.Escape && e.Modifiers == Keys.None) { StopListening(); return; }
        bool win = HotkeyText.WinKeyDown;
        if (!win && (e.KeyData & (Keys.Control | Keys.Alt)) == 0 && !HotkeyText.IsFunctionKey(key)) return;
        StopListening();
        _actionSearch.Text = HotkeyText.Format(e.KeyData, win);
        _actionSearch.SelectionStart = _actionSearch.Text.Length;
    }

    private void StopListening()
    {
        if (!_listeningForSearch) return;
        _listeningForSearch = false;
        _actionSearch.Hint = T("actions.search");
        _actionSearch.Invalidate();
    }

    internal void ListenForShortcutSearch() => _pressKeys.PerformClick();

    // Searching by keys: the shortcut pressed, compared as keys, so Ctrl+Alt+T never finds Ctrl+Alt+Shift+T.
    private static bool SameKeys(string shortcut, uint modifiers, Keys key) =>
        HotkeyText.TryParse(shortcut, out uint theirs, out Keys theirKey) && theirs == modifiers && theirKey == key;

    private bool FreeToUse(uint modifiers, Keys key)
    {
        const int Probe = 0xBFF0;
        if (!IsHandleCreated) return true;
        if (!Native.User32.RegisterHotKey(Handle, Probe, modifiers | Native.User32.MOD_NOREPEAT, (uint)key)) return false;
        Native.User32.UnregisterHotKey(Handle, Probe);
        return true;
    }

    private string FeatureState(AppCommand command, string shortcut) =>
        _shortcutProblems.GetValueOrDefault(command) ??
        (CommandCatalog.IsExperimental(command) && !_experimental.Checked ? T("actions.state.experimental") :
         shortcut.Length == 0 ? T("features.unassigned") :
         _registered?.Invoke(command) == true ? T("actions.registered") : T("actions.unavailable"));

    private bool NotWorking(AppCommand command, string shortcut) =>
        _shortcutProblems.ContainsKey(command) || shortcut.Length > 0 && _registered?.Invoke(command) == false;

    internal static string GroupName(ActionGroup group) => group switch
    {
        ActionGroup.Window => T("actions.group.window"),
        ActionGroup.Place => T("actions.group.place"),
        ActionGroup.Effects => T("actions.group.effects"),
        ActionGroup.Views => T("actions.group.views"),
        _ => T("actions.group.everywhere"),
    };

    private string WindowMenuGestureText() => _windowMenuGesture.SelectedIndex switch
    {
        (int)WindowMenuGesture.CtrlShiftRightClick => T("gestures.menu.ctrlShiftRight"),
        (int)WindowMenuGesture.CtrlMiddleClick => T("gestures.menu.ctrlMiddle"),
        _ => T("gestures.menu.ctrlRight"),
    };

    internal string AlsoIn(AppCommand command)
    {
        var places = new List<string>();
        if (CommandCatalog.InWindowMenu(command)) places.Add(T("actions.place.windowMenu"));
        if (CommandCatalog.ForView(command)) places.Add(T("actions.place.viewMenu"));
        if (CommandCatalog.InTrayMenu(command)) places.Add(T("actions.place.tray"));
        if (_showToolbar.Checked)
        {
            HashSet<AppCommand> bar = _toolbarButtons.CheckedItems.Cast<ToolbarChoice>().Select(choice => choice.Command).ToHashSet();
            if (bar.Contains(command) ||
                command is AppCommand.OpacityUp or AppCommand.OpacityDown && bar.Contains(AppCommand.ToggleDefaultOpacity) ||
                CommandCatalog.ArrangeMenu.Contains(command) && bar.Contains(AppCommand.Arrange))
                places.Add(T("actions.place.toolbar"));
        }
        if (command == AppCommand.ShowWindowMenu && _windowMenu.Checked) places.Add(WindowMenuGestureText());
        if (command is AppCommand.OpacityUp or AppCommand.OpacityDown && _opacityWheel.Checked) places.Add(T("features.wheelShortcut"));
        return places.Count == 0 ? T("actions.alsoIn.none")
            : string.Format(T("actions.alsoIn"), string.Join(T("actions.alsoIn.separator"), places));
    }

    private void ShowFeatureDetails()
    {
        object? tag = SelectedFeature();
        bool action = tag is AppCommand;
        _changeShortcut.Visible = _clearShortcut.Visible = _resetShortcut.Visible = action;
        _goToSetting.Visible = tag is string;
        _resetAll.Enabled = _hotkeys.Keys.Any(ShortcutChanged);
        switch (tag)
        {
            case AppCommand command:
                _shortcutLabel.Text = CommandCatalog.Name(command).Replace("&", "");
                _clearShortcut.Enabled = _hotkeys[command].Text.Length > 0;
                _resetShortcut.Enabled = ShortcutChanged(command);
                _alsoIn.Text = AlsoIn(command);
                break;
            case string:
                _shortcutLabel.Text = _features.SelectedItems[0].Text;
                _alsoIn.Text = T("actions.mouse.help");
                break;
            default:
                _shortcutLabel.Text = "";
                _alsoIn.Text = "";
                break;
        }
    }

    // Windows draws the table's headings unreadable in the dark theme, and its chosen row hard to tell apart.
    private void DrawFeatures()
    {
        _features.OwnerDraw = true;
        _features.BackColor = SettingsStyle.Card;
        _features.ForeColor = SettingsStyle.Text;
        int Unit(int value) => SettingsStyle.Unit(_features, value);
        // A list of tiny pictures is the way to set the height of a row.
        void RowHeight()
        {
            ImageList? before = _features.SmallImageList;
            _features.SmallImageList = new ImageList { ImageSize = new Size(1, Math.Max(Unit(32), _features.Font.Height + Unit(14))) };
            before?.Dispose();
        }
        _features.HandleCreated += (_, _) => RowHeight();
        _features.Disposed += (_, _) => _features.SmallImageList?.Dispose();
        _features.DrawColumnHeader += (_, e) =>
        {
            Graphics g = e.Graphics;
            SettingsStyle.SoftText(g);
            using (var back = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(back, e.Bounds);
            Native.DirectText.DrawText(g, e.Header?.Text, SettingsStyle.CaptionFont,
                new Rectangle(e.Bounds.Left + Unit(FeatureInset(e.ColumnIndex)), e.Bounds.Top, e.Bounds.Width - Unit(FeatureInset(e.ColumnIndex) + 4), e.Bounds.Height),
                SettingsStyle.Secondary, FeatureText);
            using var line = new Pen(SettingsStyle.Divider);
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        // Each cell is drawn on its own, once, with its part of the row's background: Windows redraws only the first
        // cell as the pointer passes, and a row drawn whole for each cell made the table lag.
        _features.DrawItem += (_, _) => { };
        _features.DrawSubItem += (_, e) => { if (e.Item is { } item) DrawFeatureRow(e.Graphics, item, e.ColumnIndex); };
        void DrawFeatureRow(Graphics g, ListViewItem item, int only)
        {
            SettingsStyle.SoftText(g);
            Rectangle row = item.Bounds;
            int start = row.Left;
            for (int index = 0; index < only && index < _features.Columns.Count; index++) start += _features.Columns[index].Width;
            if (only >= _features.Columns.Count) return;
            var part = new Rectangle(start, row.Top, only == _features.Columns.Count - 1 ? Math.Max(0, row.Right - start) : _features.Columns[only].Width, row.Height);
            if (part.Width <= 0) return;
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.SetClip(part, System.Drawing.Drawing2D.CombineMode.Intersect);
            using (var back = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(back, part);
            if (item.Tag is FeatureHeading heading)
            {
                // Across the whole row, from the first cell; the others only clear their part.
                if (only == NameColumn)
                {
                    g.ResetClip();
                    g.SetClip(row, System.Drawing.Drawing2D.CombineMode.Replace);
                    var words = new Rectangle(row.Left + Unit(FeatureInset(0) - 8), row.Top + Unit(6), row.Width - Unit(FeatureInset(0)), row.Height - Unit(6));
                    Native.DirectText.DrawText(g, heading.Text, SettingsStyle.HeadingFont, words, SettingsStyle.Text, FeatureText);
                }
                g.Restore(state);
                return;
            }
            var wash = new Rectangle(row.Left + Unit(2), row.Top + Unit(1), row.Width - Unit(4), row.Height - Unit(2));
            if (item.Selected)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = SettingsStyle.Rounded(wash, Unit(4));
                using var brush = new SolidBrush(SettingsStyle.RowSelectedOn(SettingsStyle.Card));
                g.FillPath(brush, path);
                SettingsStyle.SelectionBar(g, wash, _features);
                if (item.Focused && SettingsStyle.ShowsFocus(_features)) SettingsStyle.FocusRing(g, Rectangle.Inflate(wash, Unit(1), Unit(1)), Unit(5), _features);
            }
            int left = row.Left;
            for (int index = 0; index < _features.Columns.Count && index < item.SubItems.Count; index++)
            {
                var cell = new Rectangle(left, row.Top, _features.Columns[index].Width, row.Height);
                left += cell.Width;
                if (index != only) continue;
                bool problem = index == StateColumn && item.Tag is AppCommand command && _shortcutProblems.ContainsKey(command);
                Color color = problem ? SettingsStyle.Error : index is NameColumn or ShortcutColumn ? SettingsStyle.Text : SettingsStyle.Secondary;
                int inset = Unit(FeatureInset(index));
                bool tagged = index == NameColumn && item.Tag is AppCommand action && CommandCatalog.IsExperimental(action);
                int tag = tagged ? Unit(8) + MenuParts.TagSize(Unit).Width : 0;
                int reset = index == ShortcutColumn && ResetGlyph(item) is { } glyph ? glyph.Width + Unit(8) : 0;
                const TextFormatFlags format = FeatureText;
                string text = item.SubItems[index].Text;
                var words = new Rectangle(cell.Left + inset, cell.Top, Math.Max(0, cell.Width - inset - Unit(4) - tag - reset), cell.Height);
                Native.DirectText.DrawText(g, text, _features.Font, words, color, format);
                if (tagged && words.Width > 0)
                {
                    int width = Math.Min(words.Width, Native.DirectText.MeasureText(g, text, _features.Font, words.Size, format).Width);
                    MenuParts.DrawTag(g, words.Left + width + Unit(8), cell.Top + cell.Height / 2, Unit);
                }
                if (reset > 0 && ResetGlyph(item) is { } place && MenuParts.Icon(MenuParts.Undo) is { } undo)
                    g.DrawImage(undo, place);
            }
            g.Restore(state);
        }
    }

    // A changed shortcut ends in the mark that puts the default back, right after its keys.
    internal Rectangle? ResetGlyph(ListViewItem item)
    {
        if (item.Tag is not AppCommand command || !ShortcutChanged(command) || item.SubItems.Count <= ShortcutColumn ||
            _features.Columns.Count <= ShortcutColumn) return null;
        int size = FeatureUnit(16);
        Rectangle row = item.Bounds;
        int start = row.Left;
        for (int index = 0; index < ShortcutColumn; index++) start += _features.Columns[index].Width;
        int inset = FeatureUnit(FeatureInset(ShortcutColumn));
        string text = item.SubItems[ShortcutColumn].Text;
        int width = text.Length == 0 ? 0 : Native.DirectText.MeasureText(text, _features.Font, Size.Empty, FeatureMeasure).Width + FeatureUnit(8);
        int most = start + _features.Columns[ShortcutColumn].Width - FeatureUnit(4) - size;
        return new Rectangle(Math.Min(start + inset + width, most), row.Top + (row.Height - size) / 2, size, size);
    }

    private const TextFormatFlags FeatureText = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private const TextFormatFlags FeatureMeasure = FeatureText & ~TextFormatFlags.EndEllipsis; // Measuring with it would cut them short.

    private static int FeatureInset(int column) => column == 0 ? 18 : 12;

    private void FitFeatureColumns()
    {
        // Sizes still change while the window closes, when the table's rows are already being taken apart.
        if (_fillingFeatures || Disposing || IsDisposed || _features.Disposing || !_features.IsHandleCreated) return;
        // What shows: Windows' scroll bar is hidden past the edge, so the table may be wider than that.
        int width = Math.Min(_features.ClientSize.Width, _features.Parent?.ClientSize.Width ?? int.MaxValue);
        width -= FeatureUnit(ThinScrollBar.BarWidth);
        if (width <= 0 || _features.Columns.Count < FeatureColumnCount) return;
        int pad = FeatureUnit(FeatureInset(1) + 4);
        int glyph = FeatureUnit(16 + 8);
        var need = new int[FeatureColumnCount];
        for (int column = 1; column < FeatureColumnCount; column++)
        {
            int widest = Native.DirectText.MeasureText(_features.Columns[column].Text, SettingsStyle.CaptionFont, Size.Empty, FeatureMeasure).Width;
            foreach (ListViewItem? item in _features.Items)
                if (item is not null && item.Tag is not FeatureHeading && column < item.SubItems.Count)
                    widest = Math.Max(widest, Native.DirectText.MeasureText(item.SubItems[column].Text, _features.Font, Size.Empty, FeatureMeasure).Width +
                        (column == ShortcutColumn && item.Tag is AppCommand command && ShortcutChanged(command) ? glyph : 0));
            need[column] = widest + pad;
        }
        need[ShortcutColumn] = Math.Max(need[ShortcutColumn], FeatureUnit(150)); // Room to change it in place.
        need[StateColumn] = Math.Min(need[StateColumn], FeatureUnit(190));
        int least = FeatureUnit(150);
        int tag = FeatureUnit(FeatureInset(0) + 8 + 4) + MenuParts.TagSize(FeatureUnit).Width;
        foreach (ListViewItem? item in _features.Items)
            if (item?.Tag is AppCommand command && CommandCatalog.IsExperimental(command))
                least = Math.Max(least, Native.DirectText.MeasureText(item.Text, _features.Font, Size.Empty, FeatureMeasure).Width + tag);
        int rest = need.Skip(1).Sum();
        if (width - rest < least)
        {
            double share = Math.Max(0, width - least) / (double)rest;
            for (int column = 1; column < FeatureColumnCount; column++) need[column] = (int)(need[column] * share);
        }
        need[NameColumn] = width - need.Skip(1).Sum();
        bool moved = false;
        for (int column = 0; column < FeatureColumnCount; column++)
            if (_features.Columns[column].Width != need[column]) { _features.Columns[column].Width = need[column]; moved = true; }
        // Windows redraws only part of a row whose columns moved, leaving words where the columns used to be.
        if (moved) { _features.Invalidate(); PlaceShortcutEditor(); }
        // The columns fit now, but a table that was wider a moment ago keeps the sideways scroll bar it showed then,
        // drawn in Windows' own colours, until something else redraws it. Taking it away resizes the table, which
        // fits the columns again: only while it shows, and not from within this.
        const long WS_HSCROLL = 0x00100000;
        if (!_hidingSideways && need.Sum() <= _features.ClientSize.Width &&
            (Native.User32.GetWindowLongPtr(_features.Handle, Native.User32.GWL_STYLE) & WS_HSCROLL) != 0)
        {
            _hidingSideways = true;
            try { Native.User32.ShowScrollBar(_features.Handle, Native.User32.SB_HORZ, false); }
            finally { _hidingSideways = false; }
        }
    }

    private bool _hidingSideways;

    internal void SetActionFilter(ActionFilter filter) => _actionFilter.SelectedIndex = (int)filter;

    internal void RefreshFeatures()
    {
        object? selected = SelectedFeature();
        _features.BeginUpdate();
        _fillingFeatures = true; // The rows are half there until the end; the columns are fitted then.
        _features.Items.Clear();
        string query = _actionSearch.Text.Trim();
        bool byKeys = query.Length > 0 && HotkeyText.TryParse(query, out _, out _);
        HotkeyText.TryParse(query, out uint modifiers, out Keys key);
        var filter = (ActionFilter)Math.Max(0, _actionFilter.SelectedIndex);
        bool Found(params string[] values) => query.Length == 0 ||
            values.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        void Heading(string text) => _features.Items.Add(new ListViewItem([text, "", ""]) { Tag = new FeatureHeading(text) });

        foreach (ActionGroup group in Enum.GetValues<ActionGroup>())
        {
            bool headed = false;
            foreach (AppCommand command in CommandCatalog.All.Where(command => CommandCatalog.GroupOf(command) == group))
            {
                string name = CommandCatalog.Name(command).Replace("&", "");
                string shortcut = _hotkeys.TryGetValue(command, out TextBox? box) ? box.Text : _settings.Hotkeys.GetValueOrDefault(command, "");
                string state = FeatureState(command, shortcut);
                bool shown = filter switch
                {
                    ActionFilter.WithShortcut => shortcut.Length > 0,
                    ActionFilter.Changed => ShortcutChanged(command),
                    ActionFilter.NotWorking => NotWorking(command, shortcut),
                    _ => true,
                } && (byKeys ? SameKeys(shortcut, modifiers, key) : Found(name, shortcut, state));
                if (!shown) continue;
                if (!headed) { Heading(GroupName(group)); headed = true; }
                _features.Items.Add(new ListViewItem([name, shortcut, state]) { Tag = command });
            }
        }

        bool mouseHeaded = false;
        void Mouse(string name, string gesture, bool on, string page)
        {
            string state = on ? T("features.enabled") : T("features.disabled");
            bool shown = filter switch
            {
                ActionFilter.All => true,
                ActionFilter.WithShortcut => on,
                _ => false,
            } && !byKeys && Found(name, gesture, state);
            if (!shown) return;
            if (!mouseHeaded) { Heading(T("actions.group.mouse")); mouseHeaded = true; }
            _features.Items.Add(new ListViewItem([name, gesture, state]) { Tag = page });
        }
        Mouse(T("features.moveResize"), string.Format(T("features.dragShortcut"), _modifierKey.SelectedIndex == (int)ModifierKey.Win ? "Win" : "Alt"), _modifierDrag.Checked, "gestures");
        Mouse(T("features.windowMenu"), WindowMenuGestureText(), _windowMenu.Checked, "gestures");
        Mouse(T("features.opacityWheel"), T("features.wheelShortcut"), _opacityWheel.Checked, "appearance");

        foreach (ListViewItem row in _features.Items)
            if (row.Tag is not FeatureHeading && Equals(row.Tag, selected)) { row.Selected = true; row.Focused = true; }
        _features.EndUpdate();
        _fillingFeatures = false;
        FitFeatureColumns();
        // Filled as its page first shows, the table is fitted again once that page has its final size.
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) FitFeatureColumns(); });
        if (_features.SelectedItems.Count == 0 && _features.Items.Cast<ListViewItem>().FirstOrDefault(row => row.Tag is not FeatureHeading) is { } first)
            first.Selected = true;

        if (_features.Items.Count > 0) _featuresNote.Text = T("features.help");
        else if (byKeys) _featuresNote.Text = string.Format(FreeToUse(modifiers, key) ? T("actions.keys.free") : T("actions.keys.taken"), query);
        else _featuresNote.Text = T("actions.search.none");
        _featuresNote.ForeColor = SettingsStyle.Secondary;
        if (_editing is not null) PlaceShortcutEditor();
        ShowFeatureDetails();
    }
}
