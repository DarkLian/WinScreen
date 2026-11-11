using static WinScreen.Localization.UiText;
using System.Diagnostics;
using WinScreen.Tray;
using WinScreen.Updates;

namespace WinScreen.Settings;

/// <summary>The settings window. Apply puts the changes into effect and keeps it open; Save does that and closes it.</summary>
public sealed class SettingsWindow : Form
{
    private const int ContentWidth = SwitchBox.RowWidth + 20;
    /// <summary>The widths of a number box and of a list that drops down, at 100% scaling, so they line up down a page.</summary>
    private const int NumberWidth = 120, ChoiceWidth = 220;

    private readonly AppSettings _settings;
    private AppSettings? _pendingImport;
    private readonly NumericUpDown _opacityStep = new() { Minimum = 1, Maximum = 25, Width = 70 };
    private readonly CheckBox _opacityWheel = new SwitchBox { Text = T("settings.opacity.wheel"), AutoSize = true };
    private readonly CheckBox _layeredGlass = new SwitchBox { Text = T("settings.effects.layeredGlass"), AutoSize = true };
    private readonly CheckBox _pictureSafeDark = new SwitchBox { Text = T("settings.effects.pictureSafeDark"), AutoSize = true };
    private readonly NumericUpDown _glassBackground = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassPictures = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly CheckBox _detectDark = new SwitchBox { Text = T("settings.dark.detect"), AutoSize = true };
    private readonly TextBox _borderColor = new() { Width = 90 };
    private readonly NumericUpDown _borderOpacity = new() { Minimum = 10, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _borderThickness = new() { Minimum = 1, Maximum = 12, Width = 70 };
    private readonly CheckBox _autoAnchors = new SwitchBox { Text = T("anchors.auto.enable"), AutoSize = true };
    private readonly ComboBox _anchorSide = new ThemedCombo { Width = 200 };
    private readonly ComboBox _anchorLook = new ThemedCombo { Width = 200 };
    private readonly NumericUpDown _anchorLimit = new() { Minimum = 1, Maximum = 10, Width = 70 };
    private readonly NumericUpDown _anchorSize = new() { Minimum = 32, Maximum = 72, Increment = 4, Width = 70 };
    private readonly CheckBox _anchorRecency = new SwitchBox { Text = T("anchors.recency"), AutoSize = true };
    private readonly CheckBox _anchorAvoid = new SwitchBox { Text = T("anchors.avoid"), AutoSize = true };
    private readonly ThemedList _anchorOnly = new();
    private readonly ThemedList _anchorExcluded = new();
    private readonly ComboBox _newAnchorOnly = ProgramPicker();
    private readonly ComboBox _newAnchorExcluded = ProgramPicker();
    private readonly ComboBox _toolbarPlacement = new ThemedCombo { Width = 200 };
    private readonly ComboBox _toolbarTrigger = new ThemedCombo { Width = 200 };
    private readonly NumericUpDown _toolbarDelay = new() { Minimum = 100, Maximum = 2000, Increment = 100, Width = 90 };
    private readonly ThemedCheckList _toolbarButtons = new() { CheckOnClick = true, Width = SwitchBox.RowWidth - 8 };
    // The pages. Its own row of tabs is hidden: the list on the left chooses the page.
    private readonly TabControl _tabs = new PagesControl { Size = new Size(540, 520), TabStop = false, Multiline = true };
    private readonly NavList _nav = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new();
    private readonly Label _pageTitle = new SoftLabel() { AutoSize = true, Font = SettingsStyle.PageTitleFont, UseMnemonic = false, Margin = new Padding(0, 0, 0, 14) };
    private readonly Panel _saveBar = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _actionSearch = new() { Dock = DockStyle.Top };
    private readonly InputFrame _shortcutEditor = new(null) { Width = 240, ReserveEnd = 22, Anchor = AnchorStyles.Right };
    private readonly Label _shortcutLabel = new SoftLabel() { AutoSize = true, Anchor = AnchorStyles.Left, Font = SettingsStyle.HeadingFont, UseMnemonic = false, AutoEllipsis = true };
    private readonly Dictionary<AppCommand, string> _shortcutProblems = [];
    private bool _fillingFeatures;
    private readonly Func<AppCommand, bool>? _registered;
    private readonly ThemedTable _features = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };

    private sealed record ToolbarChoice(AppCommand Command)
    {
        public override string ToString() => CommandCatalog.Name(Command).Replace("&", "");
    }
    private readonly ComboBox _pipBackend = new ThemedCombo { Width = 240 };
    private readonly ComboBox _pipRate = new ThemedCombo { Width = 160 };
    private readonly ComboBox _pipScaling = new ThemedCombo { Width = 240 };
    private readonly NumericUpDown _pipOpacity = new() { Minimum = 10, Maximum = 100, Width = 75 };
    private readonly CheckBox _pipTaskbar = new SwitchBox { Text = T("pip.taskbar.enable"), AutoSize = true };
    private readonly ComboBox _pipPreview = new ThemedCombo { Width = 240 };
    private readonly CheckBox _pipCaptureBorder = new SwitchBox { Text = T("pip.captureBorder.hide"), AutoSize = true };
    private readonly CheckBox _pipDiagnostics = new SwitchBox { Text = T("pip.diagnostics.includeHardware"), AutoSize = true };
    private readonly List<int> _pipRates = [.. PipPreferences.FrameRates];

    private readonly CheckBox _showNotifications = new SwitchBox
        { Text = T("settings.notifications.enable"), AutoSize = true };

    private readonly CheckBox _automaticUpdates = new SwitchBox
        { Text = T("updates.enable"), AutoSize = true };

    private readonly CheckBox _experimental = new SwitchBox
        { Text = T("settings.experimental.enable"), AutoSize = true };

private readonly ErrorProvider _errors = new();
    private readonly ComboBox _language = new ThemedCombo { Width = 240 };
    private static readonly string[] Languages = ["system", "en", "zh-Hans", "zh-Hant"];
    private readonly ComboBox _theme = new ThemedCombo { Width = 150 };
    private static readonly string[] Themes = ["system", "light", "dark"];
    private readonly StartWithWindows _startup;
    private readonly Func<AppSettings, IReadOnlyList<HotkeyProblem>> _apply;
    private readonly Dictionary<AppCommand, TextBox> _hotkeys = [];
    private readonly ThemedList _excluded = new();
    private readonly ThemedList _included = new();

    private readonly ThemedList _setups = new();

    private readonly ComboBox _newExcluded = ProgramPicker();
    private readonly ComboBox _newIncluded = ProgramPicker();

    private readonly NumericUpDown _defaultOpacity = new()
    {
        Minimum = AppSettings.LowestOpacityPercent,
        Maximum = AppSettings.HighestOpacityPercent,
        Width = 70,
    };

    private readonly CheckBox _startWithWindows = new SwitchBox { Text = T("settings.startWithWindows"), AutoSize = true };

    private readonly CheckBox _showToolbar = new SwitchBox
        { Text = T("settings.toolbar.enable"), AutoSize = true };

    private readonly CheckBox _pauseInFullScreen =
        new SwitchBox { Text = T("settings.fullScreen.pause"), AutoSize = true };

    private readonly CheckBox _allowPip =
        new SwitchBox { Text = T("settings.fullScreen.allowPip"), AutoSize = true };

    private readonly CheckBox _modifierDrag = new SwitchBox
        { Text = T("gestures.moveResize.enable"), AutoSize = true };

    private readonly CheckBox _snapWhileDragging = new SwitchBox { Text = T("gestures.moveResize.snap"), AutoSize = true };
    private readonly ComboBox _modifierKey = new ThemedCombo { Width = 110 };

    private readonly ThemedList _modifierExclusions = new();
    private readonly ComboBox _newModifierExclusion = ProgramPicker();
    private readonly CheckBox _windowMenu = new SwitchBox { Text = T("gestures.menu.enable"), AutoSize = true };
    private readonly ComboBox _windowMenuGesture = new ThemedCombo { Width = 240 };
    private readonly ThemedList _windowMenuExclusions = new();
    private readonly ComboBox _newMenuExclusion = ProgramPicker();

    private readonly CheckBox _pausePin = new SwitchBox
        { Text = T("settings.pin.pauseMaximized"), AutoSize = true };

    private readonly CheckBox _opaqueOnHover = new SwitchBox
        { Text = T("settings.opacity.solidOnHover"), AutoSize = true };

    private readonly CheckBox _highlightPinned = new SwitchBox
        { Text = T("settings.pin.highlight"), AutoSize = true };

