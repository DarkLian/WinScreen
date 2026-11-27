using static WinScreen.Localization.UiText;
using System.Diagnostics;
using WinScreen.Tray;
using WinScreen.Updates;

namespace WinScreen.Settings;

// A change is applied and saved once it has settled: not while a slider is held or a list is open, and never
// half-typed.
public sealed partial class SettingsWindow : Form
{
    private const int ContentWidth = SwitchBox.RowWidth + 20;
    private const int NumberWidth = 120;

    private readonly AppSettings _settings;
    private AppSettings? _pendingImport;
    private readonly CheckBox _opacityWheel = new SwitchBox { Text = T("settings.opacity.wheel"), AutoSize = true };
    private readonly CheckBox _layeredGlass = new SwitchBox { Text = T("settings.effects.layeredGlass"), AutoSize = true };
    private readonly NumericUpDown _glassBackground = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassFade = new CurvedNumber { Minimum = 1, Maximum = EffectPreferences.MostFadeRange, Width = 70, Curve = CurvedNumber.FadeCurve };
    private readonly ComboBox _glassTextColor = TextColorPicker();
    private readonly SwitchBox _glassShadow = new() { Text = T("settings.effects.shadow"), AutoSize = true };
    private readonly CheckBox _detectDark = new SwitchBox { Text = T("settings.dark.detect"), AutoSize = true };
    private readonly NumericUpDown _darkPage = new() { Minimum = 0, Maximum = EffectPreferences.MostDarkPage, Width = 70 };
    private readonly NumericUpDown _darkText = new() { Minimum = EffectPreferences.LeastDarkText, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _darkColors = new() { Minimum = 0, Maximum = 100, Width = 70 };
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
    private readonly CheckBox _anchorPreview = new SwitchBox { Text = T("anchors.preview"), AutoSize = true };
    private readonly CheckBox _anchorOffTaskbar = new SwitchBox { Text = T("anchors.offTaskbar"), AutoSize = true };
    private readonly CheckBox _anchorEveryDesktop = new SwitchBox { Text = T("anchors.everyDesktop"), AutoSize = true };
    private readonly ThemedList _anchorOnly = new();
    private readonly ThemedList _anchorExcluded = new();
    private readonly ComboBox _newAnchorOnly = ProgramPicker();
    private readonly ComboBox _newAnchorExcluded = ProgramPicker();
    private readonly ComboBox _toolbarPlacement = new ThemedCombo { Width = 200 };
    private readonly ComboBox _toolbarTrigger = new ThemedCombo { Width = 200 };
    private readonly NumericUpDown _toolbarDelay = new() { Minimum = 100, Maximum = 2000, Increment = 100, Width = 90 };
    private readonly ThemedCheckList _toolbarButtons = new() { CheckOnClick = true, Width = SwitchBox.RowWidth - 8 };
    private readonly TabControl _tabs = new PagesControl { Size = new Size(540, 520), TabStop = false, Multiline = true };
    private readonly NavList _nav = new() { Dock = DockStyle.Fill, AccessibleName = T("settings.pages") };
    private readonly HintTextBox _search = new();
    private readonly Label _pageTitle = new SoftLabel() { AutoSize = true, Font = SettingsStyle.PageTitleFont, UseMnemonic = false, Margin = new Padding(0, 0, 0, 14) };
    private readonly HintTextBox _actionSearch = new() { Dock = DockStyle.Top };
    private readonly InputFrame _shortcutEditor = new(null) { Width = 240, ReserveEnd = 22, Anchor = AnchorStyles.Top | AnchorStyles.Left };
    private readonly Label _shortcutLabel = new SoftLabel() { AutoSize = true, Anchor = AnchorStyles.Left, Font = SettingsStyle.HeadingFont, UseMnemonic = false, AutoEllipsis = true };
    private readonly Dictionary<AppCommand, string> _shortcutProblems = [];
    private bool _fillingFeatures;
    private readonly Func<AppCommand, bool>? _registered;
    private readonly ThemedTable _features = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };

    private sealed record ToolbarChoice(AppCommand Command)
    {
        public override string ToString() => CommandCatalog.ButtonName(Command).Replace("&", "");
    }
    private readonly ComboBox _pipBackend = new ThemedCombo { Width = 240 };
    private readonly ComboBox _pipRate = new ThemedCombo { Width = 160 };
    private readonly ComboBox _pipScaling = new ThemedCombo { Width = 240 };
    private readonly NumericUpDown _pipOpacity = new() { Minimum = 10, Maximum = 100, Width = 75 };
    private readonly CheckBox _pipTaskbar = new SwitchBox { Text = T("pip.taskbar.enable"), AutoSize = true };
    private readonly CheckBox _pipTitleBar = new SwitchBox { Text = T("pip.titleBarAlways.default"), AutoSize = true };
    private readonly CheckBox _pipKeepHidden = new SwitchBox { Text = T("pip.keepHidden.default"), AutoSize = true };
    private readonly CheckBox _pipMinimizedOriginals = new SwitchBox { Text = T("pip.minimizedOriginals.taskbar"), AutoSize = true };
    private readonly ComboBox _pipPreview = new ThemedCombo { Width = 240 };
    private readonly ComboBox _pictureSave = new ThemedCombo { Width = 240 };
    private const int SaveOnly = 1, CopyOnly = 2; // As _pictureSave lists them, after both.
    private readonly PlainButton _pictureFolderChange = new() { Text = T("settings.pip.pictures.change") };
    private readonly PlainButton _pictureFolderOpen = new() { Text = T("settings.pip.pictures.open") };
    private SettingRowPanel? _pictureFolderRow;
    // Null is Windows' Screenshots folder.
    private string? _pictureFolder;
    private readonly PlainButton _recordingFolderChange = new() { Text = T("settings.pip.pictures.change") };
    private readonly PlainButton _recordingFolderOpen = new() { Text = T("settings.pip.pictures.open") };
    private SettingRowPanel? _recordingFolderRow;
    // Null is the Screen Recordings folder under Videos.
    private string? _recordingFolder;
    private readonly CheckBox _recordSound = new SwitchBox { Text = T("settings.pip.recordings.sound"), AutoSize = true };
    private readonly ComboBox _recordingRate = new ThemedCombo { Width = 240 };
    private readonly CheckBox _pipCaptureBorder = new SwitchBox { Text = T("pip.captureBorder.hide"), AutoSize = true };
    private readonly CheckBox _pipDiagnostics = new SwitchBox { Text = T("pip.diagnostics.includeHardware"), AutoSize = true };
    private readonly List<int> _pipRates = [.. PipPreferences.FrameRates];

    private readonly ComboBox _notifications = new ThemedCombo { Width = 240 };

    private readonly ComboBox _updateMode = new ThemedCombo { Width = 240 };

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
    private readonly CheckBox _runAsAdministrator = new SwitchBox { Text = T("settings.admin"), AutoSize = true };
    private readonly CheckBox _menuTransparency = new SwitchBox { Text = T("settings.menus.transparency"), AutoSize = true };

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
    private readonly ToolTip _tips = new();

    private readonly HashSet<ProgramSetup> _removedSetups = [];
    private string _savedState = "";
    private bool _refreshQueued;

    internal const int SettleMilliseconds = 300;
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = SettleMilliseconds };
    private bool _pending;
    private bool _applying;
    private bool _loading;

    private readonly Label _status = new SoftLabel()
        { AutoSize = true, ForeColor = SettingsStyle.Text, MaximumSize = new Size(500, 0), Margin = new Padding(0, 6, 0, 0) };

    public SettingsWindow(AppSettings settings, StartWithWindows startup,
        Func<AppSettings, IReadOnlyList<HotkeyProblem>> apply, Func<AppCommand, bool>? registered = null)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F); // Every size below is written for 100% display scaling.
        AutoScaleMode = AutoScaleMode.Dpi;
        FocusVisuals.Watch();
        _settings = settings;
        _startup = startup;
        _apply = apply;
        _registered = registered;
        _errors.ContainerControl = this;
        _experimental.CheckedChanged += (_, _) =>
        {
            _pipMiddleScroll.Enabled = _experimental.Checked;
            _hoverOptions?.Enabled = _experimental.Checked && Pip.PipEnhancedHover.Supported;
            if (_hotkeys.Count > 0) RefreshFeatures();
        };
        _hoverAutomatic.CheckedChanged += (_, _) => ShowHoverChoices();
        _language.Items.AddRange([T("settings.language.system"), "English", "简体中文", "繁體中文"]);
        _theme.Items.AddRange([T("settings.theme.system"), T("settings.theme.light"), T("settings.theme.dark")]);
        _updateMode.Items.AddRange([T("updates.mode.off"), T("updates.mode.tellMe"), T("updates.mode.install")]);
        _notifications.Items.AddRange([T("settings.notifications.all"), T("settings.notifications.problems"), T("settings.notifications.off")]);

        Text = T("settings.window.title");
        Icon = TrayApplication.LoadIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 660); // Room for every entry in the table of actions at Windows 11's text size.
        // No narrower than this, the narrowest its rows fit: they grow in a wider window but never shrink.
        MinimumSize = new Size(Width, 400);
        Font = SettingsStyle.BodyFont;
        BackColor = SettingsStyle.Page;
        ForeColor = SettingsStyle.Text;

        var tabs = _tabs;
        tabs.Dock = DockStyle.Fill;
        var generalPage = Tab(T("settings.general"), BuildGeneral());
        var featuresPage = Tab(T("features.title"), BuildFeatures());
        var programsPage = Tab(T("settings.programs"), BuildPrograms());
        var setupsPage = Tab(T("settings.savedSetups"), BuildSetups());
        var pipPage = Tab(T("pip.open"), BuildPip());
        var gesturePage = Tab(T("gestures.title"), BuildModifierDrag());
        var appearancePage = Tab(T("settings.customization"), BuildCustomization());
        var toolbarPage = Tab(T("toolbar.settings.title"), BuildToolbar());
        var anchorsPage = Tab(T("anchors.settings.title"), BuildAnchors());
        var backgroundsPage = _backgroundsPage = Tab(T("settings.backgrounds"), BuildBackgrounds());
        // In the order of the list at the side.
        foreach ((TabPage tab, char glyph) in new[]
                 {
                     (generalPage, MenuParts.Home), (pipPage, MenuParts.NewWindow), (backgroundsPage, MenuParts.Picture),
                     (anchorsPage, MenuParts.Pinned), (appearancePage, MenuParts.Palette), (gesturePage, MenuParts.Mouse),
                     (toolbarPage, MenuParts.Bar), (featuresPage, MenuParts.Keyboard), (programsPage, MenuParts.Apps),
                     (setupsPage, MenuParts.Save),
                 })
        {
            tabs.TabPages.Add(tab);
            _nav.Add(tab.Text, glyph, tab);
        }
        tabs.Selected += (_, _) =>
        {
            if (tabs.SelectedTab == featuresPage) RefreshFeatures();
            _nav.Selected = tabs.SelectedTab;
            _pageTitle.Text = tabs.SelectedTab?.Text ?? "";
            FollowColumn();
        };
        foreach (TabPage tab in tabs.TabPages)
            if (ColumnOf(tab) is { } column) column.Shifted += FollowColumn;
        tabs.SizeChanged += (_, _) => FollowColumn(); // Where the pages sit in it is known once it has a size.
        _nav.Selected = generalPage;
        _nav.Chosen += chosen => { tabs.SelectedTab = (TabPage)chosen; ShowSearchMatch(); };
        _search.Hint = _search.AccessibleName = T("settings.search");
        _search.TextChanged += (_, _) => SearchSettings();
        _goToPage = page => tabs.SelectedTab = page switch
        {
            "toolbar" => toolbarPage,
            "anchors" => anchorsPage,
            "gestures" => gesturePage,
            "pip" => pipPage,
            "appearance" => appearancePage,
            _ => generalPage,
        };
        _features.ItemActivate += (_, _) => // A double-click, or Enter from the keyboard.
        {
            switch (SelectedFeature())
            {
                case AppCommand command: BeginShortcutEdit(command); break;
                case string page: _goToPage(page); break;
            }
        };

        var side = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = SettingsStyle.Side, Padding = new Padding(10, 12, 10, 8) };
        _nav.BackColor = SettingsStyle.Side;
        side.Controls.Add(_nav);
        side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = SettingsStyle.Side });
        var searchRoom = new Panel { Dock = DockStyle.Top, Height = BoxHeight.Standard, Padding = new Padding(2, 0, 2, 0), BackColor = SettingsStyle.Side };
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
        Controls.Add(page);
        Controls.Add(side);

        GiveIcons();
        LoadValues();
        _pipMiddleScroll.Enabled = _experimental.Checked;
        _hoverOptions?.Enabled = _experimental.Checked && Pip.PipEnhancedHover.Supported;
        WatchForChanges(this);
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            box.TextChanged += (_, _) =>
            {
                if (!_loading && _shortcutProblems.Remove(command)) _errors.SetError(box, "");
                QueueApplyRefresh();
            };
        _settle.Tick += (_, _) => ApplyWhenSettled();
        MarkSaved();
        ResumeLayout(true);
    }

    private static IColumnPage? ColumnOf(TabPage? tab) => tab is { Controls.Count: > 0 } ? tab.Controls[0] switch
    {
        ScrollHost host => host.Page,
        IColumnPage page => page,
        _ => null,
    } : null;

    private void FollowColumn()
    {
        Rectangle page = _tabs.DisplayRectangle; // A page just chosen may not have its size yet.
        int left = _tabs.Margin.Left + page.Left, gap = _tabs.Margin.Right + _tabs.Width - page.Right;
        int column = ColumnOf(_tabs.SelectedTab)?.Shift ?? 0;
        int shift = column + left;
        _ = gap;
        if (_pageTitle.Margin.Left == shift && _status.Margin.Left == shift) return;
        _pageTitle.Margin = _pageTitle.Margin with { Left = shift };
        _status.Margin = _status.Margin with { Left = shift };
        // The page moves its column while the window is being laid out, which doesn't lay the title out again.
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _pageTitle.Parent?.PerformLayout(); });
    }

    private void GiveIcons()
    {
        var icons = new Dictionary<Control, char>
        {
            [_language] = '\uE774', [_theme] = '\uE790', [_menuTransparency] = '\uE706', [_startWithWindows] = '\uE7E8', [_runAsAdministrator] = '\uE7EF',
            [_notifications] = '\uEA8F', [_shortcutSheet] = MenuParts.Keyboard, [_updateMode] = '\uE896', [_checkUpdates] = '\uE895', [_pauseInFullScreen] = '\uE7FC', [_allowPip] = '\uE8A7',
            [_experimental] = '\uE945',
            [_modifierDrag] = '\uE7C2', [_modifierKey] = '\uE765', [_snapWhileDragging] = '\uE90C', [_windowMenu] = '\uE712',
            [_windowMenuGesture] = '\uE962',
            [_pipBackend] = '\uE7F4', [_pipRate] = '\uE916', [_pipScaling] = '\uE9A6', [_pipOpacity] = '\uE706',
            [_pipCaptureBorder] = '\uE7A8', [_pipDiagnostics] = '\uE9D9', [_pipAutoMove] = '\uE7C2', [_pipMiddleScroll] = '\uE962',
            [_hoverAutomatic] = '\uE713', [_pipTaskbar] = '\uE7C4', [_pipTitleBar] = '\uE8A0', [_pipKeepHidden] = '\uED1A', [_pipMinimizedOriginals] = '\uE921',[_pipPreview] = '\uE890', [_pictureSave] = '\uE722', [_pictureFolderChange] = '\uE8B7', [_recordingFolderChange] = '\uE714', [_recordSound] = '\uE767', [_recordingRate] = '\uE916',
            [_defaultOpacity] = '\uE706', [_opacityWheel] = '\uE962', [_opaqueOnHover] = '\uE890',
            [_pausePin] = '\uE718', [_highlightPinned] = '\uE790', [_borderColor] = '\uE790', [_borderOpacity] = '\uE706',
            [_borderThickness] = '\uE9E9', [_detectDark] = '\uE708', [_layeredGlass] = '\uE706', [_glassBackground] = '\uE706',
            [_glassText] = '\uE8D2', [_glassFade] = '\uE790', [_appGlassBackground] = '\uE706', [_appGlassText] = '\uE8D2',
            [_darkPage] = '\uE708', [_darkText] = '\uE8D2', [_darkColors] = '\uE790',
            [_appDarkPage] = '\uE708', [_appDarkText] = '\uE8D2', [_appDarkColors] = '\uE790',
            [_appGlassFade] = '\uE790', [_glassTextColor] = '\uE8D3', [_appGlassTextColor] = '\uE8D3', [_glassShadow] = '\uE81E',
            [_appGlassShadow] = '\uE81E', [_ownBackground] = '\uE91B', [_ownGlass] = '\uE706', [_ownDark] = '\uE708', [_itemOwnGlass] = '\uE706', [_itemPage] = '\uE706', [_itemText] = '\uE8D2', [_itemFade] = '\uE790', [_itemTextColor] = '\uE8D3', [_itemShadow] = '\uE81E', [_applyAll] = '\uE8C8', [_backgroundChoose] = '\uE91B', [_backgroundFit] = '\uE9A6', [_backgroundVolume] = '\uE767', [_soundInFront] = '\uE995', [_pauseFullScreen] = '\uE7FC', [_pauseSaver] = '\uE863', [_backgroundSpeed] = '\uEC4A', [_backgroundBrightness] = '\uE706', [_backgroundOrder] = '\uE8B1', [_backgroundSwitch] = '\uE893', [_pictureMinutes] = '\uE823',
            [_showToolbar] = '\uE700', [_toolbarPlacement] = '\uE8E3', [_toolbarTrigger] = '\uE8B0', [_toolbarDelay] = '\uE916',
            [_autoAnchors] = '\uE840', [_anchorSide] = '\uE8A0', [_anchorLook] = '\uE790', [_anchorLimit] = '\uE8FD',
            [_anchorSize] = '\uE740', [_anchorRecency] = '\uE81C', [_anchorAvoid] = '\uE8B9', [_anchorPreview] = '\uE890',
            [_anchorOffTaskbar] = '\uE7C4', [_anchorEveryDesktop] = '\uE7F4',
        };
        foreach (Control control in Every(this))
        {
            if (control is SwitchBox { Compact: false } option && icons.TryGetValue(option, out char glyph)) option.Glyph = glyph;
            else if (control is SettingRowPanel row && icons.FirstOrDefault(pair => pair.Key == row.Setting || row.Setting.Contains(pair.Key)) is { Value: not '\0' } found)
                row.Glyph = found.Value;
        }
        static IEnumerable<Control> Every(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Every(child)));
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int PageIndex
    {
        get => _tabs.SelectedIndex;
        set => _tabs.SelectedIndex = Math.Clamp(value, 0, _tabs.TabCount - 1);
    }

    internal bool HasChanges => _pending;

    // On top as Settings is, put there without activating it: WinForms' own TopMost activates the window.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Quiet { get => _quiet; init { _quiet = value; StayOnTopQuietly(); } }
    private readonly bool _quiet;

    protected override bool ShowWithoutActivation => Quiet;

    internal bool MadeDark { get; } = MenuStyle.Dark;

    private void StayOnTopQuietly()
    {
        if (Quiet && IsHandleCreated)
            Native.User32.SetWindowPos(Handle, Native.User32.HWND_TOPMOST, 0, 0, 0, 0,
                Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
    }

    internal sealed record UnsavedEdits(AppSettings Values, bool Imported, ProgramSetup[] RemovedSetups, string[] AddedApps, string BorderColor);

    internal UnsavedEdits? Unsaved()
    {
        RefreshApply();
        if (HasChanges && InputsComplete() && !_applying) ApplyChanges();
        if (!HasChanges) return null;
        AppSettings values = Draft((_pendingImport ?? _settings).Clone());
        values.SavedSetups = _setups.Items.Cast<ProgramSetup>().ToList();
        return new UnsavedEdits(values, _pendingImport is not null, [.. _removedSetups], [.. _addedApps], _borderColor.Text);
    }

    // Settings is made again when the look changes between light and dark; closing the old window would otherwise drop
    // these.
    internal void TakeUnsaved(UnsavedEdits? edits)
    {
        if (edits is null) return;
        _pendingImport = edits.Imported ? edits.Values : null;
        _removedSetups.UnionWith(edits.RemovedSetups);
        LoadValues(edits.Values);
        _startWithWindows.Checked = edits.Values.StartWithWindows ?? _startup.IsOn;
        foreach (string app in edits.AddedApps)
            if (!_addedApps.Contains(app, StringComparer.OrdinalIgnoreCase)) _addedApps.Add(app);
        _borderColor.Text = edits.BorderColor; // Perhaps still being typed: it can't be in Values until it is whole.
        RebuildApps();
        RefreshApply();
    }

    internal string StatusText => _status.Text;

    internal bool ApplyChanges()
    {
        if (_applying) return false;
        _settle.Stop();
        _applying = true;
        try
        {
            if (!SaveChanges()) return false;
            MarkSaved();
            return true;
        }
        finally { _applying = false; }
    }

    private void MarkSaved()
    {
        _savedState = CurrentState();
        _pending = false;
        _settle.Stop();
    }

    internal void RefreshApply()
    {
        _refreshQueued = false;
        if (IsDisposed) return;
        _pending = _pendingImport is not null || CurrentState() != _savedState;
        if (!_pending || _loading) { if (!_pending) _settle.Stop(); return; }
        _settle.Stop();
        _settle.Start();
    }

    private void ApplyWhenSettled()
    {
        _settle.Stop();
        if (IsDisposed || !_pending || !InputsComplete()) return;
        if (_applying || MouseButtons != MouseButtons.None || ListOpen(this))
        {
            _settle.Start();
            return;
        }
        ApplyChanges();

        static bool ListOpen(Control parent) => parent.Controls.Cast<Control>().Any(child =>
            child is ComboBox { IsHandleCreated: true, DroppedDown: true } || ListOpen(child));
    }

    private bool InputsComplete() => HexColor(_borderColor.Text) is not null;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        RefreshApply();
        if (_pending && InputsComplete() && !_applying) ApplyChanges();
    }

    internal void SetupsChanged()
    {
        bool wasPending = _pending;
        _setups.Items.Clear();
        _setups.Items.AddRange(_settings.SavedSetups.Where(setup => !_removedSetups.Contains(setup))
            .Select(setup => ChangeOf(setup)?.After ?? setup).Cast<object>().ToArray());
        if (!wasPending) MarkSaved();
        else RefreshApply();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessDialogKey(keyData);
        Close();
        return true;
    }

    private static IEnumerable<Control> WithText(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(WithText(child)))
        .Where(control => control is Label or CheckBox or Button or SettingsCard or SettingRowPanel && control.Text.Length > 0);

    private bool Matches(Control control) =>
        (control.Text.Replace("&", "") + (control is IDescribed described ? " " + described.Description : ""))
        .Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void SearchSettings()
    {
        if (string.IsNullOrWhiteSpace(_search.Text)) { _nav.Filter(null); return; }
        TabPage[] found = _tabs.TabPages.Cast<TabPage>().Where(page => Matches(page) || WithText(page).Any(Matches)).ToArray();
        _nav.Filter(found);
        if (found.Length == 0) return;
        if (!found.Contains(_tabs.SelectedTab))
        {
            // Changing page takes the keyboard to the pages; it goes back to the search box, still being typed in.
            bool typing = _search.Focused;
            int caret = _search.SelectionStart, chosen = _search.SelectionLength;
            _tabs.SelectedTab = found[0];
            if (typing && !_search.Focused)
            {
                _search.Focus();
                _search.Select(caret, chosen);
            }
        }
        ShowSearchMatch();
    }

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
                case ComboBox combo when combo != _actionFilter:
                    combo.SelectedIndexChanged += (_, _) => QueueApplyRefresh();
                    combo.TextChanged += (_, _) => QueueApplyRefresh();
                    break;
                case TextBox text when text != _actionSearch && text != _search: text.TextChanged += (_, _) => QueueApplyRefresh(); break;
                case Button button: button.Click += (_, _) => QueueApplyRefresh(); break;
            }
            WatchForChanges(control);
        }
    }

    // What is typed in the colour box is taken as it is: a colour still being typed is a change not yet in effect.
    private string CurrentState()
    {
        AppSettings draft = Draft(new AppSettings());
        draft.SavedSetups = _setups.Items.Cast<ProgramSetup>().ToList();
        return System.Text.Json.JsonSerializer.Serialize(draft) + "\0" + _borderColor.Text.Trim() + "\0" + string.Join("|", _removedKept.Order());
    }

    internal void SetBorderColor(string text) => _borderColor.Text = text;

    internal string BorderColorText => _borderColor.Text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitWorkingArea();
        FollowColumn();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_wheel is null) Application.AddMessageFilter(_wheel = new WheelRouter(this));
        StayOnTopQuietly();
        static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
        int dark = MenuStyle.Dark ? 1 : 0, caption = ColorRef(SettingsStyle.Page), text = ColorRef(SettingsStyle.Text);
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, in dark, sizeof(int));
        // Windows 11 only; Windows 10 refuses these and keeps its own caption colour.
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        Native.Dwmapi.DwmSetWindowAttribute(Handle, Native.Dwmapi.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FollowWindowState();
    }

    private WheelRouter? _wheel;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_wheel is not null) Application.RemoveMessageFilter(_wheel);
        _wheel = null;
        base.OnHandleDestroyed(e);
    }

    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;

    // The wheel only ever scrolls: over a setting it scrolls the page instead of changing it. A list with more than it
    // shows scrolls itself until its end, and an open drop-down keeps the wheel.
    private sealed class WheelRouter(SettingsWindow owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m) => m.Msg is WM_MOUSEWHEEL or WM_MOUSEHWHEEL && owner.RouteWheel(m);
    }

    internal bool RouteWheel(Message m)
    {
        if (IsDisposed || !Visible) return false;
        var at = new Native.NativePoint { X = (short)((long)m.LParam & 0xFFFF), Y = (short)(((long)m.LParam >> 16) & 0xFFFF) };
        if (Control.FromChildHandle(Native.User32.WindowFromPoint(at)) is not { } under || under.FindForm() != this) return false;
        if (Every(this).OfType<ComboBox>().Any(box => box.IsHandleCreated && box.DroppedDown)) return false;
        ScrollPage? page = null;
        for (Control? up = under; up is not null && page is null; up = up.Parent) page = up as ScrollPage;
        if (page is null) return false; // The page list, and the table of actions, which is a page of its own.
        if (m.Msg == WM_MOUSEHWHEEL) return true;
        int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
        for (Control? up = under; up is not null && up != page; up = up.Parent)
            if (up is ListBox list && CanScroll(list, delta))
            {
                Native.User32.SendMessage(list.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                return true;
            }
        Native.User32.SendMessage(page.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
        return true;

        static bool CanScroll(ListBox list, int delta)
        {
            int shown = Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
            return delta > 0 ? list.TopIndex > 0 : list.TopIndex + shown < list.Items.Count;
        }
        static IEnumerable<Control> Every(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Every(child)));
    }

    private bool _belowOthers;

    internal bool FillsScreen => WindowState == FormWindowState.Maximized;

    // Maximized, Settings would hide every view and pinned window, so it stops staying on top until made smaller. Done
    // without activating it, as WinForms' own TopMost would.
    private void FollowWindowState()
    {
        if (!IsHandleCreated || !(TopMost || Quiet)) return;
        bool below = FillsScreen;
        if (below == _belowOthers) return;
        _belowOthers = below;
        Native.User32.SetWindowPos(Handle, below ? Native.User32.HWND_NOTOPMOST : Native.User32.HWND_TOPMOST, 0, 0, 0, 0,
            Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitWorkingArea();
    }

    private void FitWorkingArea()
    {
        if (WindowState != FormWindowState.Normal) return; // Windows sizes a maximized one itself.
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settle.Dispose();
            _errors.Dispose();
            _tips.Dispose();
            foreach (TextBox box in _hotkeys.Values) box.Dispose();
        }
        base.Dispose(disposing);
    }

    // Windows hands one of WinScreen's own shortcuts to WinScreen as the command, so the box never sees it: it goes in
    // the box instead, and nothing runs.
    internal bool TakeShortcut(string combination)
    {
        if (string.IsNullOrEmpty(combination) || !ContainsFocus) return false;
        if (_listeningForSearch && _actionSearch.Focused)
        {
            StopListening();
            _actionSearch.Text = combination;
            return true;
        }
        if (_hotkeys.Values.FirstOrDefault(box => box.Focused) is not { } focused) return false;
        focused.Text = combination;
        _status.Text = "";
        if (_editing is not null) EndShortcutEdit(cancel: false);
        return true;
    }

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

    internal bool SaveChanges()
    {
        AppSettings draft = Draft((_pendingImport ?? _settings).Clone());
        draft.Imported = _pendingImport is not null;
        // Setups are taken from what's in effect, so one saved from a window's menu meanwhile is kept; the changes made
        // here go in the place of the setups they were made to. The draft is a copy, in the same order.
        if (_pendingImport is null)
            foreach (SetupChange change in _changedSetups)
                if (draft.SavedSetups.FindIndex(setup => Written(setup) == change.Before) is var at and >= 0)
                    draft.SavedSetups[at] = System.Text.Json.JsonSerializer.Deserialize<ProgramSetup>(Written(change.After))!;
        // A whole rule matches, not its app: an app can have several, each for some of its windows.
        HashSet<string> removedRules = [.. _removedSetups.Select(Written)];
        draft.SavedSetups.RemoveAll(setup => removedRules.Contains(Written(setup)));
        draft.KeptAreas = draft.KeptAreas.Without(_removedKept);
        try
        {
            IReadOnlyList<HotkeyProblem> problems = _apply(draft);
            if (problems.Count > 0)
            {
                // Every shortcut changed since the last time goes back to the one in effect: a problem can be reported
                // against the shortcut that was there first, when the new one is what clashes with it.
                foreach ((AppCommand command, TextBox box) in _hotkeys)
                {
                    string kept = _settings.Hotkeys.GetValueOrDefault(command, "");
                    if (draft.Hotkeys.GetValueOrDefault(command, "") == kept) continue;
                    draft.Hotkeys[command] = kept;
                    _loading = true; // Putting it back is not a change of its own.
                    try { box.Text = kept; }
                    finally { _loading = false; }
                }
                foreach (HotkeyProblem problem in problems)
                {
                    if (_hotkeys.TryGetValue(problem.Command, out TextBox? box)) _errors.SetError(box, problem.Message);
                    _shortcutProblems[problem.Command] = problem.Message;
                }
                IReadOnlyList<HotkeyProblem> still = _apply(draft);
                _status.Text = string.Join(Environment.NewLine, problems.Concat(still).Select(problem => problem.Message).Distinct());
                if (still.Count > 0)
                {
                    RefreshFeatures();
                    return false;
                }
            }
            else
            {
                _status.Text = "";
            }
            _pendingImport = null;
            _removedSetups.Clear();
            _changedSetups.Clear();
            _removedKept.Clear();
            ListKept();
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

    private AppSettings Draft(AppSettings draft)
    {
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            draft.Hotkeys[command] = box.Text;
        foreach (var (box, _, write) in Switches) write(draft, box.Checked);
        foreach (var (box, _, write) in Choices) write(draft, Math.Max(0, box.SelectedIndex));
        foreach (var (box, _, write) in Numbers) write(draft, Number(box));
        foreach (var (list, _, write) in ProgramLists) write(draft, [.. list.Items.Cast<string>()]);

        draft.StartWithWindows = _startWithWindows.Checked;
        draft.Toolbar = draft.Toolbar with { Buttons = [.. _toolbarButtons.CheckedItems.Cast<ToolbarChoice>().Select(choice => choice.Command)] };
        draft.PipHover = new PipHoverPreferences
        {
            ChooseForMe = _hoverAutomatic.Checked, Browsers = _hover.Browsers, Firefox = _hover.Firefox, Electron = _hover.Electron,
            FileExplorer = _hover.FileExplorer, Programs = [.. _hoverPrograms.Items.Cast<string>()],
        }.Validated();
        if (!_hoverAutomatic.Checked) draft.PipHover = draft.PipHover with
        {
            Browsers = _hoverBrowsers.Checked, Firefox = _hoverFirefox.Checked, Electron = _hoverElectron.Checked,
            FileExplorer = _hoverExplorer.Checked,
        };
        draft.Effects = draft.Effects with { Apps = [.. _glassApps] };
        draft.Backgrounds = _backgroundEdit.Validated();
        // A colour still being typed leaves the one in effect alone.
        if (HexColor(_borderColor.Text) is not null) draft.PinBorder = draft.PinBorder with { ColorHex = _borderColor.Text.Trim() };
        draft.PipViews = draft.PipViews with
        {
            SavePictures = _pictureSave.SelectedIndex != CopyOnly, CopyPictures = _pictureSave.SelectedIndex != SaveOnly,
            PictureFolder = _pictureFolder, RecordingFolder = _recordingFolder,
        };
        draft.Toolbar = draft.Toolbar.Validated();
        draft.Anchors = draft.Anchors.Validated();
        draft.Effects = draft.Effects.Validated();
        draft.PinBorder = draft.PinBorder.Validated();
        draft.PipViews = draft.PipViews.Validated();
        return draft;
    }

    private static int Number(NumericUpDown box)
    {
        // Reading Value would tidy up a number that's still being typed, so the text is read instead.
        if (!decimal.TryParse(box.Text, out decimal typed)) return (int)box.Value;
        return (int)Math.Clamp(typed, box.Minimum, box.Maximum);
    }

    // Filling Settings in and reading it back both go through these lists, so a new switch, choice, number or program
    // list is one line in one of them. A group's settings are written into it one at a time; Draft checks each group
    // once at the end.
    private (CheckBox Box, Func<AppSettings, bool> Read, Action<AppSettings, bool> Write)[] Switches =>
    [
        (_showToolbar, settings => settings.ShowToolbar, (settings, on) => settings.ShowToolbar = on),
        (_shortcutSheet, settings => settings.ShortcutSheet, (settings, on) => settings.ShortcutSheet = on),
        (_menuTransparency, settings => settings.MenuTransparency, (settings, on) => settings.MenuTransparency = on),
        (_experimental, settings => settings.ExperimentalFeatures, (settings, on) => settings.ExperimentalFeatures = on),
        (_runAsAdministrator, settings => settings.RunAsAdministrator, (settings, on) => settings.RunAsAdministrator = on),
        (_pauseInFullScreen, settings => settings.PauseInFullScreen, (settings, on) => settings.PauseInFullScreen = on),
        (_allowPip, settings => settings.AllowPipInFullScreen, (settings, on) => settings.AllowPipInFullScreen = on),
        (_modifierDrag, settings => settings.ModifierDrag, (settings, on) => settings.ModifierDrag = on),
        (_snapWhileDragging, settings => settings.SnapWhileDragging, (settings, on) => settings.SnapWhileDragging = on),
        (_windowMenu, settings => settings.WindowMenu, (settings, on) => settings.WindowMenu = on),
        (_pausePin, settings => settings.PausePinWhenMaximized, (settings, on) => settings.PausePinWhenMaximized = on),
        (_opaqueOnHover, settings => settings.OpaqueOnHover, (settings, on) => settings.OpaqueOnHover = on),
        (_highlightPinned, settings => settings.HighlightPinned, (settings, on) => settings.HighlightPinned = on),
        (_pipAutoMove, settings => settings.PipAutoMove, (settings, on) => settings.PipAutoMove = on),
        (_pipMiddleScroll, settings => settings.PipMiddleClickScroll, (settings, on) => settings.PipMiddleClickScroll = on),
        (_opacityWheel, settings => settings.OpacityWheel, (settings, on) => settings.OpacityWheel = on),
        (_detectDark, settings => settings.DetectAlreadyDark, (settings, on) => settings.DetectAlreadyDark = on),
        (_pipCaptureBorder, settings => settings.HideCaptureBorder, (settings, on) => settings.HideCaptureBorder = on),
        (_autoAnchors, settings => settings.Anchors.Automatic, (settings, on) => settings.Anchors = settings.Anchors with { Automatic = on }),
        (_anchorRecency, settings => settings.Anchors.RecencyEmphasis, (settings, on) => settings.Anchors = settings.Anchors with { RecencyEmphasis = on }),
        (_anchorAvoid, settings => settings.Anchors.AvoidContent, (settings, on) => settings.Anchors = settings.Anchors with { AvoidContent = on }),
        (_anchorPreview, settings => settings.Anchors.HoverPreview, (settings, on) => settings.Anchors = settings.Anchors with { HoverPreview = on }),
        (_anchorOffTaskbar, settings => settings.Anchors.OffTaskbar, (settings, on) => settings.Anchors = settings.Anchors with { OffTaskbar = on }),
        (_anchorEveryDesktop, settings => settings.Anchors.EveryDesktop, (settings, on) => settings.Anchors = settings.Anchors with { EveryDesktop = on }),
        (_layeredGlass, settings => settings.Effects.LayeredGlass, (settings, on) => settings.Effects = settings.Effects with { LayeredGlass = on }),
        (_glassShadow, settings => settings.Effects.TextShadow, (settings, on) => settings.Effects = settings.Effects with { TextShadow = on }),
        (_pipDiagnostics, settings => settings.Pip.ShowDiagnostics, (settings, on) => settings.Pip.ShowDiagnostics = on),
        (_pipTaskbar, settings => settings.PipViews.MinimizeToTaskbar, (settings, on) => settings.PipViews = settings.PipViews with { MinimizeToTaskbar = on }),
        (_pipTitleBar, settings => settings.PipViews.AlwaysShowTitleBar, (settings, on) => settings.PipViews = settings.PipViews with { AlwaysShowTitleBar = on }),
        (_pipKeepHidden, settings => settings.PipViews.KeepHiddenOnNewViews, (settings, on) => settings.PipViews = settings.PipViews with { KeepHiddenOnNewViews = on }),
        (_pipMinimizedOriginals, settings => settings.PipViews.KeepMinimizedOriginalsOnTaskbar,
            (settings, on) => settings.PipViews = settings.PipViews with { KeepMinimizedOriginalsOnTaskbar = on }),
        (_recordSound, settings => settings.PipViews.RecordSound, (settings, on) => settings.PipViews = settings.PipViews with { RecordSound = on }),
    ];

    // Each list's position, from 0.
    private (ComboBox Box, Func<AppSettings, int> Read, Action<AppSettings, int> Write)[] Choices =>
    [
        (_language, settings => Array.IndexOf(Languages, settings.Language), (settings, at) => settings.Language = Languages[at]),
        (_theme, settings => Array.IndexOf(Themes, settings.Theme), (settings, at) => settings.Theme = Themes[at]),
        (_updateMode, settings => (int)settings.Updates, (settings, at) => settings.Updates = (UpdateMode)at),
        (_notifications, settings => (int)settings.Notifications, (settings, at) => settings.Notifications = (NotificationMode)at),
        (_modifierKey, settings => (int)settings.ModifierKey, (settings, at) => settings.ModifierKey = (ModifierKey)at),
        (_windowMenuGesture, settings => (int)settings.WindowMenuGesture, (settings, at) => settings.WindowMenuGesture = (WindowMenuGesture)at),
        (_toolbarPlacement, settings => (int)settings.Toolbar.Placement,
            (settings, at) => settings.Toolbar = settings.Toolbar with { Placement = (ToolbarPlacement)at }),
        (_toolbarTrigger, settings => (int)settings.Toolbar.Trigger, (settings, at) => settings.Toolbar = settings.Toolbar with { Trigger = (ToolbarTrigger)at }),
        (_anchorSide, settings => (int)settings.Anchors.Side, (settings, at) => settings.Anchors = settings.Anchors with { Side = (AnchorSide)at }),
        (_anchorLook, settings => (int)settings.Anchors.Style, (settings, at) => settings.Anchors = settings.Anchors with { Style = (AnchorLook)at }),
        (_glassTextColor, settings => (int)settings.Effects.TextColor,
            (settings, at) => settings.Effects = settings.Effects with { TextColor = (Glass.GlassText)at }),
        (_pipBackend, settings => (int)settings.Pip.Backend, (settings, at) => settings.Pip.Backend = (PipBackendPreference)at),
        (_pipRate, settings => _pipRates.IndexOf(settings.Pip.FrameRate), (settings, at) => settings.Pip.FrameRate = _pipRates[at]),
        (_pipScaling, settings => (int)settings.Pip.Scaling, (settings, at) => settings.Pip.Scaling = (PipScaling)at),
        (_pipPreview, settings => (int)settings.PipViews.HoverPreview,
            (settings, at) => settings.PipViews = settings.PipViews with { HoverPreview = (PipHoverPreview)at }),
        (_recordingRate, settings => Array.IndexOf(Pip.RecordingGeometry.ChoosableRates, settings.PipViews.RecordingFrameRate),
            (settings, at) => settings.PipViews = settings.PipViews with { RecordingFrameRate = Pip.RecordingGeometry.ChoosableRates[at] }),
    ];

    private (NumericUpDown Box, Func<AppSettings, int> Read, Action<AppSettings, int> Write)[] Numbers =>
    [
        (_defaultOpacity, settings => Math.Clamp(settings.DefaultOpacityPercent, AppSettings.LowestOpacityPercent, AppSettings.HighestOpacityPercent),
            (settings, n) => settings.DefaultOpacityPercent = n),
        (_toolbarDelay, settings => settings.Toolbar.HoverDelayMilliseconds,
            (settings, n) => settings.Toolbar = settings.Toolbar with { HoverDelayMilliseconds = n }),
        (_anchorLimit, settings => settings.Anchors.MaxAutomatic, (settings, n) => settings.Anchors = settings.Anchors with { MaxAutomatic = n }),
        (_anchorSize, settings => settings.Anchors.Size, (settings, n) => settings.Anchors = settings.Anchors with { Size = n }),
        (_glassBackground, settings => settings.Effects.BackgroundPercent, (settings, n) => settings.Effects = settings.Effects with { BackgroundPercent = n }),
        (_glassText, settings => settings.Effects.TextPercent, (settings, n) => settings.Effects = settings.Effects with { TextPercent = n }),
        (_glassFade, settings => settings.Effects.FadeRangePercent, (settings, n) => settings.Effects = settings.Effects with { FadeRangePercent = n }),
        (_darkPage, settings => settings.Effects.DarkPagePercent, (settings, n) => settings.Effects = settings.Effects with { DarkPagePercent = n }),
        (_darkText, settings => settings.Effects.DarkTextPercent, (settings, n) => settings.Effects = settings.Effects with { DarkTextPercent = n }),
        (_darkColors, settings => settings.Effects.DarkColorsPercent, (settings, n) => settings.Effects = settings.Effects with { DarkColorsPercent = n }),
        (_borderOpacity, settings => settings.PinBorder.OpacityPercent, (settings, n) => settings.PinBorder = settings.PinBorder with { OpacityPercent = n }),
        (_borderThickness, settings => settings.PinBorder.Thickness, (settings, n) => settings.PinBorder = settings.PinBorder with { Thickness = n }),
        (_pipOpacity, settings => settings.Pip.OpacityPercent, (settings, n) => settings.Pip.OpacityPercent = n),
    ];

    private (ListBox List, Func<AppSettings, List<string>> Read, Action<AppSettings, List<string>> Write)[] ProgramLists =>
    [
        (_excluded, settings => settings.ExcludedPrograms, (settings, programs) => settings.ExcludedPrograms = programs),
        (_included, settings => settings.IncludedPrograms, (settings, programs) => settings.IncludedPrograms = programs),
        (_anchorOnly, settings => settings.Anchors.OnlyPrograms, (settings, programs) => settings.Anchors = settings.Anchors with { OnlyPrograms = programs }),
        (_anchorExcluded, settings => settings.Anchors.ExcludedPrograms,
            (settings, programs) => settings.Anchors = settings.Anchors with { ExcludedPrograms = programs }),
        (_windowMenuExclusions, settings => settings.WindowMenuExclusions, (settings, programs) => settings.WindowMenuExclusions = programs),
        (_modifierExclusions, settings => settings.ModifierExclusions, (settings, programs) => settings.ModifierExclusions = programs),
        (_originalPrograms, settings => settings.PipOriginalPrograms, (settings, programs) => settings.PipOriginalPrograms = programs),
    ];

    internal void AddExcluded(string program) { AddProgram(_excluded, program); QueueApplyRefresh(); }

    internal void AddIncluded(string program) { AddProgram(_included, program); QueueApplyRefresh(); }

    internal void SetDefaultOpacity(int percent) => _defaultOpacity.Value = percent;

    internal void AddApp(string program)
    {
        _newBackgroundApp.Text = program;
        AddBackgroundApp();
    }

    internal void ChangeSetup(ProgramSetup setup)
    {
        int index = _setups.Items.IndexOf(setup);
        if (index < 0) return;
        var copy = System.Text.Json.JsonSerializer.Deserialize<ProgramSetup>(System.Text.Json.JsonSerializer.Serialize(setup))!;
        using var dialog = new RuleDialog(copy);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ReplaceSetup(setup, dialog.Result());
    }

    // What is saved is built from the setups in effect (so one saved from a window's menu meanwhile is kept), so the
    // change is kept against the setup it was made to.
    internal void ReplaceSetup(ProgramSetup shown, ProgramSetup changed)
    {
        int index = _setups.Items.IndexOf(shown);
        if (index < 0) return;
        if (_changedSetups.Find(change => ReferenceEquals(change.After, shown)) is { } earlier) earlier.After = changed;
        else _changedSetups.Add(new SetupChange(Written(shown), changed));
        _setups.Items[index] = changed;
        _setups.SelectedIndex = index;
        QueueApplyRefresh();
    }

    // What is in effect is a fresh copy after every save, so the setup is found again by what it says, not by which
    // object it is.
    private sealed class SetupChange(string before, ProgramSetup after)
    {
        public string Before { get; } = before;
        public ProgramSetup After { get; set; } = after;
    }

    private readonly List<SetupChange> _changedSetups = [];

    private SetupChange? ChangeOf(ProgramSetup inEffect) => _changedSetups.Find(change => change.Before == Written(inEffect));

    private static string Written(ProgramSetup setup) => System.Text.Json.JsonSerializer.Serialize(setup);

    internal void RemoveSetup(ProgramSetup setup)
    {
        _setups.Items.Remove(setup);
        // A setup changed here and then removed: the one in effect goes, not the change.
        if (_changedSetups.Find(change => ReferenceEquals(change.After, setup)) is { } change)
        {
            _changedSetups.Remove(change);
            if (_settings.SavedSetups.Find(inEffect => Written(inEffect) == change.Before) is { } inEffect)
                setup = inEffect;
        }
        _removedSetups.Add(setup);
        QueueApplyRefresh();
    }

    // Already saved; shown here too so the window's next change doesn't save the list without it.
    internal void AddOriginalProgram(string program)
    {
        string name = ProgramName(program);
        string[] hovered = _hoverPrograms.Items.Cast<string>().Where(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (string item in hovered) _hoverPrograms.Items.Remove(item);
        bool listed = _originalPrograms.Items.Cast<string>().Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!listed) AddProgram(_originalPrograms, name);
        if (hovered.Length > 0 || !listed) RebuildApps();
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

    private PipHoverPreferences _hover = new();
    private bool _hoverShowsOwn = true, _loadingHover;

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

    // A program has Enhanced hover or Show original, never both: with its original shown, nothing is sent to it.
    private void RebuildApps(int focusRemoveAt = -1)
    {
        // The page is rebuilt unseen and left where it was scrolled to: taking away the button that had the keyboard
        // would otherwise send the keyboard, and the page with it, back to the top.
        ScrollPage? scroller = null;
        for (Control? parent = _appRows.Parent; parent is not null && scroller is null; parent = parent.Parent) scroller = parent as ScrollPage;
        int scrolled = scroller is null ? 0 : -scroller.AutoScrollPosition.Y;
        bool hid = scroller is { IsHandleCreated: true, Visible: true };
        if (hid) Native.User32.SendMessage(scroller!.Handle, WM_SETREDRAW, 0, 0);
        bool hadKeyboard = _appRows.ContainsFocus;
        try { BuildAppRows(); }
        finally
        {
            if (scroller is not null) scroller.AutoScrollPosition = new Point(0, scrolled);
            // Turning drawing off hides the page from Windows, so it is back on before anything in it takes the
            // keyboard.
            if (hid) Native.User32.SendMessage(scroller!.Handle, WM_SETREDRAW, 1, 0);
            if (hadKeyboard)
            {
                Control[] removes = _appRows.Controls.Cast<Control>().SelectMany(row => row.Controls.OfType<PlainButton>()).ToArray();
                if (focusRemoveAt >= 0 && removes.Length > 0) removes[Math.Min(focusRemoveAt, removes.Length - 1)].Focus();
                else _newApp.Focus();
                if (scroller is not null) scroller.AutoScrollPosition = new Point(0, scrolled);
            }
            if (hid) scroller!.Invalidate(true);
        }
    }

    private const uint WM_SETREDRAW = 0x000B;

    private void BuildAppRows()
    {
        // Sizes here are at 100% scaling; rows made after the window has scaled itself are scaled to match below.
        double scale = _appRows.DeviceDpi / 96.0;
        bool scaled = IsHandleCreated;
        int Measured(string text) => (int)Math.Ceiling(Native.DirectText.MeasureText(text, SettingsStyle.CaptionFont, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width / scale);
        string hoverText = T("menu.view.hover"), originalText = T("settings.pip.apps.original");
        const int switchWidth = 40, between = 32, removeAt = SwitchBox.RowWidth - 36;
        int originalColumn = Math.Max(Measured(originalText), switchWidth), hoverColumn = Math.Max(Measured(hoverText), switchWidth);
        int originalMiddle = removeAt - 16 - originalColumn / 2;
        int hoverMiddle = originalMiddle - originalColumn / 2 - between - hoverColumn / 2;
        int namesEnd = hoverMiddle - hoverColumn / 2 - 12;
        _tips.RemoveAll();
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
            row.Controls.Add(new SoftLabel { Text = name, AutoSize = false, Bounds = new Rectangle(1, 0, Math.Max(60, namesEnd), row.Height),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
                ForeColor = heading ? SettingsStyle.Secondary : SettingsStyle.Text, Font = heading ? SettingsStyle.CaptionFont : SettingsStyle.BodyFont });
            return row;
        }
        void Add(Panel row)
        {
            if (scaled) row.Scale(new SizeF((float)scale, (float)scale));
            _appRows.Controls.Add(row);
        }
        void Place(Panel row, Control control, int at)
        {
            control.Location = new Point(at, (row.Height - control.Height) / 2);
            control.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(control);
            control.BringToFront();
        }
        Panel head = Row(T("settings.pip.apps.app"), true);
        foreach (var (text, middle) in new[] { (hoverText, hoverMiddle), (originalText, originalMiddle) })
            Place(head, new SoftLabel { Text = text, AutoSize = true, ForeColor = SettingsStyle.Secondary, Font = SettingsStyle.CaptionFont, UseMnemonic = false },
                middle - Measured(text) / 2);
        Add(head);
        foreach (CheckBox kind in new[] { _hoverBrowsers, _hoverFirefox, _hoverElectron, _hoverExplorer })
        {
            Panel row = Row(kind.Text, false);
            kind.Size = kind.GetPreferredSize(Size.Empty);
            if (scaled) kind.Size = new Size((int)Math.Round(kind.Width / scale), (int)Math.Round(kind.Height / scale));
            Place(row, kind, hoverMiddle - kind.Width / 2);
            Add(row);
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
            string removeText = string.Format(T("settings.pip.apps.remove"), program);
            var remove = new PlainButton { Glyph = MenuParts.Delete, Removes = true, AutoSize = false, Padding = Padding.Empty,
                AccessibleName = removeText };
            remove.Size = remove.GetPreferredSize(Size.Empty);
            _tips.SetToolTip(remove, T("common.remove").Replace("&", ""));
            int place = Array.IndexOf(programs, program);
            remove.Click += (_, _) =>
            {
                Unlist(_hoverPrograms, program);
                Unlist(_originalPrograms, program);
                _addedApps.RemoveAll(name => name.Equals(program, StringComparison.OrdinalIgnoreCase));
                BeginInvoke(() => RebuildApps(place)); // Not from inside the click of a button this removes.
                QueueApplyRefresh();
            };
            if (scaled)
                foreach (Control part in new Control[] { hover, original, remove })
                    part.Size = new Size((int)Math.Round(part.Width / scale), (int)Math.Round(part.Height / scale));
            Place(row, hover, hoverMiddle - hover.Width / 2);
            Place(row, original, originalMiddle - original.Width / 2);
            Place(row, remove, removeAt);
            Add(row);
        }
        _appRows.ResumeLayout(true);
        for (Control? parent = _appRows.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollPage page) { page.Restretch(); break; }
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
                    Diagnostics.Log.Swallowed(e);
                }
            }
        }

        return names;
    }

    internal void SetUpdateMode(UpdateMode mode) => _updateMode.SelectedIndex = (int)mode;
    internal void SetNotifications(NotificationMode mode) => _notifications.SelectedIndex = (int)mode;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? UpdateReady { get; init; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action? RestartToUpdate { get; init; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action? CheckForUpdates { get; init; }

    private readonly PlainButton _restartToUpdate = new() { AutoSize = true, Visible = false, Margin = Padding.Empty };
    private readonly PlainButton _checkUpdates = new() { Text = T("updates.checkNow"), AutoSize = true, Margin = new Padding(0, 0, 8, 0) };

    internal void SetOption(string option, bool on)
    {
        CheckBox box = option switch
        {
            nameof(AppSettings.ShowToolbar) => _showToolbar,
            nameof(AppSettings.ShortcutSheet) => _shortcutSheet,
            nameof(AppSettings.MenuTransparency) => _menuTransparency,
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

    internal void LoadValues(AppSettings? values = null)
    {
        bool inEffect = values is null && _pendingImport is null || ReferenceEquals(values, _settings);
        _loading = true;
        try { Fill(values ?? _pendingImport ?? _settings); }
        finally { _loading = false; }
        if (inEffect) MarkSaved();
        else RefreshApply();
    }

    private void Fill(AppSettings values)
    {
        // The groups are shown as they would be used: checked once here.
        AppSettings shown = values.Clone();
        shown.Toolbar = shown.Toolbar.Validated();
        shown.Anchors = shown.Anchors.Validated();
        shown.Effects = shown.Effects.Validated();
        shown.PinBorder = shown.PinBorder.Validated();
        shown.Pip = shown.Pip.Validated();
        shown.PipViews = shown.PipViews.Validated();

        _setups.Items.Clear();
        _newExcluded.Text = "";
        _newIncluded.Text = "";
        _errors.Clear();
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            box.Text = shown.Hotkeys.GetValueOrDefault(command, "");
        _setups.Items.AddRange(values.SavedSetups.Cast<object>().ToArray()); // The rules themselves: edits find them by reference.
        _startWithWindows.Checked = _startup.IsOn;

        // The frame rates offered are the usual ones, the monitor's and whatever was chosen before.
        if (!_pipRates.Contains(shown.Pip.FrameRate)) _pipRates.Add(shown.Pip.FrameRate);
        int refresh = Pip.PipDisplay.CachedRefresh(Handle);
        if (!_pipRates.Contains(refresh)) _pipRates.Add(refresh);
        _pipRates.Sort();
        _pipRate.Items.Clear();
        foreach (int rate in _pipRates) _pipRate.Items.Add(rate == 0 ? T("pip.frameRate.matchMonitor") : rate + " FPS");

        foreach (var (box, read, _) in Switches) box.Checked = read(shown);
        foreach (var (box, read, _) in Choices) box.SelectedIndex = Math.Max(0, read(shown));
        foreach (var (box, read, _) in Numbers) box.Value = read(shown);
        foreach (var (list, read, _) in ProgramLists)
        {
            list.Items.Clear();
            list.Items.AddRange(read(shown).Cast<object>().ToArray());
        }

        _runAsAdministrator.Enabled = Native.ProcessInfo.CanElevate || shown.RunAsAdministrator; // Still able to turn off.
        for (int i = 0; i < _toolbarButtons.Items.Count; i++)
            _toolbarButtons.SetItemChecked(i, shown.Toolbar.Buttons.Contains(((ToolbarChoice)_toolbarButtons.Items[i]).Command));
        _loadingHover = true;
        _hover = values.PipHover;
        _hoverAutomatic.Checked = values.PipHover.ChooseForMe;
        ShowHoverChoices();
        _loadingHover = false;
        _hoverPrograms.Items.Clear();
        _hoverPrograms.Items.AddRange(values.PipHover.Programs.Cast<object>().ToArray());
        _addedApps.Clear();
        RebuildApps();
        _pictureSave.SelectedIndex = !shown.PipViews.SavePictures ? CopyOnly : shown.PipViews.CopyPictures ? 0 : SaveOnly;
        _pictureFolder = shown.PipViews.PictureFolder;
        ShowPictureFolder();
        _recordingFolder = shown.PipViews.RecordingFolder;
        ShowRecordingFolder();
        FillGlassApps(shown.Effects);
        FillBackgrounds(values.Backgrounds);
        _borderColor.Text = shown.PinBorder.ColorHex;
        RefreshFeatures();
    }

    private static TabPage Tab(string title, Control content)
    {
        var tab = new TabPage(title) { Padding = new Padding(0, 4, 0, 0), UseVisualStyleBackColor = false, BackColor = SettingsStyle.Page };
        tab.Controls.Add(content is ScrollPage page ? new ScrollHost(page) : content);
        return tab;
    }

    private static Label Note(string text) => new NoteLabel()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(SwitchBox.RowWidth - 8, 0), Margin = new Padding(7, 6, 3, 2),
        ForeColor = SettingsStyle.Secondary, Font = SettingsStyle.CaptionFont, UseMnemonic = false,
    };

    private static SettingsCard Section(string title, params Control[] items) => Card(title, false, items);

    private static SettingsCard Card(string title, bool experimental, params Control[] items)
    {
        var kept = new List<Control>();
        foreach (Control item in items)
        {
            if (item is NoteLabel note && kept.Count > 0)
            {
                string Joined(string before) => before.Length == 0 ? note.Text : before + Environment.NewLine + note.Text;
                if (kept[^1] is SwitchBox { Compact: false } option) { option.Description = Joined(option.Description); continue; }
                if (kept[^1] is SettingRowPanel row) { row.Description = Joined(row.Description); continue; }
            }
            if (item is CheckBox box) box.Margin = new Padding(3, 0, 3, 0);
            kept.Add(item);
        }
        return new SettingsCard(title, experimental, ContentWidth, kept);
    }

    private static Control SettingRow(string text, Control control)
    {
        if (control is NumericUpDown number) control = new NumberSlider(number) { AccessibleName = text.TrimEnd(':', '：', ' ') };
        else if (control is TextBox box) control = new InputFrame(box);
        return new SettingRowPanel(text.TrimEnd(':', '：', ' '), control);
    }

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

        // A standard account can't have it: Windows would ask for another person's password and run WinScreen as them.
        bool adminWorks = Native.ProcessInfo.CanElevate;
        // Windows 10 has no Transparency effects for menus to follow.
        bool blurWorks = Environment.OSVersion.Version.Build >= 22000;
        _menuTransparency.Enabled = blurWorks;
        var startup = new List<Control> { language, theme, _menuTransparency,
            Note(blurWorks ? T("settings.menus.transparency.help") : T("settings.menus.transparency.windows11")), _startWithWindows, _runAsAdministrator,
            Note(adminWorks ? T("settings.admin.help") : T("settings.admin.unavailable")),
            SettingRow(T("settings.notifications"), _notifications), Note(T("settings.notifications.help")),
            SettingRow(T("updates.mode.label"), _updateMode), Note(T("updates.mode.help")) };
        _updateMode.Enabled = VelopackUpdateSession.IsConfigured;
        _restartToUpdate.Text = T("updates.restart");
        _restartToUpdate.Size = _restartToUpdate.GetPreferredSize(Size.Empty); // Hidden until needed, so laid out by nobody before.
        _restartToUpdate.Click += (_, _) => RestartToUpdate?.Invoke();
        _checkUpdates.Enabled = VelopackUpdateSession.IsConfigured;
        _checkUpdates.Click += (_, _) => CheckForUpdates?.Invoke();
        var updateButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, BackColor = SettingsStyle.Card };
        updateButtons.Controls.Add(_checkUpdates);
        updateButtons.Controls.Add(_restartToUpdate);
        // The version is at the end of the page, with What's new.
        startup.Add(SettingRow(T("updates.check"), updateButtons));
        // Shown each time Settings comes forward: an update can finish downloading while it is open.
        Activated += (_, _) => _restartToUpdate.Visible = UpdateReady?.Invoke() == true;
        if (!VelopackUpdateSession.IsConfigured)
            startup.Add(Note(T("updates.repositoryRequired")));
        else if (!VelopackUpdateSession.IsInstalled)
            startup.Add(Note(T("updates.portable")));

        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("settings.startupAndUpdates"), startup.ToArray()));
        page.Controls.Add(Section(T("settings.windows"), _pauseInFullScreen, _allowPip));
        page.Controls.Add(Section(T("settings.experimental"), _experimental));
        page.Controls.Add(BuildTransfer());
        page.Controls.Add(new SoftLabel
        {
            Text = string.Format(T("app.version"), AppInfo.Version), AutoSize = true,
            ForeColor = SettingsStyle.Secondary, Font = SettingsStyle.CaptionFont, Margin = new Padding(3, 2, 3, 0),
        });
        var whatsNew = new PlainButton { Text = T("whatsNew.open"), AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        whatsNew.Click += (_, _) => WhatsNew.Show();
        page.Controls.Add(whatsNew);
        return page;
    }

    private FlowLayoutPanel BuildModifierDrag()
    {
        _modifierKey.Items.AddRange(["Alt", "Win"]);
        Control key = SettingRow(T("gestures.moveResize.modifier").TrimEnd(':', '：'), _modifierKey);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("gestures.moveResize.title"), _modifierDrag, Note(T("gestures.moveResize.help")), key, _snapWhileDragging,
            ProgramRules(T("gestures.exclusions"), _modifierExclusions, _newModifierExclusion), Note(T("programs.alwaysLeftAlone"))));
        _windowMenuGesture.Items.AddRange([T("gestures.menu.ctrlRight"), T("gestures.menu.ctrlShiftRight"), T("gestures.menu.ctrlMiddle")]);
        page.Controls.Add(Section(T("window.menu"), _windowMenu, Note(T("gestures.menu.help")), Note(T("gestures.hookHelp")),
            SettingRow(T("gestures.menu.gesture"), _windowMenuGesture),
            ProgramRules(T("gestures.exclusions"), _windowMenuExclusions, _newMenuExclusion), Note(T("programs.alwaysLeftAlone"))));
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
            Row(T("pip.scaling"), _pipScaling), Note(T("pip.scaling.help")), Row(T("pip.opacityPercent"), _pipOpacity), _pipCaptureBorder, _pipDiagnostics,
            Note(T("pip.diagnostics.retryHelp"))));
        page.Controls.Add(Section(T("settings.pip.moving"), _pipAutoMove, Note(T("settings.pip.autoMoveHelp"))));
        page.Controls.Add(Card(T("pip.interactive"), true, _pipMiddleScroll, Note(T("settings.pip.middleScrollHelp"))));
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
        _newApp.TextChanged += (_, _) => add.Enabled = _newApp.Text.Trim().Length > 0;
        void AddApp()
        {
            string name = ProgramName(_newApp.Text);
            _newApp.Text = "";
            if (name.Length == 0) return;
            if (!_addedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _addedApps.Add(name);
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
        _newApp.Margin = new Padding(0, 0, 8, 0);
        add.Margin = Padding.Empty;
        _newApp.Anchor = add.Anchor = AnchorStyles.Left;
        var adding = SettingGroups.JoinAbove(new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 3, 6), BackColor = SettingsStyle.Card });
        adding.Controls.Add(_newApp);
        adding.Controls.Add(add);
        var parts = new List<Control> { Note(T("settings.pip.apps.help")), _hoverAutomatic, _appRows, adding, Note(T("settings.pip.hover.programsHelp")) };
        if (!Pip.PipEnhancedHover.Supported) parts.Add(Note(T("settings.pip.hover.unsupported")));
        page.Controls.Add(Card(T("settings.pip.apps"), true, parts.ToArray()));
        _pictureSave.Items.AddRange([T("settings.pip.pictures.copySave"), T("settings.pip.pictures.saveOnly"), T("settings.pip.pictures.copyOnly")]);
        _pictureSave.SelectedIndexChanged += (_, _) => ShowPictureFolder();
        _pictureFolderChange.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = PictureFolderInUse, UseDescriptionForTitle = true,
                Description = T("settings.pip.pictures.folder") };
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPath.Length == 0) return;
            _pictureFolder = string.Equals(dialog.SelectedPath, Pip.PictureShelf.DefaultFolder(), StringComparison.OrdinalIgnoreCase)
                ? null : dialog.SelectedPath;
            ShowPictureFolder();
        };
        _pictureFolderOpen.Click += (_, _) => Pip.PictureShelf.OpenFolder(PictureFolderInUse);
        _pictureFolderChange.Margin = new Padding(0, 0, 8, 0);
        _pictureFolderOpen.Margin = Padding.Empty;
        var folderButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Margin = Padding.Empty, BackColor = SettingsStyle.Card };
        folderButtons.Controls.Add(_pictureFolderChange);
        folderButtons.Controls.Add(_pictureFolderOpen);
        _pictureFolderRow = (SettingRowPanel)SettingRow(T("settings.pip.pictures.folder"), folderButtons);
        _recordingFolderChange.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = RecordingFolderInUse, UseDescriptionForTitle = true,
                Description = T("settings.pip.recordings.folder") };
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPath.Length == 0) return;
            _recordingFolder = string.Equals(dialog.SelectedPath, Pip.PictureShelf.DefaultRecordingFolder(), StringComparison.OrdinalIgnoreCase)
                ? null : dialog.SelectedPath;
            ShowRecordingFolder();
        };
        _recordingFolderOpen.Click += (_, _) => Pip.PictureShelf.OpenFolder(RecordingFolderInUse);
        _recordingFolderChange.Margin = new Padding(0, 0, 8, 0);
        _recordingFolderOpen.Margin = Padding.Empty;
        var recordingButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Margin = Padding.Empty, BackColor = SettingsStyle.Card };
        recordingButtons.Controls.Add(_recordingFolderChange);
        recordingButtons.Controls.Add(_recordingFolderOpen);
        _recordingFolderRow = (SettingRowPanel)SettingRow(T("settings.pip.recordings.folder"), recordingButtons);
        _recordingRate.Items.AddRange([.. Pip.RecordingGeometry.ChoosableRates.Select(rate => (object)(rate == 0 ? T("settings.pip.recordings.rate.view") : rate + " FPS"))]);
        page.Controls.Add(Section(T("settings.pip.pictures"), Row(T("settings.pip.pictures.after"), _pictureSave), _pictureFolderRow,
            _recordingFolderRow, Row(T("settings.pip.recordings.rate"), _recordingRate), _recordSound, Note(T("settings.pip.pictures.help"))));
        ShowPictureFolder();
        ShowRecordingFolder();
        page.Controls.Add(Section(T("pip.hover.title"), _pipKeepHidden, _pipMinimizedOriginals, _pipTaskbar, _pipTitleBar, Row(T("pip.hover.mode"), _pipPreview), Note(T("pip.hover.windowsOnly"))));
        return page;
    }

    private string PictureFolderInUse => _pictureFolder ?? Pip.PictureShelf.DefaultFolder();

    private string RecordingFolderInUse => _recordingFolder ?? Pip.PictureShelf.DefaultRecordingFolder();

    private void ShowRecordingFolder()
    {
        if (_recordingFolderRow is not null) _recordingFolderRow.Description = RecordingFolderInUse;
    }

    private void ShowPictureFolder()
    {
        if (_pictureFolderRow is null) return;
        _pictureFolderRow.Description = PictureFolderInUse;
        _pictureFolderChange.Enabled = _pictureSave.SelectedIndex != CopyOnly;
    }

    private FlowLayoutPanel BuildPrograms()
    {
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("programs.exclusions"), Programs(_excluded, _newExcluded, null, T("programs.empty.excluded"))));
        page.Controls.Add(Section(T("programs.inclusions"), Programs(_included, _newIncluded, null, T("programs.empty.included")),
            Note(T("programs.precedence"))));
        return page;
    }

    private static ProgramList Programs(ThemedList list, ComboBox picker, string? title, string empty) =>
        new(list, picker, title, empty, T("common.add"), T("common.remove"));

    private FlowLayoutPanel BuildSetups()
    {
        FlowLayoutPanel page = ScrollingPage();
        _setups.IconOf = (item, size) => item is ProgramSetup setup ? ProgramIcons.Of(setup.Program, size) : null;
        var setups = new ProgramList(_setups, null, null, T("settings.setups.empty"), T("common.add"), T("common.remove"),
            item => RemoveSetup((ProgramSetup)item), item => ChangeSetup((ProgramSetup)item), T("common.change"));
        page.Controls.Add(Section(T("settings.savedSetups"), Note(T("settings.setups.help")), setups));
        return page;
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
                AppSettings imported = SettingsTransfer.Import(dialog.FileName, _settings);
                if (imported.Backgrounds.RemoteCount is var remote and > 0 &&
                    ThemedMessage.Show(this, string.Format(T("settings.transfer.remoteBackgrounds"), remote), T("settings.transfer.title"),
                        okCancel: true) != DialogResult.OK)
                    imported.Backgrounds = imported.Backgrounds.WithoutRemote();
                _pendingImport = imported;
                _removedSetups.Clear();
                _changedSetups.Clear();
                _removedKept.Clear();
                LoadValues();
                if (ApplyChanges()) _status.Text = T("settings.transfer.imported");
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
            _opacityWheel, _opaqueOnHover));
        var color = new InputFrame(_borderColor) { Width = NumberWidth + 10, Swatch = () => HexColor(_borderColor.Text) };
        _borderColor.TextChanged += (_, _) => color.Invalidate();
        page.Controls.Add(Section(T("settings.pin.border"), _pausePin, _highlightPinned, SettingRow(T("settings.pin.color"), color),
            SettingRow(T("settings.pin.opacity"), _borderOpacity), SettingRow(T("settings.pin.thickness"), _borderThickness)));
        return page;
    }

    private static Color? HexColor(string text) =>
        text.Trim() is { Length: 7 } hex && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int rgb)
            ? Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF) : null;

    private Control BuildToolbar()
    {
        _toolbarPlacement.Items.AddRange([T("toolbar.placement.left"), T("toolbar.placement.centre"), T("toolbar.placement.right")]);
        _toolbarTrigger.Items.AddRange([T("toolbar.trigger.titleBar"), T("toolbar.trigger.minimize"), T("toolbar.trigger.either")]);
        foreach (AppCommand command in CommandCatalog.ToolbarButtons) _toolbarButtons.Items.Add(new ToolbarChoice(command));
        _toolbarButtons.Tagged = item => item is ToolbarChoice { Command: var command } && CommandCatalog.IsExperimental(command);
        _toolbarButtons.IconOf = (item, pixels) => item is ToolbarChoice { Command: var shown }
            ? Tray.ColorIcons.Of(MenuParts.IconOf(shown) ?? MenuParts.More, pixels) : null;
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
        _anchorLook.Items.AddRange([T("anchors.look.dark"), T("anchors.look.light"), T("anchors.look.noBackground")]);
        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Section(T("anchors.settings.title"), _autoAnchors, Note(T("anchors.auto.help")),
            SettingRow(T("anchors.side.label"), _anchorSide), SettingRow(T("anchors.look.label"), _anchorLook),
            SettingRow(T("anchors.limit"), _anchorLimit), SettingRow(T("anchors.size"), _anchorSize),
            _anchorRecency, _anchorAvoid, Note(T("anchors.avoid.help"))));
        page.Controls.Add(Section(T("anchors.every.title"), _anchorPreview, _anchorOffTaskbar, Note(T("anchors.offTaskbar.help")),
            _anchorEveryDesktop, Note(T("anchors.everyDesktop.help"))));
        page.Controls.Add(Section(T("anchors.auto.only"), Programs(_anchorOnly, _newAnchorOnly, null, T("programs.empty.included"))));
        page.Controls.Add(Section(T("anchors.auto.excluded"), Programs(_anchorExcluded, _newAnchorExcluded, null, T("programs.empty.excluded")),
            Note(T("programs.alwaysLeftAlone"))));
        return page;
    }

    private static Control ProgramRules(string title, ThemedList list, ComboBox picker) =>
        Programs(list, picker, title.TrimEnd(':', '：'), T("programs.empty.excluded"));
}