    private readonly CheckBox _pipAutoMove = new SwitchBox
        { Text = T("settings.pip.autoMove"), AutoSize = true };
    private readonly CheckBox _pipMiddleScroll = new SwitchBox
        { Text = T("settings.pip.middleScroll"), AutoSize = true };
    private readonly CheckBox _hoverAutomatic = new SwitchBox { Text = T("settings.pip.hover.automatic"), AutoSize = true };
    private readonly ThemedList _originalPrograms = new();
    private readonly ComboBox _newOriginalProgram = ProgramPicker();
    private readonly CheckBox _hoverBrowsers = new SwitchBox { Text = T("settings.pip.hover.browsers"), AutoSize = true };
    private readonly CheckBox _hoverFirefox = new SwitchBox { Text = T("settings.pip.hover.firefox"), AutoSize = true };
    private readonly CheckBox _hoverElectron = new SwitchBox { Text = T("settings.pip.hover.electron"), AutoSize = true };
    private readonly CheckBox _hoverExplorer = new SwitchBox { Text = T("settings.pip.hover.explorer"), AutoSize = true };
    private readonly ThemedList _hoverPrograms = new();
    private readonly ComboBox _newHoverProgram = ProgramPicker();
    private Control? _hoverOptions;
    private readonly FlowLayoutPanel _appRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = SettingsStyle.Card };
    private readonly ComboBox _newApp = ProgramPicker();
    private readonly List<string> _addedApps = [];

    private readonly HashSet<ProgramSetup> _removedSetups = [];
    private readonly Button _applyButton = new() { Text = T("settings.apply"), AutoSize = true, Enabled = false };
    private string _savedState = "";
    private bool _refreshQueued;

    private readonly Label _status = new SoftLabel()
        { AutoSize = true, ForeColor = SettingsStyle.Text, MaximumSize = new Size(500, 0), Margin = new Padding(0, 6, 0, 0) };

    public SettingsWindow(AppSettings settings, StartWithWindows startup,
        Func<AppSettings, IReadOnlyList<HotkeyProblem>> apply, Func<AppCommand, bool>? registered = null)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F); // Every size below is written for 100% display scaling.
        AutoScaleMode = AutoScaleMode.Dpi;
        _settings = settings;
        _startup = startup;
        _apply = apply;
        _registered = registered;
        _errors.ContainerControl = this;
        _experimental.CheckedChanged += (_, _) =>
        {
            _pipAutoMove.Enabled = _pipMiddleScroll.Enabled = _experimental.Checked;
            _hoverOptions?.Enabled = _experimental.Checked && Pip.PipEnhancedHover.Supported;
            if (_hotkeys.Count > 0) RefreshFeatures();
        };
        _hoverAutomatic.CheckedChanged += (_, _) => ShowHoverChoices();
        _language.Items.AddRange([T("settings.language.system"), "English", "简体中文", "繁體中文"]);
        _theme.Items.AddRange([T("settings.theme.system"), T("settings.theme.light"), T("settings.theme.dark")]);

        Text = T("settings.window.title");
        Icon = TrayApplication.LoadIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(640, 400);
        Font = MenuStyle.Font;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;

        var tabs = _tabs;
        tabs.Dock = DockStyle.Fill;
        var generalPage = Tab(T("settings.general"), BuildGeneral());
        var featuresPage = Tab(T("features.title"), BuildFeatures());
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(featuresPage);
        tabs.TabPages.Add(Tab(T("settings.programs"), BuildPrograms()));
        tabs.TabPages.Add(Tab(T("settings.savedSetups"), BuildSetups()));
        var pipPage = Tab(T("pip.open"), BuildPip());
        var gesturePage = Tab(T("gestures.title"), BuildModifierDrag());
        var appearancePage = Tab(T("settings.customization"), BuildCustomization());
        var toolbarPage = Tab(T("toolbar.settings.title"), BuildToolbar());
        var anchorsPage = Tab(T("anchors.settings.title"), BuildAnchors());
        tabs.TabPages.AddRange([pipPage, gesturePage, appearancePage, toolbarPage, anchorsPage]);
        tabs.Selected += (_, _) =>
        {
            if (tabs.SelectedTab == featuresPage) RefreshFeatures();
            _nav.Selected = tabs.SelectedTab;
            _pageTitle.Text = tabs.SelectedTab?.Text ?? "";
        };
        _nav.Add(generalPage.Text, MenuParts.Home, generalPage);
        _nav.AddHeading(T("settings.nav.windows"));
        _nav.Add(featuresPage.Text, MenuParts.Keyboard, featuresPage);
        _nav.Add(gesturePage.Text, MenuParts.Mouse, gesturePage);
        _nav.Add(toolbarPage.Text, MenuParts.Bar, toolbarPage);
        _nav.Add(anchorsPage.Text, MenuParts.Pinned, anchorsPage);
        _nav.Add(appearancePage.Text, MenuParts.Palette, appearancePage);
        _nav.AddHeading(T("settings.nav.views"));
        _nav.Add(pipPage.Text, MenuParts.NewWindow, pipPage);
        _nav.AddHeading(T("settings.nav.apps"));
        _nav.Add(tabs.TabPages[2].Text, MenuParts.Apps, tabs.TabPages[2]);
        _nav.Add(tabs.TabPages[3].Text, MenuParts.Save, tabs.TabPages[3]);
        _nav.Selected = generalPage;
        _nav.Chosen += chosen => { tabs.SelectedTab = (TabPage)chosen; ShowSearchMatch(); };
        _search.PlaceholderText = _search.AccessibleName = T("settings.search");
        _search.TextChanged += (_, _) => SearchSettings();
        _features.ItemActivate += (_, _) => // A double-click, or Enter from the keyboard.
        {
            if (_features.SelectedItems.Count == 0) return;
            object? tag = _features.SelectedItems[0].Tag;
            if (tag is AppCommand command)
            {
                tabs.SelectedTab = featuresPage;
                ShowShortcutEditor();
                _hotkeys[command].Focus();
                return;
            }
            tabs.SelectedTab = (tag as string) switch
            {
                "toolbar" => toolbarPage,
                "anchors" => anchorsPage,
                "gestures" => gesturePage,
                "pip" => pipPage,
                "appearance" => appearancePage,
                _ => generalPage,
            };
        };

        var side = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = SettingsStyle.Side, Padding = new Padding(10, 12, 10, 8) };
        _nav.BackColor = SettingsStyle.Side;
        side.Controls.Add(_nav);
        side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = SettingsStyle.Side });
        // Its edges line up with the highlighted page below, which sits a little in from the side's edges.
        var searchRoom = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2, 0, 2, 0), BackColor = SettingsStyle.Side };
        searchRoom.Controls.Add(new InputFrame(_search) { Glyph = MenuParts.Search, Dock = DockStyle.Fill });
        side.Controls.Add(searchRoom);
        var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Dock = DockStyle.Fill, Padding = new Padding(20, 6, 12, 10) };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pageTitle.Text = generalPage.Text;
        page.Controls.Add(_pageTitle, 0, 0);
        page.Controls.Add(tabs, 0, 1);
        page.Controls.Add(_status, 0, 2);
        page.Controls.Add(BuildButtons(), 0, 3);
        Controls.Add(page);
        Controls.Add(side);

        LoadValues();
        _pipAutoMove.Enabled = _pipMiddleScroll.Enabled = _experimental.Checked;
        _hoverOptions?.Enabled = _experimental.Checked && Pip.PipEnhancedHover.Supported;
        WatchForChanges(this);
        foreach (TextBox box in _hotkeys.Values) box.TextChanged += (_, _) => QueueApplyRefresh();
        MarkSaved();
        ResumeLayout(true);
    }

    /// <summary>The page showing, for opening the window again where it was.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int PageIndex
    {
        get => _tabs.SelectedIndex;
        set => _tabs.SelectedIndex = Math.Clamp(value, 0, _tabs.TabCount - 1);
    }

    /// <summary>True when what's on screen differs from what's in effect, so Apply has something to do.</summary>
    internal bool HasChanges => _applyButton.Enabled;

    /// <summary>Applies the changes and keeps the window open, the way the Apply button does.</summary>
    internal bool ApplyChanges()
    {
        if (!SaveChanges()) return false;
        MarkSaved();
        return true;
    }

    private void MarkSaved()
    {
        _savedState = CurrentState();
        _applyButton.Enabled = false;
        _saveBar.Visible = false;
    }

    /// <summary>Rechecks whether there's anything to apply. Changing something and changing it back leaves nothing.</summary>
    internal void RefreshApply()
    {
        _refreshQueued = false;
        if (IsDisposed) return;
        _applyButton.Enabled = _pendingImport is not null || CurrentState() != _savedState;
        _saveBar.Visible = _applyButton.Enabled; // The bar is only there while something waits to be saved.
    }

    /// <summary>Esc closes the window, as the Cancel button it used to have did.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessDialogKey(keyData);
        Close();
        return true;
    }

    /// <summary>Every piece of text on a page, for finding a setting by a word in it.</summary>
    private static IEnumerable<Control> WithText(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(WithText(child)))
        .Where(control => control is Label or CheckBox or Button or SettingsCard && control.Text.Length > 0);

    private bool Matches(Control control) => control.Text.Replace("&", "").Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Leaves only the pages that mention what was typed, and goes to the first of them.</summary>
    private void SearchSettings()
    {
        if (string.IsNullOrWhiteSpace(_search.Text)) { _nav.Filter(null); return; }
        TabPage[] found = _tabs.TabPages.Cast<TabPage>().Where(page => Matches(page) || WithText(page).Any(Matches)).ToArray();
        _nav.Filter(found);
        if (found.Length == 0) return;
        if (!found.Contains(_tabs.SelectedTab)) _tabs.SelectedTab = found[0];
        ShowSearchMatch();
    }

    /// <summary>Scrolls the page to the first thing on it that matches the search.</summary>
    private void ShowSearchMatch()
    {
        if (string.IsNullOrWhiteSpace(_search.Text) || _tabs.SelectedTab is not { } page) return;
        if (WithText(page).FirstOrDefault(Matches) is { } match)
            for (Control? up = match; up is not null && up != page; up = up.Parent)
                if (up.Parent is ScrollableControl { AutoScroll: true } scroller) scroller.ScrollControlIntoView(match);
    }

    // Some controls report a change before it happens, so the check waits for the change to land.
    private void QueueApplyRefresh()
    {
        if (!IsHandleCreated) { RefreshApply(); return; }
        if (_refreshQueued) return;
        _refreshQueued = true;
        BeginInvoke(RefreshApply);
    }

    private void WatchForChanges(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case CheckBox box: box.CheckedChanged += (_, _) => QueueApplyRefresh(); break;
                case NumericUpDown number: number.TextChanged += (_, _) => QueueApplyRefresh(); break;
                case CheckedListBox list: list.ItemCheck += (_, _) => QueueApplyRefresh(); break;
                case ComboBox combo:
                    combo.SelectedIndexChanged += (_, _) => QueueApplyRefresh();
                    combo.TextChanged += (_, _) => QueueApplyRefresh();
                    break;
                case TextBox text when text != _actionSearch: text.TextChanged += (_, _) => QueueApplyRefresh(); break;
                case Button button when button != _applyButton: button.Click += (_, _) => QueueApplyRefresh(); break;
            }
            WatchForChanges(control);
        }
    }

    /// <summary>Everything the window would save, written out so two moments can be compared.</summary>
    private string CurrentState()
    {
        AppSettings draft = Draft(new AppSettings(), commitTyped: false);
        draft.SavedSetups = _setups.Items.Cast<ProgramSetup>().ToList();
        return System.Text.Json.JsonSerializer.Serialize(draft);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitWorkingArea();
    }

    /// <summary>The title bar takes the window's own colours instead of the accent colour, where Windows allows it.</summary>
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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitWorkingArea();
    }

    private void FitWorkingArea()
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _errors.Dispose();
            foreach (TextBox box in _hotkeys.Values) box.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Called when a key combination is pressed in a hotkey box.</summary>
    internal void CaptureHotkey(AppCommand command, Keys keyData, bool win = false)
    {
        Keys key = keyData & Keys.KeyCode;
        bool heldModifier = win || (keyData & (Keys.Control | Keys.Alt | Keys.Shift)) != 0;
        if (key is Keys.Delete or Keys.Back && !heldModifier)
        {
            _hotkeys[command].Text = "";
            _status.Text = "";
            return;
        }

        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return;

        bool usable = win || (keyData & (Keys.Control | Keys.Alt)) != 0 || HotkeyText.IsFunctionKey(key);
        if (usable)
            _hotkeys[command].Text = HotkeyText.Format(keyData, win);
        _status.Text = usable ? "" : T("hotkeys.modifierRequired");
    }

    /// <summary>Applies and saves what is on screen. Returns false if a hotkey couldn't be claimed.</summary>
    internal bool SaveChanges()
    {
        AppSettings draft = Draft((_pendingImport ?? _settings).Clone(), commitTyped: true);
        draft.Imported = _pendingImport is not null;
        try
        {
            IReadOnlyList<HotkeyProblem> problems = _apply(draft);
            _status.Text = string.Join(Environment.NewLine, problems.Select(problem => problem.Message));
            foreach (HotkeyProblem problem in problems)
                if (_hotkeys.TryGetValue(problem.Command, out TextBox? box))
                    _errors.SetError(box, problem.Message);
            if (problems.Count > 0)
            {
                foreach (HotkeyProblem problem in problems) _shortcutProblems[problem.Command] = problem.Message;
                _actionSearch.Clear();
                RefreshFeatures();
                _tabs.SelectedIndex = 1;
                ListViewItem? row = _features.Items.Cast<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, problems[0].Command));
                if (row is not null) { row.Selected = true; row.EnsureVisible(); }
                ShowShortcutEditor();
                _hotkeys[problems[0].Command].Focus();
                return false;
            }
            _pendingImport = null;
            _shortcutProblems.Clear();
            RefreshFeatures();
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException)
        {
            _status.Text = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// What's on screen, on top of <paramref name="draft"/>. With <paramref name="commitTyped"/>, program names typed
    /// but not yet added go into their lists, the way saving treats them; otherwise they're only counted.
    /// </summary>
    private AppSettings Draft(AppSettings draft, bool commitTyped)
    {
        List<string> Programs(ListBox list, ComboBox typed)
        {
            if (commitTyped) AddTyped(list, typed);
            List<string> names = list.Items.Cast<string>().ToList();
            string name = ProgramName(typed.Text);
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            return names;
        }
        int Number(NumericUpDown box)
        {
            // Reading Value would tidy up a number that's still being typed, so the text is read instead.
            if (commitTyped || !decimal.TryParse(box.Text, out decimal typed)) return (int)box.Value;
            return (int)Math.Clamp(typed, box.Minimum, box.Maximum);
        }
        foreach ((AppCommand command, TextBox box) in _hotkeys)
        {
            draft.Hotkeys[command] = box.Text;
            if (commitTyped) _errors.SetError(box, "");
        }

        // A name typed but not yet added is still what the person meant.
        draft.ExcludedPrograms = Programs(_excluded, _newExcluded);
        draft.IncludedPrograms = Programs(_included, _newIncluded);
        if (commitTyped) draft.SavedSetups.RemoveAll(setup => _removedSetups.Any(removed =>
            removed.Program == setup.Program && removed.ExecutablePath == setup.ExecutablePath));
        draft.ShowToolbar = _showToolbar.Checked;
        draft.Toolbar = new ToolbarPreferences
        {
            Placement = (ToolbarPlacement)Math.Max(0, _toolbarPlacement.SelectedIndex),
            Trigger = (ToolbarTrigger)Math.Max(0, _toolbarTrigger.SelectedIndex),
            HoverDelayMilliseconds = Number(_toolbarDelay),
            Buttons = _toolbarButtons.CheckedItems.Cast<ToolbarChoice>().Select(choice => choice.Command).ToList(),
        }.Validated();
        List<string> anchorOnly = Programs(_anchorOnly, _newAnchorOnly);
        List<string> anchorExcluded = Programs(_anchorExcluded, _newAnchorExcluded);
        draft.Anchors = new AnchorPreferences
        {
            Automatic = _autoAnchors.Checked,
            OnlyPrograms = anchorOnly,
            ExcludedPrograms = anchorExcluded,
            Side = (AnchorSide)Math.Max(0, _anchorSide.SelectedIndex),
            Style = (AnchorLook)Math.Max(0, _anchorLook.SelectedIndex),
            MaxAutomatic = Number(_anchorLimit),
            Size = Number(_anchorSize),
            RecencyEmphasis = _anchorRecency.Checked,
            AvoidContent = _anchorAvoid.Checked,
        }.Validated();
        draft.ShowNotifications = _showNotifications.Checked;
        draft.AutomaticUpdates = _automaticUpdates.Checked;
        draft.ExperimentalFeatures = _experimental.Checked;
        draft.StartWithWindows = _startWithWindows.Checked;
        draft.Language = Languages[Math.Max(0, _language.SelectedIndex)];
        draft.Theme = Themes[Math.Max(0, _theme.SelectedIndex)];
        draft.PauseInFullScreen = _pauseInFullScreen.Checked;
        draft.AllowPipInFullScreen = _allowPip.Checked;
        draft.ModifierDrag = _modifierDrag.Checked;
        draft.ModifierKey = (ModifierKey)Math.Max(0, _modifierKey.SelectedIndex);
        draft.SnapWhileDragging = _snapWhileDragging.Checked;
        draft.WindowMenu = _windowMenu.Checked;
        draft.WindowMenuGesture = (WindowMenuGesture)Math.Max(0, _windowMenuGesture.SelectedIndex);
        draft.WindowMenuExclusions = Programs(_windowMenuExclusions, _newMenuExclusion);
        draft.ModifierExclusions = Programs(_modifierExclusions, _newModifierExclusion);
        draft.PausePinWhenMaximized = _pausePin.Checked;
        draft.OpaqueOnHover = _opaqueOnHover.Checked;
        draft.HighlightPinned = _highlightPinned.Checked;
        draft.PipAutoMove = _pipAutoMove.Checked;
        draft.PipMiddleClickScroll = _pipMiddleScroll.Checked;
        draft.PipOriginalPrograms = Programs(_originalPrograms, _newOriginalProgram);
        draft.PipHover = new PipHoverPreferences
        {
            ChooseForMe = _hoverAutomatic.Checked, Browsers = _hover.Browsers, Firefox = _hover.Firefox, Electron = _hover.Electron,
            FileExplorer = _hover.FileExplorer, Programs = Programs(_hoverPrograms, _newHoverProgram),
        }.Validated();
        if (!_hoverAutomatic.Checked) draft.PipHover = draft.PipHover with
        {
            Browsers = _hoverBrowsers.Checked, Firefox = _hoverFirefox.Checked, Electron = _hoverElectron.Checked,
            FileExplorer = _hoverExplorer.Checked,
        };
        draft.DefaultOpacityPercent = Number(_defaultOpacity);
        draft.OpacityStepPercent = Number(_opacityStep);
        draft.OpacityWheel = _opacityWheel.Checked;
        draft.DetectAlreadyDark = _detectDark.Checked;
        draft.Effects = new EffectPreferences
        {
            LayeredGlass = _layeredGlass.Checked, PictureSafeDark = _pictureSafeDark.Checked,
            BackgroundPercent = Number(_glassBackground), TextPercent = Number(_glassText), PicturePercent = Number(_glassPictures),
        }.Validated();
        draft.PinBorder = new PinBorderPreferences
        {
            ColorHex = _borderColor.Text,
            OpacityPercent = Number(_borderOpacity),
            Thickness = Number(_borderThickness),
        }.Validated();
        draft.Pip.Backend = (PipBackendPreference)Math.Max(0, _pipBackend.SelectedIndex);
        draft.Pip.FrameRate = _pipRates[Math.Max(0, _pipRate.SelectedIndex)];
        draft.Pip.Scaling = (PipScaling)Math.Max(0, _pipScaling.SelectedIndex);
        draft.Pip.OpacityPercent = Number(_pipOpacity);
        draft.Pip.ShowDiagnostics = _pipDiagnostics.Checked;
        draft.HideCaptureBorder = _pipCaptureBorder.Checked;
        draft.PipViews = new PipViewPreferences { MinimizeToTaskbar = _pipTaskbar.Checked,
            HoverPreview = (PipHoverPreview)Math.Max(0, _pipPreview.SelectedIndex),
            HoverDelayMilliseconds = draft.PipViews.HoverDelayMilliseconds }.Validated();
        return draft;
    }

    internal void AddExcluded(string program) { AddProgram(_excluded, program); QueueApplyRefresh(); }

    internal void AddIncluded(string program) { AddProgram(_included, program); QueueApplyRefresh(); }

    internal void SetDefaultOpacity(int percent) => _defaultOpacity.Value = percent;

    /// <summary>Forgets a program's saved setup, the way the Remove button does.</summary>
    internal void RemoveSetup(ProgramSetup setup)
    {
        _setups.Items.Remove(setup);
        _removedSetups.Add(setup);
        QueueApplyRefresh();
    }

    private static void AddProgram(ListBox list, string program)
    {
        string name = ProgramName(program);
        if (name.Length > 0 && !list.Items.Contains(name))
            list.Items.Add(name);
    }

    private static string ProgramName(string program)
    {
        string name = program.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void AddTyped(ListBox list, ComboBox newProgram)
    {
        AddProgram(list, newProgram.Text);
        newProgram.Text = "";
    }

    private PipHoverPreferences _hover = new();
    private bool _hoverShowsOwn = true, _loadingHover;

    /// <summary>
    /// With "choose for me" on, the four boxes show what WinScreen picks and can't be changed; the person's own
    /// picks are kept for when it is turned off again.
    /// </summary>
    private void ShowHoverChoices()
    {
        bool own = !_hoverAutomatic.Checked;
        if (!own && _hoverShowsOwn && !_loadingHover)
            _hover = _hover with { Browsers = _hoverBrowsers.Checked, Firefox = _hoverFirefox.Checked,
                Electron = _hoverElectron.Checked, FileExplorer = _hoverExplorer.Checked };
        _hoverShowsOwn = own;
        bool allowed = _hoverOptions?.Enabled != false;
        _hoverAutomatic.Enabled = allowed;
        _hoverBrowsers.Enabled = _hoverFirefox.Enabled = _hoverElectron.Enabled = _hoverExplorer.Enabled = own && allowed;
        _hoverBrowsers.Checked = own ? _hover.Browsers : Pip.PipHoverApps.Recommended(Pip.PipHoverApp.Browser);
        _hoverFirefox.Checked = own ? _hover.Firefox : Pip.PipHoverApps.Recommended(Pip.PipHoverApp.Firefox);
        _hoverElectron.Checked = own ? _hover.Electron : Pip.PipHoverApps.Recommended(Pip.PipHoverApp.Electron);
        _hoverExplorer.Checked = own ? _hover.FileExplorer : Pip.PipHoverApps.Recommended(Pip.PipHoverApp.FileExplorer);
    }

    /// <summary>
    /// Fills the table of apps: the four kinds WinScreen knows, then every program named for Enhanced hover or for
    /// showing its original. A program has one or the other, never both: with its original shown, nothing is sent to it.
    /// </summary>
    private void RebuildApps()
    {
        const int hoverAt = SwitchBox.RowWidth - 190, originalAt = SwitchBox.RowWidth - 100, removeAt = SwitchBox.RowWidth - 36;
        bool hoverAllowed = _hoverOptions?.Enabled != false;
        _appRows.SuspendLayout();
        foreach (Control row in _appRows.Controls.Cast<Control>().ToArray())
        {
            foreach (CheckBox kind in new[] { _hoverBrowsers, _hoverFirefox, _hoverElectron, _hoverExplorer }) row.Controls.Remove(kind);
            _appRows.Controls.Remove(row);
            row.Dispose();
        }
        Panel Row(string name, bool heading)
        {
            var row = new Panel { Size = new Size(SwitchBox.RowWidth, heading ? 24 : 34), Margin = new Padding(3, 0, 3, 0), BackColor = SettingsStyle.Card };
            row.Controls.Add(new SoftLabel { Text = name, AutoSize = false, Bounds = new Rectangle(1, 0, hoverAt - 8, row.Height),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
                ForeColor = heading ? SettingsStyle.Secondary : SettingsStyle.Text, Font = heading ? MenuStyle.SmallFont : MenuStyle.Font });
            return row;
        }
        void Place(Panel row, Control control, int at)
        {
            control.Location = new Point(at, (row.Height - control.Height) / 2);
            row.Controls.Add(control);
            control.BringToFront();
        }
        Panel head = Row(T("settings.pip.apps.app"), true);
        foreach (var (text, at) in new[] { (T("menu.view.hover"), hoverAt), (T("settings.pip.apps.original"), originalAt) })
            Place(head, new SoftLabel { Text = text, AutoSize = true, ForeColor = SettingsStyle.Secondary, Font = MenuStyle.SmallFont, UseMnemonic = false }, at - 4);
        _appRows.Controls.Add(head);
        foreach (CheckBox kind in new[] { _hoverBrowsers, _hoverFirefox, _hoverElectron, _hoverExplorer })
        {
            Panel row = Row(kind.Text, false);
            kind.Size = kind.GetPreferredSize(Size.Empty);
            Place(row, kind, hoverAt);
            _appRows.Controls.Add(row);
        }
        bool Listed(ListBox list, string name) => list.Items.Cast<string>().Contains(name, StringComparer.OrdinalIgnoreCase);
        void Unlist(ListBox list, string name)
        {
            foreach (string item in list.Items.Cast<string>().Where(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
                list.Items.Remove(item);
        }
        string[] programs = _hoverPrograms.Items.Cast<string>().Concat(_originalPrograms.Items.Cast<string>()).Concat(_addedApps)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string program in programs)
        {
            Panel row = Row(program, false);
            var hover = new SwitchBox { Compact = true, Checked = Listed(_hoverPrograms, program), Enabled = hoverAllowed,
                AccessibleName = program + ": " + T("menu.view.hover") };
            var original = new SwitchBox { Compact = true, Checked = Listed(_originalPrograms, program),
                AccessibleName = program + ": " + T("settings.pip.apps.original") };
            hover.Size = hover.GetPreferredSize(Size.Empty);
            original.Size = original.GetPreferredSize(Size.Empty);
            hover.CheckedChanged += (_, _) =>
            {
                if (hover.Checked) { AddProgram(_hoverPrograms, program); original.Checked = false; }
                else Unlist(_hoverPrograms, program);
                if (!_addedApps.Contains(program, StringComparer.OrdinalIgnoreCase)) _addedApps.Add(program);
                QueueApplyRefresh();
            };
            original.CheckedChanged += (_, _) =>
            {
                if (original.Checked) { AddProgram(_originalPrograms, program); hover.Checked = false; }
                else Unlist(_originalPrograms, program);
                if (!_addedApps.Contains(program, StringComparer.OrdinalIgnoreCase)) _addedApps.Add(program);
                QueueApplyRefresh();
            };
            var remove = new PlainButton { Text = "\u00D7", AutoSize = false, Size = new Size(26, 24), Padding = Padding.Empty,
                AccessibleName = T("common.remove") + ": " + program };
            remove.Click += (_, _) =>
            {
                Unlist(_hoverPrograms, program);
                Unlist(_originalPrograms, program);
                _addedApps.RemoveAll(name => name.Equals(program, StringComparison.OrdinalIgnoreCase));
                BeginInvoke(RebuildApps); // Not from inside the click of a button this removes.
                QueueApplyRefresh();
            };
            Place(row, hover, hoverAt);
            Place(row, original, originalAt);
            Place(row, remove, removeAt);
            _appRows.Controls.Add(row);
        }
        _appRows.ResumeLayout(true);
    }

    private static ComboBox ProgramPicker()
    {
        return new ThemedPicker
        {
            Dock = DockStyle.Fill,
            Placeholder = T("programs.nameHint"),
            AccessibleName = T("programs.nameHint"),
            Choices = RunningPrograms,
            IconOf = ProgramIcons.Of,
        };
    }

    /// <summary>The programs with a window open right now, under the names the lists use.</summary>
    private static IEnumerable<string> RunningPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != 0 && process.Id != Environment.ProcessId && names.Add(process.ProcessName))
                        ProgramIcons.Remember(process.ProcessName, process.MainWindowHandle);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Ended while being listed, or not readable from here.
                }
            }
        }

        return names;
    }

    internal void SetOption(string option, bool on)
    {
        CheckBox box = option switch
        {
            nameof(AppSettings.ShowToolbar) => _showToolbar,
            nameof(AppSettings.ShowNotifications) => _showNotifications,
            nameof(AppSettings.AutomaticUpdates) => _automaticUpdates,
            nameof(AppSettings.ExperimentalFeatures) => _experimental,
            nameof(AppSettings.PauseInFullScreen) => _pauseInFullScreen,
            nameof(AppSettings.AllowPipInFullScreen) => _allowPip,
            nameof(AppSettings.ModifierDrag) => _modifierDrag,
            nameof(AppSettings.SnapWhileDragging) => _snapWhileDragging,
            nameof(AppSettings.OpacityWheel) => _opacityWheel,
            nameof(AppSettings.HideCaptureBorder) => _pipCaptureBorder,
            nameof(AnchorPreferences.Automatic) => _autoAnchors,
            nameof(AppSettings.PausePinWhenMaximized) => _pausePin,
            nameof(AppSettings.OpaqueOnHover) => _opaqueOnHover,
            nameof(AppSettings.HighlightPinned) => _highlightPinned,
            nameof(AppSettings.PipAutoMove) => _pipAutoMove,
            nameof(AppSettings.PipMiddleClickScroll) => _pipMiddleScroll,
            nameof(PipHoverPreferences.ChooseForMe) => _hoverAutomatic,
            nameof(PipHoverPreferences.Browsers) => _hoverBrowsers,
            nameof(PipHoverPreferences.Firefox) => _hoverFirefox,
            nameof(PipHoverPreferences.Electron) => _hoverElectron,
            nameof(PipHoverPreferences.FileExplorer) => _hoverExplorer,
            _ => _startWithWindows,
        };
        box.Checked = on;
    }

    private void LoadValues()
    {
        AppSettings values = _pendingImport ?? _settings;
        _excluded.Items.Clear();
        _included.Items.Clear();
        _setups.Items.Clear();
        _newExcluded.Text = "";
        _newIncluded.Text = "";
        _errors.Clear();
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            box.Text = values.Hotkeys.GetValueOrDefault(command, "");
        _excluded.Items.AddRange(values.ExcludedPrograms.Cast<object>().ToArray());
        _included.Items.AddRange(values.IncludedPrograms.Cast<object>().ToArray());
        _setups.Items.AddRange(values.SavedSetups.Cast<object>().ToArray());
        _startWithWindows.Checked = _startup.IsOn;
        _showToolbar.Checked = values.ShowToolbar;
        ToolbarPreferences toolbar = values.Toolbar.Validated();
        _toolbarPlacement.SelectedIndex = (int)toolbar.Placement;
        _toolbarTrigger.SelectedIndex = (int)toolbar.Trigger;
        _toolbarDelay.Value = toolbar.HoverDelayMilliseconds;
        for (int i = 0; i < _toolbarButtons.Items.Count; i++)
            _toolbarButtons.SetItemChecked(i, toolbar.Buttons.Contains(((ToolbarChoice)_toolbarButtons.Items[i]).Command));
        AnchorPreferences anchors = values.Anchors.Validated();
        _autoAnchors.Checked = anchors.Automatic;
        _anchorSide.SelectedIndex = (int)anchors.Side;
        _anchorLook.SelectedIndex = (int)anchors.Style;
        _anchorLimit.Value = anchors.MaxAutomatic;
        _anchorSize.Value = anchors.Size;
        _anchorRecency.Checked = anchors.RecencyEmphasis;
        _anchorAvoid.Checked = anchors.AvoidContent;
        _anchorOnly.Items.Clear();
        _anchorOnly.Items.AddRange(anchors.OnlyPrograms.Cast<object>().ToArray());
        _anchorExcluded.Items.Clear();
        _anchorExcluded.Items.AddRange(anchors.ExcludedPrograms.Cast<object>().ToArray());
        _showNotifications.Checked = values.ShowNotifications;
        _automaticUpdates.Checked = values.AutomaticUpdates;
        _experimental.Checked = values.ExperimentalFeatures;
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, values.Language));
        _theme.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, values.Theme));
        _pauseInFullScreen.Checked = values.PauseInFullScreen;
        _allowPip.Checked = values.AllowPipInFullScreen;
        _modifierDrag.Checked = values.ModifierDrag;
        _modifierKey.SelectedIndex = (int)values.ModifierKey;
        _snapWhileDragging.Checked = values.SnapWhileDragging;
        _windowMenu.Checked = values.WindowMenu;
        _windowMenuGesture.SelectedIndex = (int)values.WindowMenuGesture;
        _windowMenuExclusions.Items.Clear();
        _windowMenuExclusions.Items.AddRange(values.WindowMenuExclusions.Cast<object>().ToArray());
        _modifierExclusions.Items.Clear();
        _modifierExclusions.Items.AddRange(values.ModifierExclusions.Cast<object>().ToArray());
        _pausePin.Checked = values.PausePinWhenMaximized;
        _opaqueOnHover.Checked = values.OpaqueOnHover;
        _highlightPinned.Checked = values.HighlightPinned;
        _pipAutoMove.Checked = values.PipAutoMove;
        _pipMiddleScroll.Checked = values.PipMiddleClickScroll;
        _loadingHover = true;
        _hover = values.PipHover;
        _hoverAutomatic.Checked = values.PipHover.ChooseForMe;
        ShowHoverChoices();
        _loadingHover = false;
        _originalPrograms.Items.Clear();
        _originalPrograms.Items.AddRange(values.PipOriginalPrograms.Cast<object>().ToArray());
        _hoverPrograms.Items.Clear();
        _hoverPrograms.Items.AddRange(values.PipHover.Programs.Cast<object>().ToArray());
        _addedApps.Clear();
        RebuildApps();
        PipPreferences pip = values.Pip.Validated();
        if (!_pipRates.Contains(pip.FrameRate)) _pipRates.Add(pip.FrameRate);
        int refresh = Pip.PipDisplay.CachedRefresh(Handle);
        if (!_pipRates.Contains(refresh)) _pipRates.Add(refresh);
        _pipRates.Sort();
        _pipRate.Items.Clear();
        foreach (int rate in _pipRates) _pipRate.Items.Add(rate == 0 ? T("pip.frameRate.matchMonitor") : rate + " FPS");
        _pipBackend.SelectedIndex = (int)pip.Backend;
        _pipRate.SelectedIndex = _pipRates.IndexOf(pip.FrameRate);
        _pipScaling.SelectedIndex = (int)pip.Scaling;
        _pipOpacity.Value = pip.OpacityPercent;
        _pipDiagnostics.Checked = pip.ShowDiagnostics;
        _pipCaptureBorder.Checked = values.HideCaptureBorder;
        _pipTaskbar.Checked = values.PipViews.MinimizeToTaskbar;
        _pipPreview.SelectedIndex = (int)values.PipViews.Validated().HoverPreview;
        _opacityStep.Value = Math.Clamp(values.OpacityStepPercent, 1, 25);
        _opacityWheel.Checked = values.OpacityWheel;
        _detectDark.Checked = values.DetectAlreadyDark;
        EffectPreferences effects = values.Effects.Validated();
        _layeredGlass.Checked = effects.LayeredGlass;
        _pictureSafeDark.Checked = effects.PictureSafeDark;
        _glassBackground.Value = effects.BackgroundPercent;
        _glassText.Value = effects.TextPercent;
        _glassPictures.Value = effects.PicturePercent;
        PinBorderPreferences border = values.PinBorder.Validated();
        _borderColor.Text = border.ColorHex;
        _borderOpacity.Value = border.OpacityPercent;
        _borderThickness.Value = border.Thickness;
        _defaultOpacity.Value = Math.Clamp(values.DefaultOpacityPercent, AppSettings.LowestOpacityPercent,
            AppSettings.HighestOpacityPercent);
        RefreshFeatures();
    }

    private static TabPage Tab(string title, Control content)
    {
        var tab = new TabPage(title) { Padding = new Padding(0, 4, 0, 0), UseVisualStyleBackColor = false, BackColor = SettingsStyle.Page };
        tab.Controls.Add(content is ScrollPage page ? new ScrollHost(page) : content);
        return tab;
    }

    /// <summary>A line of explanation under the option it belongs to.</summary>
    private static Label Note(string text) => new SoftLabel()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(SwitchBox.RowWidth - 8, 0), Margin = new Padding(7, 0, 3, 10),
        ForeColor = SettingsStyle.Secondary, Font = MenuStyle.SmallFont, UseMnemonic = false,
    };

    /// <summary>A titled group of options.</summary>
    private static SettingsCard Section(string title, params Control[] items) => Card(title, false, items);

    private static SettingsCard Card(string title, bool experimental, params Control[] items)
    {
        foreach (Control item in items)
            if (item is CheckBox option) option.Margin = new Padding(3, 0, 3, 0);
        return new SettingsCard(title, experimental, ContentWidth, items);
    }

    /// <summary>One option: what it is on the left, the control that sets it on the right.</summary>
    private static Control SettingRow(string text, Control control)
    {
        // A box typed into gets the same rounded frame and width as every other one.
        if (control is NumericUpDown number) control = new InputFrame(number) { Width = NumberWidth };
        else if (control is TextBox box) control = new InputFrame(box);
        else if (control is ComboBox) control.Width = ChoiceWidth;
        text = text.TrimEnd(':', '：', ' ');
        var row = new Panel { Size = new Size(SwitchBox.RowWidth, Math.Max(42, control.Height + 10)), Margin = new Padding(3, 0, 3, 0) };
        control.Margin = Padding.Empty;
        control.Location = new Point(row.Width - control.Width - 6, (row.Height - control.Height) / 2);
        control.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(control);
        row.Controls.Add(new SoftLabel { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0) });
        return row;
    }

    /// <summary>A page that scrolls: cards one under another.</summary>
    private static FlowLayoutPanel ScrollingPage() => new ScrollPage
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true,
        BackColor = SettingsStyle.Page, Padding = new Padding(0, 0, 6, 0),
    };

    private FlowLayoutPanel BuildGeneral()
    {
        _language.Width = 170;
        Control language = SettingRow(T("settings.language").TrimEnd(':', '：'), _language);
        Control theme = SettingRow(T("settings.theme").TrimEnd(':', '：'), _theme);

        var startup = new List<Control> { language, theme, _startWithWindows, _showNotifications, _automaticUpdates };
        bool updatesWork = VelopackUpdateSession.IsConfigured && VelopackUpdateSession.IsInstalled;
        _automaticUpdates.Enabled = updatesWork;
        if (!VelopackUpdateSession.IsConfigured)
            startup.Add(Note(T("updates.repositoryRequired")));
        else if (!VelopackUpdateSession.IsInstalled)
            startup.Add(Note(T("updates.installedOnly")));

        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("settings.startupAndUpdates"), startup.ToArray()));
        page.Controls.Add(Section(T("settings.windows"), _pauseInFullScreen, _allowPip));
        page.Controls.Add(Section(T("settings.experimental"), _experimental));
        page.Controls.Add(BuildTransfer());
        page.Controls.Add(new SoftLabel
        {
            Text = string.Format(T("app.version"), AppInfo.Version), AutoSize = true,
            ForeColor = SettingsStyle.Secondary, Font = MenuStyle.SmallFont, Margin = new Padding(3, 2, 3, 0),
        });
        return page;
    }

    private FlowLayoutPanel BuildModifierDrag()
    {
        _modifierKey.Items.AddRange(["Alt", "Win"]);
        Control key = SettingRow(T("gestures.moveResize.modifier").TrimEnd(':', '：'), _modifierKey);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("gestures.moveResize.title"), _modifierDrag, key, _snapWhileDragging,
            ProgramRules(T("gestures.exclusions"), _modifierExclusions, _newModifierExclusion)));
        page.Controls.Add(Note(T("gestures.moveResize.help")));
        _windowMenuGesture.Items.AddRange([T("gestures.menu.ctrlRight"), T("gestures.menu.ctrlShiftRight"), T("gestures.menu.ctrlMiddle")]);
        page.Controls.Add(Section(T("window.menu"), _windowMenu, SettingRow(T("gestures.menu.gesture"), _windowMenuGesture),
            ProgramRules(T("gestures.exclusions"), _windowMenuExclusions, _newMenuExclusion)));
        page.Controls.Add(Note(T("gestures.menu.help")));
        page.Controls.Add(Note(T("gestures.hookHelp")));
        page.Controls.Add(Note(T("programs.alwaysLeftAlone")));
        return page;
    }

    private FlowLayoutPanel BuildPip()
    {
        _pipPreview.Items.AddRange([T("pip.hover.off"), T("pip.hover.whole"), T("pip.hover.lens")]);
        _pipBackend.Items.AddRange([T("common.auto"), T("pip.backend.compatibility"), T("pip.backend.advanced")]);
        _pipScaling.Items.AddRange([T("pip.scaling.auto"), T("pip.scaling.crisp"), T("pip.scaling.smooth")]);
        _pipBackend.SelectedIndexChanged += (_, _) =>
        {
            _pipRate.Enabled = _pipScaling.Enabled = _pipBackend.SelectedIndex != (int)PipBackendPreference.Dwm;
        };
        _pipRate.DropDown += (_, _) =>
        {
            int refresh = Pip.PipDisplay.CachedRefresh(Handle);
            if (_pipRates.Contains(refresh)) return;
            int chosen = _pipRates[Math.Max(0, _pipRate.SelectedIndex)];
            _pipRates.Add(refresh); _pipRates.Sort();
            _pipRate.Items.Clear();
            foreach (int rate in _pipRates) _pipRate.Items.Add(rate == 0 ? T("pip.frameRate.matchMonitor") : rate + " FPS");
            _pipRate.SelectedIndex = _pipRates.IndexOf(chosen);
        };
        static Control Row(string label, Control control) => SettingRow(label, control);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("pip.open"), Row(T("pip.rendering"), _pipBackend), Row(T("pip.frameRate"), _pipRate),
            Note(T("pip.rendering.compatibilityHelp")), Note(T("pip.rendering.captureIndicatorHelp")),
            Row(T("pip.scaling"), _pipScaling), Row(T("pip.opacityPercent"), _pipOpacity), _pipCaptureBorder, _pipDiagnostics,
            Note(T("pip.diagnostics.retryHelp"))));
        page.Controls.Add(Card(T("settings.pip.moving"), true, _pipAutoMove, Note(T("settings.pip.autoMoveHelp"))));
        page.Controls.Add(Card(T("pip.interactive"), true, _pipMiddleScroll, Note(T("settings.pip.middleScrollHelp"))));
        // One table says, for each kind of app and each program added, what a view of it does.
        _hoverOptions = new Control();
        _hoverOptions.EnabledChanged += (_, _) => { ShowHoverChoices(); RebuildApps(); };
        foreach (CheckBox kind in new[] { _hoverBrowsers, _hoverFirefox, _hoverElectron, _hoverExplorer })
        {
            ((SwitchBox)kind).Compact = true;
            kind.AccessibleName = kind.Text;
        }
        _newApp.Dock = DockStyle.None;
        _newApp.Width = 300;
        var add = new PlainButton { Text = T("common.add"), Enabled = false };
        _newApp.TextChanged += (_, _) => add.Enabled = _newApp.Text.Trim().Length > 0; // Nothing to add until a name is typed.
        void AddApp()
        {
            string name = ProgramName(_newApp.Text);
            _newApp.Text = "";
            if (name.Length == 0) return;
            if (!_addedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _addedApps.Add(name);
            // A program is added because it doesn't work through a view; Enhanced hover is the other choice on its row.
            if (!_hoverPrograms.Items.Cast<string>().Contains(name, StringComparer.OrdinalIgnoreCase)) AddProgram(_originalPrograms, name);
            RebuildApps();
            QueueApplyRefresh();
        }
        add.Click += (_, _) => AddApp();
        _newApp.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_newApp.DroppedDown) _newApp.DroppedDown = false; // Enter on a suggestion takes it and adds it.
            AddApp();
        };
        // Both centred on one line, as the program lists on other pages place them: anchored to neither top nor bottom.
        _newApp.Margin = new Padding(0, 0, 8, 0);
        add.Margin = Padding.Empty;
        _newApp.Anchor = add.Anchor = AnchorStyles.Left;
        var adding = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 3, 6), BackColor = SettingsStyle.Card };
        adding.Controls.Add(_newApp);
        adding.Controls.Add(add);
        var parts = new List<Control> { Note(T("settings.pip.apps.help")), _hoverAutomatic, _appRows, adding, Note(T("settings.pip.hover.programsHelp")) };
        if (!Pip.PipEnhancedHover.Supported) parts.Add(Note(T("settings.pip.hover.unsupported")));
        page.Controls.Add(Card(T("settings.pip.apps"), true, parts.ToArray()));
        page.Controls.Add(Section(T("pip.hover.title"), _pipTaskbar, Row(T("pip.hover.mode"), _pipPreview), Note(T("pip.hover.windowsOnly"))));
        return page;
    }

    private FlowLayoutPanel BuildPrograms()
    {
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("programs.exclusions"), Programs(_excluded, _newExcluded, null, T("programs.empty.excluded"))));
        page.Controls.Add(Section(T("programs.inclusions"), Programs(_included, _newIncluded, null, T("programs.empty.included"))));
        page.Controls.Add(Note(T("programs.precedence")));
        return page;
    }

    /// <summary>A list of programs with a box to add one, as every page that has one shows it.</summary>
    private static ProgramList Programs(ThemedList list, ComboBox picker, string? title, string empty) =>
        new(list, picker, title, empty, T("common.add"), T("common.remove"));

    private FlowLayoutPanel BuildSetups()
    {
        FlowLayoutPanel page = ScrollingPage();
        var setups = new ProgramList(_setups, null, null, T("settings.setups.empty"), T("common.add"), T("common.remove"),
            item => RemoveSetup((ProgramSetup)item));
        page.Controls.Add(Section(T("settings.savedSetups"), Note(T("settings.setups.help")), setups));
        return page;
    }

    private Control BuildButtons()
    {
        var save = new PlainButton { Text = T("settings.save"), Primary = true, Anchor = AnchorStyles.Right };
        save.Click += (_, _) => ApplyChanges();
        AcceptButton = null;
        var discard = new PlainButton { Text = T("settings.discard"), Anchor = AnchorStyles.Right };
        discard.Click += (_, _) =>
        {
            _pendingImport = null;
            _removedSetups.Clear();
            LoadValues();
            _status.Text = "";
            RefreshApply();
        };
        _applyButton.Click += (_, _) => ApplyChanges();

        var text = new SoftLabel { Text = T("settings.unsaved"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SettingsStyle.Accent,
            BackColor = SettingsStyle.AccentBack, Margin = new Padding(12, 0, 0, 0) };
        var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SettingsStyle.AccentBack, Padding = new Padding(0, 6, 8, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(text, 0, 0);
        row.Controls.Add(discard, 1, 0);
        row.Controls.Add(save, 2, 0);
        _saveBar.Controls.Add(row);
        _saveBar.AutoSize = true; // As tall as its buttons, whatever the display scale.
        _saveBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _saveBar.BackColor = SettingsStyle.AccentBack;
        _saveBar.Dock = DockStyle.None;
        _saveBar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _saveBar.Margin = new Padding(0, 8, 0, 0);
        return _saveBar;
    }

    private Control BuildTransfer()
    {
        var export = new PlainButton { Text = T("settings.transfer.export"), AutoSize = true };
        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", DefaultExt = "json", FileName = "WinScreen-settings.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { SettingsTransfer.Export(_settings, dialog.FileName); _status.Text = T("settings.transfer.exported"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        };
        var import = new PlainButton { Text = T("settings.transfer.import"), AutoSize = true };
        import.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                _pendingImport = SettingsTransfer.Import(dialog.FileName, _settings);
                _removedSetups.Clear();
                LoadValues();
                _status.Text = T("settings.transfer.review");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            { _status.Text = e.Message; }
        };
        export.Margin = new Padding(0, 0, 8, 0);
        import.Margin = Padding.Empty;
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 3, 8), BackColor = SettingsStyle.Card };
        buttons.Controls.Add(export);
        buttons.Controls.Add(import);
        return Section(T("settings.transfer.title"), buttons, Note(T("settings.transfer.paths")));
    }

    private FlowLayoutPanel BuildCustomization()
    {
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("settings.opacity.title"), SettingRow(T("settings.opacity.default"), _defaultOpacity),
            SettingRow(T("settings.opacity.step"), _opacityStep), _opacityWheel, _opaqueOnHover));
        var color = new InputFrame(_borderColor) { Width = NumberWidth + 10, Swatch = () => HexColor(_borderColor.Text) };
        _borderColor.TextChanged += (_, _) => color.Invalidate();
        page.Controls.Add(Section(T("settings.pin.border"), _pausePin, _highlightPinned, SettingRow(T("settings.pin.color"), color),
            SettingRow(T("settings.pin.opacity"), _borderOpacity), SettingRow(T("settings.pin.thickness"), _borderThickness)));
        page.Controls.Add(Card(T("settings.effects.title"), true, _detectDark, _layeredGlass,
            SettingRow(T("settings.effects.background"), _glassBackground), SettingRow(T("settings.effects.text"), _glassText),
            SettingRow(T("settings.effects.pictures"), _glassPictures), _pictureSafeDark, Note(T("settings.effects.help"))));
        return page;
    }

    /// <summary>The colour a #RRGGBB box holds, or null while what's typed isn't one yet.</summary>
    private static Color? HexColor(string text) =>
        text.Trim() is { Length: 7 } hex && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int rgb)
            ? Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF) : null;

    private Control BuildToolbar()
    {
        _toolbarPlacement.Items.AddRange([T("toolbar.placement.left"), T("toolbar.placement.centre"), T("toolbar.placement.right")]);
        _toolbarTrigger.Items.AddRange([T("toolbar.trigger.titleBar"), T("toolbar.trigger.minimize")]);
        foreach (AppCommand command in ToolbarPreferences.DefaultButtons()) _toolbarButtons.Items.Add(new ToolbarChoice(command));
        _toolbarButtons.Tagged = item => item is ToolbarChoice { Command: var command } && CommandCatalog.IsExperimental(command);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("toolbar.settings.title"), _showToolbar, SettingRow(T("toolbar.placement.label"), _toolbarPlacement),
            SettingRow(T("toolbar.trigger.label"), _toolbarTrigger), SettingRow(T("toolbar.delay.label"), _toolbarDelay),
            Note(T("toolbar.trigger.help"))));
        var buttons = new InputFrame(_toolbarButtons, InputFrame.Look.List) { Width = SwitchBox.RowWidth, Margin = new Padding(3, 6, 3, 8) };
        void Fit() => buttons.Height = _toolbarButtons.RowHeight * _toolbarButtons.Items.Count + SettingsStyle.Unit(buttons, 8);
        Fit();
        _toolbarButtons.FontChanged += (_, _) => Fit();
        buttons.DpiChangedAfterParent += (_, _) => Fit();
        page.Controls.Add(Section(T("toolbar.buttons.label"), buttons, Note(T("toolbar.buttons.help"))));
        return page;
    }

    private Control BuildAnchors()
    {
        _anchorSide.Items.AddRange([T("anchors.side.right"), T("anchors.side.left"), T("anchors.side.near")]);
        _anchorLook.Items.AddRange([T("anchors.look.dark"), T("anchors.look.light")]);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("anchors.settings.title"), _autoAnchors, Note(T("anchors.auto.help")),
            SettingRow(T("anchors.side.label"), _anchorSide), SettingRow(T("anchors.look.label"), _anchorLook),
            SettingRow(T("anchors.limit"), _anchorLimit), SettingRow(T("anchors.size"), _anchorSize),
            _anchorRecency, _anchorAvoid, Note(T("anchors.avoid.help"))));
        page.Controls.Add(Section(T("anchors.auto.only"), Programs(_anchorOnly, _newAnchorOnly, null, T("programs.empty.included"))));
        page.Controls.Add(Section(T("anchors.auto.excluded"), Programs(_anchorExcluded, _newAnchorExcluded, null, T("programs.empty.excluded"))));
        page.Controls.Add(Note(T("programs.alwaysLeftAlone")));
        return page;
    }

    private static Control ProgramRules(string title, ThemedList list, ComboBox picker) =>
        Programs(list, picker, title.TrimEnd(':', '：'), T("programs.empty.excluded"));

    private Control BuildFeatures()
    {
        _features.HideSelection = false;
        _features.Columns.Add(T("features.name"), 160);
        _features.Columns.Add(T("actions.target"), 100);
        _features.Columns.Add(T("actions.default"), 120);
        _features.Columns.Add(T("actions.saved"), 120);
        _features.Columns.Add(T("features.state"), 160);
        foreach (AppCommand command in CommandCatalog.All)
        {
            var box = new TextBox { ReadOnly = true, ShortcutsEnabled = false, AccessibleName = CommandCatalog.Name(command) };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Tab) return;
                e.SuppressKeyPress = true;
                CaptureHotkey(command, e.KeyData, HotkeyText.WinKeyDown);
            };
            _hotkeys[command] = box;
        }
        var clear = new PlainButton { Text = T("actions.clear") };
        var reset = new PlainButton { Text = T("actions.reset") };
        void SetSelected(bool defaults)
        {
            if (_features.SelectedItems.Count == 0 || _features.SelectedItems[0].Tag is not AppCommand command) return;
            _hotkeys[command].Text = defaults ? new AppSettings().Hotkeys.GetValueOrDefault(command, "") : "";
            _hotkeys[command].Focus();
        }
        clear.Click += (_, _) => SetSelected(false);
        reset.Click += (_, _) => SetSelected(true);

        // The chosen action's shortcut, in a card under the table: its name, the box to press keys in, and the buttons.
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, BackColor = SettingsStyle.Card };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _shortcutLabel.Margin = new Padding(2, 0, 8, 0);
        _shortcutEditor.Margin = new Padding(0, 0, 8, 0);
        clear.Margin = new Padding(0, 0, 6, 0);
        reset.Margin = Padding.Empty;
        clear.Anchor = reset.Anchor = AnchorStyles.Right;
        editor.Controls.Add(_shortcutLabel, 0, 0);
        editor.Controls.Add(_shortcutEditor, 1, 0);
        editor.Controls.Add(clear, 2, 0);
        editor.Controls.Add(reset, 3, 0);
        Label help = Note(T("settings.hotkeys.captureHelp"));
        help.Margin = new Padding(2, 8, 0, 0);
        editor.Controls.Add(help, 0, 1);
        editor.SetColumnSpan(help, 4);
        var card = new CardPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 10) };
        card.Controls.Add(editor);
        _features.SelectedIndexChanged += (_, _) => ShowShortcutEditor();
        _actionSearch.AccessibleName = T("actions.search");
        _actionSearch.PlaceholderText = T("actions.search");
        _actionSearch.TextChanged += (_, _) => RefreshFeatures();

        DrawFeatures();
        var table = new InputFrame(_features, InputFrame.Look.List) { Dock = DockStyle.Fill };
        var page = new Panel { Dock = DockStyle.Fill };
        page.Controls.Add(table);
        page.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        page.Controls.Add(card);
        var intro = new SoftLabel { Text = T("features.help"), AutoSize = true, Dock = DockStyle.Top, ForeColor = SettingsStyle.Secondary, Padding = new Padding(1, 8, 0, 8) };
        page.Controls.Add(intro);
        page.Controls.Add(new InputFrame(_actionSearch) { Glyph = MenuParts.Search, Dock = DockStyle.Top, Height = 34 });
        page.Resize += (_, _) => intro.MaximumSize = new Size(page.ClientSize.Width, 0);
        _features.ShowItemToolTips = true;
        _features.Resize += (_, _) => FitFeatureColumns();
        _features.ClientSizeChanged += (_, _) => FitFeatureColumns();
        return page;
    }

    /// <summary>
    /// Draws the table of actions in the window's colours: Windows draws its headings unreadable in the dark theme,
    /// and its chosen row hard to tell apart.
    /// </summary>
    private void DrawFeatures()
    {
        _features.OwnerDraw = true;
        _features.BackColor = SettingsStyle.Card;
        _features.ForeColor = SettingsStyle.Text;
        int Unit(int value) => SettingsStyle.Unit(_features, value);
        // A list of tiny pictures is the way to set the height of a row.
        void RowHeight() => _features.SmallImageList = new ImageList { ImageSize = new Size(1, Math.Max(Unit(32), _features.Font.Height + Unit(14))) };
        _features.HandleCreated += (_, _) => RowHeight();
        _features.DrawColumnHeader += (_, e) =>
        {
            Graphics g = e.Graphics;
            SettingsStyle.SoftText(g);
            using (var back = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(g, e.Header?.Text, MenuStyle.SmallFont,
                new Rectangle(e.Bounds.Left + Unit(FeatureInset(e.ColumnIndex)), e.Bounds.Top, e.Bounds.Width - Unit(FeatureInset(e.ColumnIndex) + 4), e.Bounds.Height),
                SettingsStyle.Secondary, FeatureText);
            using var line = new Pen(SettingsStyle.Divider);
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        _features.DrawItem += (_, _) => { }; // Each cell draws itself, the first one the row's background too.
        _features.DrawSubItem += (_, e) =>
        {
            if (e.Item is not { } item || e.SubItem is null) return;
            Graphics g = e.Graphics;
            SettingsStyle.SoftText(g);
            Rectangle cell = e.Bounds;
            if (e.ColumnIndex == 0)
            {
                Rectangle row = item.Bounds;
                using (var back = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(back, row);
                var wash = new Rectangle(row.Left + Unit(2), row.Top + Unit(1), row.Width - Unit(4), row.Height - Unit(2));
                if (item.Selected)
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var path = SettingsStyle.Rounded(wash, Unit(4));
                    using var brush = new SolidBrush(SettingsStyle.RowSelectedOn(SettingsStyle.Card));
                    g.FillPath(brush, path);
                    SettingsStyle.SelectionBar(g, wash, _features);
                    if (item.Focused && _features.Focused) SettingsStyle.FocusRing(g, wash, Unit(4), _features);
                }
                cell = new Rectangle(row.Left, row.Top, _features.Columns[0].Width, row.Height);
            }
            bool problem = e.ColumnIndex == 4 && item.Tag is AppCommand command && _shortcutProblems.ContainsKey(command);
            Color color = problem ? SettingsStyle.Error : e.ColumnIndex is 0 or 3 ? SettingsStyle.Text : SettingsStyle.Secondary;
            int inset = Unit(FeatureInset(e.ColumnIndex));
            // An experimental action's name has the tag after it, as in the menus; the name gives way to it if short of room.
            bool tagged = e.ColumnIndex == 0 && item.Tag is AppCommand action && CommandCatalog.IsExperimental(action);
            int tag = tagged ? Unit(8) + MenuParts.TagSize(Unit).Width : 0;
            const TextFormatFlags format = FeatureText;
            var words = new Rectangle(cell.Left + inset, cell.Top, Math.Max(0, cell.Width - inset - Unit(4) - tag), cell.Height);
            TextRenderer.DrawText(g, e.SubItem.Text, _features.Font, words, color, format);
            if (tagged && words.Width > 0)
            {
                int width = Math.Min(words.Width, TextRenderer.MeasureText(g, e.SubItem.Text, _features.Font, words.Size, format).Width);
                MenuParts.DrawTag(g, words.Left + width + Unit(8), cell.Top + cell.Height / 2, Unit);
            }
        };
    }

    /// <summary>How the table's words are set, measured the same way as drawn so a column fits its words exactly.</summary>
    private const TextFormatFlags FeatureText = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private const TextFormatFlags FeatureMeasure = FeatureText & ~TextFormatFlags.EndEllipsis; // Measuring with it would cut them short.

    /// <summary>Where a column's words start, at 100% scaling: the first further in, past the bar of a chosen row.</summary>
    private static int FeatureInset(int column) => column == 0 ? 18 : 12;

    /// <summary>
    /// Gives each column of the table the room its longest entry needs and the name column the rest, so nothing is cut
    /// short while there is room. Columns can still be dragged wider; a cut-short entry shows in full when pointed at.
    /// </summary>
    private void FitFeatureColumns()
    {
        // Sizes still change while the window closes, when the table's rows are already being taken apart.
        if (_fillingFeatures || Disposing || IsDisposed || _features.Disposing || !_features.IsHandleCreated) return;
        // What shows: Windows' scroll bar is hidden past the edge, so the table may be wider than that.
        int width = Math.Min(_features.ClientSize.Width, _features.Parent?.ClientSize.Width ?? int.MaxValue);
        width -= SettingsStyle.Unit(_features, ThinScrollBar.BarWidth);
        if (width <= 0 || _features.Columns.Count < 5) return;
        int pad = SettingsStyle.Unit(_features, FeatureInset(1) + 4);
        var need = new int[5];
        for (int column = 1; column < 5; column++)
        {
            int widest = TextRenderer.MeasureText(_features.Columns[column].Text, MenuStyle.SmallFont, Size.Empty, FeatureMeasure).Width;
            foreach (ListViewItem? item in _features.Items)
                if (item is not null && column < item.SubItems.Count)
                    widest = Math.Max(widest, TextRenderer.MeasureText(item.SubItems[column].Text, _features.Font, Size.Empty, FeatureMeasure).Width);
            need[column] = widest + pad;
        }
        // A problem can be a whole sentence; the state column stays short and shows it in full when pointed at.
        need[4] = Math.Min(need[4], SettingsStyle.Unit(_features, 150));
        // The name column keeps room for an experimental action's whole name and its tag; the others give way first.
        int least = SettingsStyle.Unit(_features, 150);
        int tag = SettingsStyle.Unit(_features, FeatureInset(0) + 8 + 4) + MenuParts.TagSize(value => SettingsStyle.Unit(_features, value)).Width;
        foreach (ListViewItem? item in _features.Items)
            if (item?.Tag is AppCommand command && CommandCatalog.IsExperimental(command))
                least = Math.Max(least, TextRenderer.MeasureText(item.Text, _features.Font, Size.Empty, FeatureMeasure).Width + tag);
        int rest = need.Skip(1).Sum();
        if (width - rest < least)
        {
            double share = Math.Max(0, width - least) / (double)rest;
            for (int column = 1; column < 5; column++) need[column] = (int)(need[column] * share);
        }
        need[0] = width - need.Skip(1).Sum();
        for (int column = 0; column < 5; column++)
            if (_features.Columns[column].Width != need[column]) _features.Columns[column].Width = need[column];
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

    private void ShowShortcutEditor()
    {
        _shortcutLabel.Text = "";
        if (_features.SelectedItems.Count == 0 || _features.SelectedItems[0].Tag is not AppCommand command)
        {
            _shortcutEditor.Host(null);
            _shortcutEditor.Enabled = false;
            return;
        }
        _shortcutLabel.Text = CommandCatalog.Name(command).Replace("&", "");
        _shortcutEditor.Enabled = true;
        _shortcutEditor.Host(_hotkeys[command]);
    }

    internal void RefreshFeatures()
    {
        object? selected = _features.SelectedItems.Count > 0 ? _features.SelectedItems[0].Tag : null;
        _features.BeginUpdate();
        _fillingFeatures = true; // The rows are half there until the end; the columns are fitted then.
        _features.Items.Clear();
        var defaults = new AppSettings();
        foreach (AppCommand command in CommandCatalog.All)
        {
            string name = CommandCatalog.Name(command).Replace("&", "");
            string target = command is AppCommand.RestoreAll or AppCommand.PickPipSource
                ? T("actions.target.app") : T("actions.target.window");
            string saved = _settings.Hotkeys.GetValueOrDefault(command, "");
            string state = _shortcutProblems.GetValueOrDefault(command) ??
                (CommandCatalog.IsExperimental(command) && !_settings.ExperimentalFeatures ? T("features.disabled") :
                 saved.Length == 0 ? T("features.unassigned") :
                 _registered?.Invoke(command) == true ? T("actions.registered") : T("actions.unavailable"));
            string[] values = [name, target, defaults.Hotkeys.GetValueOrDefault(command, ""), saved, state];
            if (!string.IsNullOrWhiteSpace(_actionSearch.Text) && !values.Any(value =>
                value.Contains(_actionSearch.Text, StringComparison.CurrentCultureIgnoreCase))) continue;
            var row = new ListViewItem(values) { Tag = command, ToolTipText = string.Join(Environment.NewLine, values) };
            _features.Items.Add(row);
            if (Equals(selected, command)) row.Selected = true;
        }
        void Option(string name, string shortcut, bool enabled, string page)
        {
            string[] values = [name.Replace("&", ""), T("actions.target.app"), shortcut, shortcut,
                enabled ? T("features.enabled") : T("features.disabled")];
            if (!string.IsNullOrWhiteSpace(_actionSearch.Text) && !values.Any(value =>
                value.Contains(_actionSearch.Text, StringComparison.CurrentCultureIgnoreCase))) return;
            var row = new ListViewItem(values) { Tag = page, ToolTipText = string.Join(Environment.NewLine, values) };
            _features.Items.Add(row);
            if (Equals(selected, page)) row.Selected = true;
        }
        string none = T("features.noShortcut");
        bool toolbarHasButtons = _toolbarButtons.CheckedItems.Cast<ToolbarChoice>().Any(choice =>
            !CommandCatalog.IsExperimental(choice.Command) || _experimental.Checked);
        Option(T("toolbar.settings.title"), none, _showToolbar.Checked && toolbarHasButtons, "toolbar");
        Option(T("features.moveResize"), string.Format(T("features.dragShortcut"), _modifierKey.SelectedIndex == (int)ModifierKey.Win ? "Win" : "Alt"), _modifierDrag.Checked, "gestures");
        string gesture = _windowMenuGesture.SelectedIndex switch
        {
            (int)WindowMenuGesture.CtrlShiftRightClick => T("gestures.menu.ctrlShiftRight"),
            (int)WindowMenuGesture.CtrlMiddleClick => T("gestures.menu.ctrlMiddle"),
            _ => T("gestures.menu.ctrlRight"),
        };
        Option(T("features.windowMenu"), gesture, _windowMenu.Checked, "gestures");
        Option(T("features.autoAnchors"), none, _autoAnchors.Checked, "anchors");
        Option(T("features.opacityWheel"), T("features.wheelShortcut"), _opacityWheel.Checked, "appearance");
        Option(_detectDark.Text, none, _detectDark.Checked && _experimental.Checked, "appearance");
        Option(_pipTaskbar.Text, none, _pipTaskbar.Checked, "pip");
        Option(T("pip.hover.mode"), none, _pipPreview.SelectedIndex > 0, "pip");
        _features.EndUpdate();
        _fillingFeatures = false;
        FitFeatureColumns();
        if (_features.SelectedItems.Count == 0 && _features.Items.Count > 0) _features.Items[0].Selected = true;
        ShowShortcutEditor();
    }
}
