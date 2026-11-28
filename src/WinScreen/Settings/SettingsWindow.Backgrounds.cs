using static WinScreen.Localization.UiText;
using WinScreen.Glass;

namespace WinScreen.Settings;

// The list holds Every app and the apps with their own background, Glass or Dark levels; the three cards under it
// (Background, Glass, Dark mode) edit the one chosen in the list. Every app's cards hold what every other app starts from.
public sealed partial class SettingsWindow
{
    private const int MostBackgroundRows = 5;
    private const int MostKeptRows = 4;
    private readonly SwitchBox _ownBackground = new() { Text = T("settings.apps.ownBackground"), AutoSize = true };
    private readonly SwitchBox _ownGlass = new() { Text = T("settings.apps.ownGlass"), AutoSize = true };
    private readonly SwitchBox _ownDark = new() { Text = T("settings.apps.ownDark"), AutoSize = true };
    private readonly List<Control> _appDarkRows = [];
    private readonly List<Control> _mainDarkRows = [];
    private Control? _appDarkNote;
    private SettingsCard? _glassCard, _darkCard;
    private readonly List<Control> _backgroundRows = [];
    // Added here and not yet given anything of their own: they stay in the list until it is filled again.
    private readonly List<string> _listedApps = [];
    private readonly NumericUpDown _appGlassBackground = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appGlassText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appGlassFade = new CurvedNumber { Minimum = 1, Maximum = EffectPreferences.MostFadeRange, Width = 70, Curve = CurvedNumber.FadeCurve };
    private readonly ComboBox _appGlassTextColor = TextColorPicker();
    private readonly NumericUpDown _appGlassShadow = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appGlassShadowWidth = new() { Minimum = TextShadowLevels.Thinnest, Maximum = TextShadowLevels.Widest, Width = 70 };
    private readonly NumericUpDown _appDarkPage = new() { Minimum = 0, Maximum = EffectPreferences.MostDarkPage, Width = 70 };
    private readonly NumericUpDown _appDarkText = new() { Minimum = EffectPreferences.LeastDarkText, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appDarkColors = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly List<Control> _appGlassRows = [];
    private readonly List<Control> _mainGlassRows = [];
    private Control? _appGlassNote;
    private List<AppGlassLevels> _glassApps = [];
    private bool _showingGlassApp;
    private readonly ThemedList _keptList = new() { AccessibleName = T("settings.keptAreas.list"), EmptyText = T("settings.keptAreas.empty") };
    private readonly PlainButton _keptRemove = new() { Text = T("common.remove"), Enabled = false };
    // Taken away here but still in effect until applied; areas marked from a window's menu meanwhile are kept.
    private readonly HashSet<string> _removedKept = new(StringComparer.OrdinalIgnoreCase);
    private InputFrame? _keptFrame;
    private readonly BackgroundList _backgroundList = new() { AccessibleName = T("settings.backgrounds.list") };
    private readonly ComboBox _newBackgroundApp = ProgramPicker();
    private readonly BackdropPreview _backgroundPreview = new() { AccessibleName = T("settings.backgrounds.preview") };
    private readonly PlainButton _backgroundChoose = new() { Text = T("common.add"), AutoSize = true };
    private readonly PlainButton _backgroundLink = new() { Text = T("settings.backgrounds.addLink"), AutoSize = true };
    private readonly PlainButton _backgroundClear = new() { Text = T("settings.backgrounds.clear"), AutoSize = true };
    private readonly PlainButton _itemUp = new() { Text = T("settings.backgrounds.moveUp"), AutoSize = true };
    private readonly PlainButton _itemDown = new() { Text = T("settings.backgrounds.moveDown"), AutoSize = true };
    private readonly PlainButton _applyAll = new() { Text = T("settings.backgrounds.applyAllButton"), AutoSize = true };
    private Control? _applyAllRow;
    private readonly SwitchBox _itemOwnGlass = new() { Text = T("settings.backgrounds.ownGlass"), AutoSize = true };
    private readonly NumericUpDown _itemPage = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _itemText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _itemFade = new CurvedNumber { Minimum = 1, Maximum = EffectPreferences.MostFadeRange, Width = 70, Curve = CurvedNumber.FadeCurve };
    private readonly ComboBox _itemTextColor = TextColorPicker();
    private readonly NumericUpDown _itemShadow = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _itemShadowWidth = new() { Minimum = TextShadowLevels.Thinnest, Maximum = TextShadowLevels.Widest, Width = 70 };
    private readonly List<Control> _itemGlassRows = [];
    private readonly PlainButton _backgroundRemoveApp = new() { Text = T("common.remove"), Enabled = false };
    private readonly ComboBox _backgroundFit = new ThemedCombo { Width = 200 };
    private readonly NumericUpDown _backgroundVolume = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _backgroundSpeed = new() { Minimum = BackgroundItem.SlowestSpeed, Maximum = BackgroundItem.FastestSpeed, Increment = 5, Width = 70 };
    private readonly NumericUpDown _backgroundBrightness = new() { Minimum = BackgroundItem.DimmestBrightness, Maximum = 100, Width = 70 };
    private readonly ComboBox _backgroundOrder = new ThemedCombo { Width = 200 };
    private readonly ComboBox _backgroundSwitch = new ThemedCombo { Width = 200 };
    private readonly ComboBox _pictureMinutes = new ThemedCombo { Width = 120 };
    private static int[] PictureMinuteChoices => BackgroundChoice.MinuteChoices;
    // The pictures and videos of the background shown, when there is more than one; the rows under it edit the one chosen.
    private readonly ThemedList _itemList = new() { AccessibleName = T("settings.backgrounds.items") };
    private InputFrame? _itemFrame;
    private readonly List<Control> _setRows = [];
    private int _itemAt;
    private readonly SwitchBox _soundInFront = new() { Text = T("settings.backgrounds.soundInFront"), AutoSize = true };
    private readonly SwitchBox _pauseFullScreen = new() { Text = T("settings.backgrounds.pauseFullScreen"), AutoSize = true };
    private readonly SwitchBox _pauseSaver = new() { Text = T("settings.backgrounds.pauseSaver"), AutoSize = true };
    private TabPage? _backgroundsPage;
    private FlowLayoutPanel? _backgroundsScroll;
    private SettingsCard? _backgroundEditor;
    private SettingRowPanel? _backgroundFileRow;
    private InputFrame? _backgroundFrame;
    private BackgroundPreferences _backgroundEdit = new();
    private bool _showingBackground;

    private FlowLayoutPanel BuildBackgrounds()
    {
        _backgroundFit.Items.AddRange([T("settings.backgrounds.fill"), T("settings.backgrounds.fitWhole"),
            T("settings.backgrounds.stretch"), T("settings.backgrounds.tile"), T("settings.backgrounds.center")]);
        _backgroundOrder.Items.AddRange([T("settings.backgrounds.order.inOrder"), T("settings.backgrounds.order.shuffle")]);
        _backgroundSwitch.Items.AddRange([T("settings.backgrounds.switch.videoEnds"), T("settings.backgrounds.switch.timed")]);
        _pictureMinutes.Items.AddRange([.. PictureMinuteChoices.Select(minutes => (object)minutes.ToString(System.Globalization.CultureInfo.CurrentCulture))]);
        _backgroundList.SelectedIndexChanged += (_, _) => { _itemAt = 0; ShowBackground(controls: true); };
        _backgroundList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || ShownApp is null) return;
            e.Handled = true;
            RemoveBackgroundApp();
        };
        _backgroundFrame = new InputFrame(_backgroundList, InputFrame.Look.List) { Width = SwitchBox.RowWidth, Margin = new Padding(3, 6, 3, 4) };
        _backgroundList.FontChanged += (_, _) => FitBackgroundList();
        _backgroundFrame.DpiChangedAfterParent += (_, _) => FitBackgroundList();

        _newBackgroundApp.Dock = DockStyle.None;
        _newBackgroundApp.Width = 300;
        var add = new PlainButton { Text = T("common.add"), Enabled = false };
        _newBackgroundApp.TextChanged += (_, _) => add.Enabled = _newBackgroundApp.Text.Trim().Length > 0;
        add.Click += (_, _) => AddBackgroundApp();
        _newBackgroundApp.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_newBackgroundApp.DroppedDown) _newBackgroundApp.DroppedDown = false; // Enter on a suggestion takes it and adds it.
            AddBackgroundApp();
        };
        _newBackgroundApp.Margin = new Padding(0, 0, 8, 0);
        add.Margin = new Padding(0, 0, 8, 0);
        _backgroundRemoveApp.Margin = Padding.Empty;
        _newBackgroundApp.Anchor = add.Anchor = _backgroundRemoveApp.Anchor = AnchorStyles.Left;
        var adding = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 3, 6), BackColor = SettingsStyle.Card };
        adding.Controls.Add(_newBackgroundApp);
        adding.Controls.Add(add);
        adding.Controls.Add(_backgroundRemoveApp);

        _backgroundChoose.Click += (_, _) => ChooseBackground();
        _backgroundLink.Click += (_, _) => AddLink();
        _backgroundPreview.ChooseAsked += ChooseBackground;
        _backgroundPreview.Dropped += AddItem;
        _backgroundClear.Click += (_, _) => RemoveItem();
        _itemUp.Click += (_, _) => MoveItem(-1);
        _itemDown.Click += (_, _) => MoveItem(1);
        _applyAll.Click += (_, _) => ChangeBackground(choice => choice.WithLookOf(_itemAt));
        _itemOwnGlass.CheckedChanged += (_, _) =>
            ChangeItem(item => item with { GlassLevels = _itemOwnGlass.Checked ? ItemGlass.From(AppGlassShown(ShownApp)) : null });
        foreach (NumericUpDown box in new[] { _itemPage, _itemText, _itemFade, _itemShadow, _itemShadowWidth }) box.TextChanged += (_, _) => ChangeItemGlass();
        _itemTextColor.SelectedIndexChanged += (_, _) => ChangeItemGlass();
        _itemList.SelectedIndexChanged += (_, _) =>
        {
            if (_showingBackground || _itemList.SelectedIndex < 0) return;
            _itemAt = _itemList.SelectedIndex;
            ShowBackground(controls: true);
        };
        _itemList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _itemList.SelectedIndex < 0) return;
            e.Handled = true;
            RemoveItem();
        };
        _itemFrame = new InputFrame(_itemList, InputFrame.Look.List) { Width = SwitchBox.RowWidth, Margin = new Padding(3, 6, 3, 4) };
        _itemList.FontChanged += (_, _) => FitItemList();
        _itemFrame.DpiChangedAfterParent += (_, _) => FitItemList();
        _backgroundFit.SelectedIndexChanged += (_, _) =>
            ChangeItem(item => item with { Fit = (BackdropFit)Math.Max(0, _backgroundFit.SelectedIndex) });
        _backgroundVolume.TextChanged += (_, _) => ChangeItem(item => item with { Volume = Typed(_backgroundVolume) });
        _backgroundSpeed.TextChanged += (_, _) => ChangeItem(item => item with { Speed = Typed(_backgroundSpeed) });
        _backgroundBrightness.TextChanged += (_, _) => ChangeItem(item => item with { Brightness = Typed(_backgroundBrightness) });
        _backgroundOrder.SelectedIndexChanged += (_, _) =>
            ChangeBackground(choice => choice with { Order = (BackdropOrder)Math.Max(0, _backgroundOrder.SelectedIndex) });
        _backgroundSwitch.SelectedIndexChanged += (_, _) =>
            ChangeBackground(choice => choice with { Switch = (BackdropSwitch)Math.Max(0, _backgroundSwitch.SelectedIndex) });
        _pictureMinutes.SelectedIndexChanged += (_, _) =>
            ChangeBackground(choice => choice with { PictureMinutes = PictureMinuteChoices[Math.Max(0, _pictureMinutes.SelectedIndex)] });
        _backgroundRemoveApp.Click += (_, _) => RemoveBackgroundApp();
        foreach (PlainButton button in new[] { _backgroundChoose, _backgroundLink, _backgroundClear, _itemUp })
            button.Margin = new Padding(0, 0, 8, 0);
        _itemDown.Margin = Padding.Empty;
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, BackColor = SettingsStyle.Card };
        buttons.Controls.AddRange([_backgroundChoose, _backgroundLink, _backgroundClear, _itemUp, _itemDown]);
        _backgroundFileRow = (SettingRowPanel)SettingRow(T("settings.backgrounds.file"), buttons);
        _setRows.AddRange([_itemFrame, SettingRow(T("settings.backgrounds.order"), _backgroundOrder),
            SettingRow(T("settings.backgrounds.switch"), _backgroundSwitch), Note(T("settings.backgrounds.switch.help")),
            SettingRow(T("settings.backgrounds.pictureMinutes"), _pictureMinutes), Note(T("settings.backgrounds.setHelp"))]);
        _backgroundRows.AddRange([_backgroundPreview, _backgroundFileRow, .. _setRows, SettingRow(T("settings.backgrounds.fit"), _backgroundFit),
            SettingRow(T("settings.backgrounds.sound"), _backgroundVolume), Note(T("settings.backgrounds.soundHelp")),
            SettingRow(T("settings.backgrounds.speed"), _backgroundSpeed),
            SettingRow(T("settings.backgrounds.brightness"), _backgroundBrightness)]);
        _itemGlassRows.AddRange([SettingRow(T("settings.effects.background"), _itemPage), SettingRow(T("settings.effects.text"), _itemText),
            SettingRow(T("settings.effects.fade"), _itemFade), SettingRow(T("settings.effects.textColor"), _itemTextColor),
            SettingRow(T("settings.effects.shadow"), _itemShadow), SettingRow(T("settings.effects.shadowWidth"), _itemShadowWidth)]);
        _backgroundRows.AddRange([_itemOwnGlass, Note(T("settings.backgrounds.ownGlassHelp")), .. _itemGlassRows,
            _applyAllRow = SettingRow(T("settings.backgrounds.applyAll"), _applyAll), Note(T("settings.backgrounds.applyAllHelp"))]);
        _ownBackground.CheckedChanged += (_, _) => SetOwnBackground(_ownBackground.Checked);
        _ownGlass.CheckedChanged += (_, _) => SetOwnPart(_ownGlass.Checked, dark: false);
        _ownDark.CheckedChanged += (_, _) => SetOwnPart(_ownDark.Checked, dark: true);

        _keptFrame = new InputFrame(_keptList, InputFrame.Look.List) { Width = SwitchBox.RowWidth, Margin = new Padding(3, 6, 3, 4) };
        _keptList.FontChanged += (_, _) => FitKeptList();
        _keptFrame.DpiChangedAfterParent += (_, _) => FitKeptList();
        _keptList.SelectedIndexChanged += (_, _) => _keptRemove.Enabled = _keptList.SelectedItem is not null;
        _keptList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _keptList.SelectedItem is null) return;
            e.Handled = true;
            RemoveKept();
        };
        _keptRemove.Click += (_, _) => RemoveKept();
        _keptRemove.Margin = Padding.Empty;
        var keptButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 3, 6), BackColor = SettingsStyle.Card };
        keptButtons.Controls.Add(_keptRemove);

        foreach (NumericUpDown box in new[] { _appGlassBackground, _appGlassText, _appGlassFade, _appGlassShadow, _appGlassShadowWidth, _appDarkPage, _appDarkText, _appDarkColors })
            box.TextChanged += (_, _) => ChangeGlassApp();
        _appGlassTextColor.SelectedIndexChanged += (_, _) => ChangeGlassApp();
        _appGlassRows.Add(SettingRow(T("settings.effects.background"), _appGlassBackground));
        _appGlassRows.Add(SettingRow(T("settings.effects.text"), _appGlassText));
        _appGlassRows.Add(SettingRow(T("settings.effects.fade"), _appGlassFade));
        _appGlassRows.Add(SettingRow(T("settings.effects.textColor"), _appGlassTextColor));
        _appGlassRows.Add(SettingRow(T("settings.effects.shadow"), _appGlassShadow));
        _appGlassRows.Add(SettingRow(T("settings.effects.shadowWidth"), _appGlassShadowWidth));
        _appDarkRows.Add(SettingRow(T("settings.dark.page"), _appDarkPage));
        _appDarkRows.Add(SettingRow(T("settings.dark.text"), _appDarkText));
        _appDarkRows.Add(SettingRow(T("settings.dark.colors"), _appDarkColors));
        _mainGlassRows.AddRange([SettingRow(T("settings.effects.background"), _glassBackground), SettingRow(T("settings.effects.text"), _glassText),
            SettingRow(T("settings.effects.fade"), _glassFade), Note(T("settings.effects.fadeHelp")),
            SettingRow(T("settings.effects.textColor"), _glassTextColor), Note(T("settings.effects.textColorHelp")),
            SettingRow(T("settings.effects.shadow"), _glassShadow), SettingRow(T("settings.effects.shadowWidth"), _glassShadowWidth),
            Note(T("settings.effects.shadowHelp")),
            Note(T("settings.effects.help"))]);
        _appGlassNote = Note(T("settings.apps.glassHelp"));
        _appDarkNote = Note(T("settings.apps.darkHelp"));
        _mainDarkRows.AddRange([SettingRow(T("settings.dark.page"), _darkPage), SettingRow(T("settings.dark.text"), _darkText),
            SettingRow(T("settings.dark.colors"), _darkColors), Note(T("settings.dark.colorsHelp")), Note(T("settings.effects.help"))]);
        _backgroundEditor = Card(T("settings.backgrounds.everyApp"), false,
            [Note(T("settings.effects.backgroundAbout")), _ownBackground, .. _backgroundRows]);
        _glassCard = Card(T("settings.backgrounds.everyApp"), false,
            [Note(T("settings.effects.glassAbout")), .. _mainGlassRows, _ownGlass, _appGlassNote, .. _appGlassRows]);
        _darkCard = Card(T("settings.backgrounds.everyApp"), false,
            [Note(T("settings.effects.darkAbout")), .. _mainDarkRows, _ownDark, _appDarkNote, .. _appDarkRows]);

        foreach (SwitchBox rule in new[] { _soundInFront, _pauseFullScreen, _pauseSaver })
            rule.CheckedChanged += (_, _) =>
            {
                if (_showingBackground || _loading) return;
                _backgroundEdit = _backgroundEdit with
                {
                    SoundOnlyInFront = _soundInFront.Checked, PauseInFullScreen = _pauseFullScreen.Checked, PauseOnBatterySaver = _pauseSaver.Checked,
                };
                QueueApplyRefresh();
            };

        // Back from File Explorer, say, where a file may have been moved or put back.
        Activated += (_, _) =>
        {
            if (_gone.Count == 0) return;
            _gone.Clear();
            ShowBackground(controls: false);
        };
        FlowLayoutPanel page = ScrollingPage();
        _backgroundsScroll = page;
        page.Controls.Add(Section(T("settings.backgrounds.title"), Note(T("settings.backgrounds.help")), _backgroundFrame, adding));
        page.Controls.Add(_backgroundEditor);
        page.Controls.Add(_glassCard);
        page.Controls.Add(_darkCard);
        // Shown whichever app is chosen above: they are the same for all of them.
        page.Controls.Add(Section(T("settings.effects.allWindows"), Note(T("settings.effects.allWindowsHelp")),
            _layeredGlass, Note(T("settings.effects.layeredGlassHelp")), _detectDark, _soundInFront, _pauseFullScreen, _pauseSaver));
        page.Controls.Add(Section(T("settings.keptAreas.title"), Note(T("settings.keptAreas.help")), _keptFrame, keptButtons));
        ListBackgrounds(null);
        ListKept();
        return page;
    }

    // Saved from a window's own Glass panel: what is being edited here for other apps stays.
    internal void AppGlassChanged(string program)
    {
        bool wasPending = _pending;
        AppGlassLevels? saved = _settings.Effects.OwnOf(program);
        int at = _glassApps.FindIndex(app => SameApp(app.Program, program));
        if (saved is null) { if (at >= 0) _glassApps.RemoveAt(at); }
        else if (at >= 0) _glassApps[at] = saved;
        else _glassApps.Add(saved);
        ListBackgrounds(ShownApp);
        if (!wasPending) MarkSaved();
        else RefreshApply();
    }

    private void FillGlassApps(EffectPreferences effects)
    {
        _glassApps = [.. effects.Apps];
        ListBackgrounds(ShownApp);
    }

    private static bool SameApp(string a, string b) =>
        string.Equals(BackgroundPreferences.ProgramKey(a), BackgroundPreferences.ProgramKey(b), StringComparison.OrdinalIgnoreCase);

    private List<string> ListedApps()
    {
        var apps = new List<string>();
        foreach (string program in _backgroundEdit.Apps.Select(app => app.Program).Concat(_glassApps.Select(app => app.Program)).Concat(_listedApps))
            if (!apps.Any(listed => SameApp(listed, program))) apps.Add(program);
        return apps;
    }

    private AppGlassLevels? GlassOf(string? program) => program is null ? null : _glassApps.Find(app => SameApp(app.Program, program));

    // An app's own levels start from the ones every app has, as they are set above.
    private AppGlassLevels MainGlass(string program) => new()
    {
        Program = program, BackgroundPercent = Typed(_glassBackground), TextPercent = Typed(_glassText), FadeRangePercent = Typed(_glassFade),
        TextColor = TextColorOf(_glassTextColor), ShadowPercent = Typed(_glassShadow), ShadowWidth = Typed(_glassShadowWidth),
        DarkPagePercent = Typed(_darkPage), DarkTextPercent = Typed(_darkText), DarkColorsPercent = Typed(_darkColors),
    };

    private static ComboBox TextColorPicker()
    {
        var picker = new ThemedCombo { Width = 200 };
        picker.Items.AddRange([T("settings.effects.textColor.asItIs"), T("settings.effects.textColor.light"), T("settings.effects.textColor.dark")]);
        picker.SelectedIndex = 0;
        return picker;
    }

    private static Glass.GlassText TextColorOf(ComboBox picker) => (Glass.GlassText)Math.Max(0, picker.SelectedIndex);

    // Each part the app's own, or Every app's while it has none.
    private AppGlassLevels AppGlassShown(string? program)
    {
        AppGlassLevels? own = GlassOf(program);
        AppGlassLevels main = MainGlass(program ?? "");
        if (own is null) return main;
        AppGlassLevels shown = own.OwnGlass ? own : own.WithGlassOf(main);
        return own.OwnDark ? shown : shown.WithDarkOf(main);
    }

    private void ChangeItemGlass()
    {
        if (!_itemOwnGlass.Checked) return;
        ChangeItem(item => item with
        {
            GlassLevels = new ItemGlass
            {
                PagePercent = Typed(_itemPage), TextPercent = Typed(_itemText), FadeRangePercent = Typed(_itemFade),
                TextColor = TextColorOf(_itemTextColor), ShadowPercent = Typed(_itemShadow), ShadowWidth = Typed(_itemShadowWidth),
            },
        });
    }

    // Its own Glass levels, or the app's, which it uses until it has its own.
    private void ShowItemGlass(string? app, BackgroundItem? item, bool ownBackground)
    {
        ItemGlass glass = item?.GlassLevels ?? ItemGlass.From(AppGlassShown(app));
        bool was = _showingBackground;
        _showingBackground = true;
        try
        {
            _itemOwnGlass.Checked = item?.GlassLevels is not null;
            _itemPage.Value = glass.PagePercent;
            _itemText.Value = glass.TextPercent;
            _itemFade.Value = glass.FadeRangePercent;
            _itemTextColor.SelectedIndex = (int)glass.TextColor;
            _itemShadow.Value = Math.Clamp(glass.ShadowPercent, 0, 100);
            _itemShadowWidth.Value = Math.Clamp(glass.ShadowWidth, TextShadowLevels.Thinnest, TextShadowLevels.Widest);
        }
        finally { _showingBackground = was; }
        _itemOwnGlass.Visible = item is not null;
        // Only while it has its own: otherwise they would repeat the Glass card's below.
        foreach (Control row in _itemGlassRows)
        {
            row.Visible = item?.GlassLevels is not null;
            row.Enabled = ownBackground;
        }
    }

    // Each part shows the app's own, or Every app's while it has none.
    private void ShowGlassApp(string? program)
    {
        AppGlassLevels? own = GlassOf(program);
        AppGlassLevels shown = AppGlassShown(program);
        _showingGlassApp = true;
        try
        {
            _ownGlass.Checked = own is { OwnGlass: true };
            _ownDark.Checked = own is { OwnDark: true };
            _appGlassBackground.Value = shown.BackgroundPercent;
            _appGlassText.Value = shown.TextPercent;
            _appGlassFade.Value = shown.FadeRangePercent;
            _appGlassTextColor.SelectedIndex = (int)shown.TextColor;
            _appGlassShadow.Value = Math.Clamp(shown.ShadowPercent, 0, 100);
            _appGlassShadowWidth.Value = Math.Clamp(shown.ShadowWidth, TextShadowLevels.Thinnest, TextShadowLevels.Widest);
            _appDarkPage.Value = Math.Clamp(shown.DarkPagePercent, 0, EffectPreferences.MostDarkPage);
            _appDarkText.Value = Math.Clamp(shown.DarkTextPercent, EffectPreferences.LeastDarkText, 100);
            _appDarkColors.Value = Math.Clamp(shown.DarkColorsPercent, 0, 100);
        }
        finally { _showingGlassApp = false; }
        foreach (Control row in _appGlassRows) row.Enabled = own is { OwnGlass: true };
        foreach (Control row in _appDarkRows) row.Enabled = own is { OwnDark: true };
    }

    private void ChangeGlassApp()
    {
        if (_showingGlassApp || _loading || GlassOf(ShownApp) is not { } app) return;
        int at = _glassApps.IndexOf(app);
        _glassApps[at] = app with
        {
            BackgroundPercent = Typed(_appGlassBackground), TextPercent = Typed(_appGlassText), FadeRangePercent = Typed(_appGlassFade),
            TextColor = TextColorOf(_appGlassTextColor), ShadowPercent = Typed(_appGlassShadow), ShadowWidth = Typed(_appGlassShadowWidth),
            DarkPagePercent = Typed(_appDarkPage), DarkTextPercent = Typed(_appDarkText), DarkColorsPercent = Typed(_appDarkColors),
        };
        QueueApplyRefresh();
    }

    // A part made the app's own starts as Every app's, so nothing changes until it is changed.
    private void SetOwnPart(bool on, bool dark)
    {
        if (_showingGlassApp || _showingBackground || _loading || ShownApp is not { } program) return;
        AppGlassLevels? own = GlassOf(program);
        if (on == (dark ? own is { OwnDark: true } : own is { OwnGlass: true })) return;
        AppGlassLevels main = MainGlass(program), next = own ?? main with { OwnGlass = false };
        next = dark ? next.WithDarkOf(main) with { OwnDark = on } : next.WithGlassOf(main) with { OwnGlass = on };
        if (own is not null) _glassApps.Remove(own);
        if (next.OwnGlass || next.OwnDark) _glassApps.Add(next);
        if (!_listedApps.Any(listed => SameApp(listed, program))) _listedApps.Add(program);
        ShowGlassApp(program);
        QueueApplyRefresh();
    }

    // Its own starts as Every app's, so nothing changes until it is changed.
    private void SetOwnBackground(bool on)
    {
        if (_showingBackground || _loading || ShownApp is not { } program) return;
        bool has = _backgroundEdit.OwnOf(program) is not null;
        if (on == has) return;
        _backgroundEdit = on
            ? _backgroundEdit with { Apps = [.. _backgroundEdit.Apps, new AppBackground { Program = program, Background = _backgroundEdit.Everywhere }] }
            : _backgroundEdit with { Apps = _backgroundEdit.Apps.Where(app => !SameApp(app.Program, program)).ToList() };
        if (!_listedApps.Any(listed => SameApp(listed, program))) _listedApps.Add(program);
        ShowBackground(controls: true);
        QueueApplyRefresh();
    }

    internal void KeptAreasChanged()
    {
        bool wasPending = _pending;
        ListKept();
        if (!wasPending) MarkSaved();
        else RefreshApply();
    }

    private void ListKept()
    {
        string? selected = (_keptList.SelectedItem as KeptEntry)?.Program;
        _keptList.BeginUpdate();
        _keptList.Items.Clear();
        foreach (AppKeptAreas app in _settings.KeptAreas.Apps.Where(app => !_removedKept.Contains(app.Program)))
            _keptList.Items.Add(new KeptEntry(app.Program, app.Areas.Count));
        _keptList.EndUpdate();
        int at = _keptList.Items.Cast<KeptEntry>().ToList().FindIndex(entry => string.Equals(entry.Program, selected, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _keptList.SelectedIndex = at;
        _keptRemove.Enabled = _keptList.SelectedItem is not null;
        FitKeptList();
    }

    private void FitKeptList()
    {
        if (_keptFrame is null) return;
        int rows = Math.Clamp(_keptList.Items.Count, 1, MostKeptRows);
        _keptFrame.Height = _keptList.ItemHeight * rows + SettingsStyle.Unit(_keptFrame, 8);
    }

    private void RemoveKept()
    {
        if (_keptList.SelectedItem is not KeptEntry entry) return;
        int at = _keptList.SelectedIndex;
        bool hadKeyboard = _keptRemove.Focused;
        _removedKept.Add(entry.Program);
        ListKept();
        if (_keptList.Items.Count > 0) _keptList.SelectedIndex = Math.Min(at, _keptList.Items.Count - 1);
        if (hadKeyboard && !_keptRemove.Enabled) _keptList.Focus();
        QueueApplyRefresh();
    }

    private sealed record KeptEntry(string Program, int Count)
    {
        public override string ToString() => $"{Program} ({Count})";
    }

    private string? ShownApp => (_backgroundList.SelectedItem as BackgroundEntry)?.Program;

    private BackgroundChoice ChoiceOf(string? program) => program is null ? _backgroundEdit.Everywhere : _backgroundEdit.OwnOf(program) ?? _backgroundEdit.Everywhere;

    private static bool IsVideo(string file) => BackdropChoice.IsVideoText(file);

    // Looked up away from this thread, once each time the list fills: a file on a share that is gone takes seconds to say
    // so. Until the answer comes, a file counts as there.
    private readonly Dictionary<string, bool> _gone = new(StringComparer.OrdinalIgnoreCase);

    private bool Gone(string file)
    {
        if (_gone.TryGetValue(file, out bool gone)) return gone;
        _gone[file] = false;
        _ = LookFor(file);
        return false;
    }

    private async Task LookFor(string file)
    {
        if (!await Task.Run(() => new BackdropChoice(file, BackdropFit.Fill).IsMissing) || IsDisposed) return;
        _gone[file] = true;
        ShowBackground(controls: false);
    }

    // A file by its name, a link as it is, and either marked when it can't be found.
    private string NameOf(string file)
    {
        if (BackdropChoice.IsLinkText(file)) return file.Trim();
        string name = Path.GetFileName(file);
        return Gone(file) ? string.Format(T("settings.backgrounds.missing"), name) : name;
    }

    private string ItemText(BackgroundItem item) =>
        item.Volume > 0 && IsVideo(item.File) ? string.Format(T("settings.backgrounds.withSound"), NameOf(item.File), item.Volume) : NameOf(item.File);

    private string DetailOf(string? program)
    {
        if (program is not null && _backgroundEdit.OwnOf(program) is null) return T("settings.apps.everyBackground");
        IReadOnlyList<BackgroundItem> items = ChoiceOf(program).Items;
        if (items.Count == 0) return program is null ? T("settings.backgrounds.none") : T("settings.backgrounds.appNone");
        return items.Count == 1 ? ItemText(items[0]) : string.Format(T("settings.backgrounds.andMore"), NameOf(items[0].File), items.Count - 1);
    }

    private BackgroundItem? ShownItem(BackgroundChoice choice) => choice.Items is { Count: > 0 } items ? items[Math.Clamp(_itemAt, 0, items.Count - 1)] : null;

    private void FitItemList()
    {
        if (_itemFrame is null) return;
        int rows = Math.Clamp(_itemList.Items.Count, 2, MostBackgroundRows);
        _itemFrame.Height = _itemList.ItemHeight * rows + SettingsStyle.Unit(_itemFrame, 8);
    }

    private void ChangeItem(Func<BackgroundItem, BackgroundItem> change) => ChangeBackground(choice =>
    {
        IReadOnlyList<BackgroundItem> items = choice.Items;
        if (items.Count == 0) return choice;
        int at = Math.Clamp(_itemAt, 0, items.Count - 1);
        return choice.WithItems(items.Select((item, index) => index == at ? change(item) : item));
    });

    // After the one shown, taking its fit so a set looks alike.
    private void AddItem(string file)
    {
        BackgroundChoice choice = ChoiceOf(ShownApp);
        BackgroundItem? like = ShownItem(choice);
        var added = new BackgroundItem { File = file.Trim(), Fit = like?.Fit ?? BackdropFit.Fill };
        int at = choice.Items.Count == 0 ? 0 : Math.Clamp(_itemAt, 0, choice.Items.Count - 1) + 1;
        _itemAt = at;
        ChangeBackground(current => current.WithItems(current.Items.Take(at).Append(added).Concat(current.Items.Skip(at))));
        ShowBackground(controls: true);
    }

    private void RemoveItem()
    {
        BackgroundChoice choice = ChoiceOf(ShownApp);
        if (choice.Items.Count == 0) return;
        int at = Math.Clamp(_itemAt, 0, choice.Items.Count - 1);
        _itemAt = Math.Max(0, Math.Min(at, choice.Items.Count - 2));
        ChangeBackground(current => current.WithItems(current.Items.Where((_, index) => index != at)));
        ShowBackground(controls: true);
    }

    private void MoveItem(int by)
    {
        BackgroundChoice choice = ChoiceOf(ShownApp);
        int at = Math.Clamp(_itemAt, 0, Math.Max(0, choice.Items.Count - 1)), to = at + by;
        if (to < 0 || to >= choice.Items.Count) return;
        _itemAt = to;
        ChangeBackground(current =>
        {
            List<BackgroundItem> items = [.. current.Items];
            (items[at], items[to]) = (items[to], items[at]);
            return current.WithItems(items);
        });
        ShowBackground(controls: true);
    }

    private void AddLink()
    {
        if (LinkDialog.Ask(this) is { } link) AddItem(link);
    }

    private static int Typed(NumericUpDown box) =>
        decimal.TryParse(box.Text, out decimal typed) ? (int)Math.Clamp(typed, box.Minimum, box.Maximum) : (int)box.Value;

    private void FillBackgrounds(BackgroundPreferences values)
    {
        _backgroundEdit = values.Validated();
        _showingBackground = true;
        try
        {
            _soundInFront.Checked = _backgroundEdit.SoundOnlyInFront;
            _pauseFullScreen.Checked = _backgroundEdit.PauseInFullScreen;
            _pauseSaver.Checked = _backgroundEdit.PauseOnBatterySaver;
        }
        finally { _showingBackground = false; }
        ListBackgrounds(ShownApp);
    }

    // Saved from a window's menu (its background's sound): the same change here, so what is being edited stays.
    internal void BackgroundsSaved(Func<BackgroundPreferences, BackgroundPreferences> change)
    {
        bool wasPending = _pending;
        string? shown = ShownApp;
        _backgroundEdit = change(_backgroundEdit);
        ListBackgrounds(shown);
        if (!wasPending) MarkSaved();
        else RefreshApply();
    }

    // From a window's menu: its app chosen in the list, added to it when it has nothing of its own.
    internal void ShowBackgroundOf(string program)
    {
        string name = BackgroundPreferences.ProgramKey(program);
        if (name.Length == 0) return;
        if (_backgroundsPage is not null) PageIndex = _tabs.TabPages.IndexOf(_backgroundsPage);
        if (!ListedApps().Any(listed => SameApp(listed, name))) _listedApps.Add(name);
        _itemAt = 0;
        ListBackgrounds(ListedApps().First(listed => SameApp(listed, name)));
        if (_backgroundEditor is not null) _backgroundsScroll?.ScrollControlIntoView(_backgroundEditor);
    }

    private void ListBackgrounds(string? select)
    {
        BackgroundEntry Entry(string? program) =>
            new(program, program ?? T("settings.backgrounds.everyApp"), () => ChoiceOf(program), () => DetailOf(program));
        _gone.Clear();
        _backgroundList.BeginUpdate();
        _backgroundList.Items.Clear();
        _backgroundList.Items.Add(Entry(null));
        foreach (string program in ListedApps()) _backgroundList.Items.Add(Entry(program));
        _backgroundList.EndUpdate();
        int at = _backgroundList.Items.Cast<BackgroundEntry>().ToList()
            .FindIndex(entry => entry.Program is not null && string.Equals(entry.Program, select, StringComparison.OrdinalIgnoreCase));
        _backgroundList.SelectedIndex = Math.Max(0, at);
        FitBackgroundList();
        ShowBackground(controls: true);
    }

    private void FitBackgroundList()
    {
        if (_backgroundFrame is null) return;
        int rows = Math.Clamp(_backgroundList.Items.Count, 1, MostBackgroundRows);
        _backgroundFrame.Height = _backgroundList.ItemHeight * rows + SettingsStyle.Unit(_backgroundFrame, 8);
    }

    private void ShowBackground(bool controls)
    {
        if (_backgroundEditor is null || _backgroundFileRow is null) return;
        string? app = ShownApp;
        BackgroundChoice choice = ChoiceOf(app);
        if (controls)
        {
            _showingBackground = true;
            try
            {
                string name = app ?? T("settings.backgrounds.everyApp");
                _backgroundEditor.Text = string.Format(T("settings.effects.backgroundFor"), name);
                _glassCard?.Text = string.Format(T("settings.effects.glassFor"), name);
                _darkCard?.Text = string.Format(T("settings.effects.darkFor"), name);
                BackgroundItem item = ShownItem(choice) ?? new BackgroundItem();
                _backgroundFit.SelectedIndex = (int)item.Fit;
                _backgroundVolume.Value = item.Volume;
                _backgroundSpeed.Value = Math.Clamp(item.Speed, BackgroundItem.SlowestSpeed, BackgroundItem.FastestSpeed);
                _backgroundBrightness.Value = Math.Clamp(item.Brightness, BackgroundItem.DimmestBrightness, 100);
                _backgroundOrder.SelectedIndex = (int)choice.Order;
                _backgroundSwitch.SelectedIndex = (int)choice.Switch;
                // The nearest choice to a number from elsewhere.
                _pictureMinutes.SelectedIndex = Array.IndexOf(PictureMinuteChoices,
                    PictureMinuteChoices.MinBy(minutes => Math.Abs(minutes - choice.PictureMinutes)));
            }
            finally { _showingBackground = false; }
            _backgroundRemoveApp.Enabled = app is not null; // Every app stays.
            // Every app's own Glass levels are the ones every app starts from.
            _ownBackground.Visible = _ownGlass.Visible = _ownDark.Visible = app is not null;
            _appGlassNote?.Visible = app is not null;
            _appDarkNote?.Visible = app is not null;
            foreach (Control row in _appGlassRows.Concat(_appDarkRows)) row.Visible = app is not null;
            foreach (Control row in _mainGlassRows.Concat(_mainDarkRows)) row.Visible = app is null;
            ShowGlassApp(app);
        }
        bool own = app is null || _backgroundEdit.OwnOf(app) is not null;
        _showingBackground = true;
        try { _ownBackground.Checked = own; }
        finally { _showingBackground = false; }
        foreach (Control row in _backgroundRows) row.Enabled = own;
        IReadOnlyList<BackgroundItem> items = choice.Items;
        _itemAt = Math.Clamp(_itemAt, 0, Math.Max(0, items.Count - 1));
        BackgroundItem? shown = ShownItem(choice);
        bool chosen = shown is not null, several = items.Count > 1;
        _backgroundClear.Enabled = chosen;
        _itemUp.Visible = _itemDown.Visible = several;
        _itemUp.Enabled = _itemAt > 0;
        _itemDown.Enabled = _itemAt < items.Count - 1;
        foreach (Control row in _setRows) row.Visible = several;
        _applyAllRow?.Visible = several;
        ShowItemGlass(app, shown, own);
        bool video = chosen && IsVideo(shown!.File);
        _backgroundVolume.Enabled = video;
        _backgroundSpeed.Enabled = video || chosen && BackdropChoice.Kind(shown!.File) == ".gif";
        _backgroundFit.Enabled = _backgroundBrightness.Enabled = chosen;
        _showingBackground = true;
        try
        {
            string[] texts = [.. items.Select(ItemText)];
            if (!texts.SequenceEqual(_itemList.Items.Cast<string>()))
            {
                _itemList.BeginUpdate();
                _itemList.Items.Clear();
                _itemList.Items.AddRange([.. texts]);
                _itemList.EndUpdate();
            }
            if (items.Count > 0) _itemList.SelectedIndex = _itemAt;
        }
        finally { _showingBackground = false; }
        FitItemList();
        _backgroundFileRow.Description = DetailOf(app);
        if (shown is null) _backgroundPreview.Show("", BackdropFit.Fill, T("settings.backgrounds.drop"));
        else if (BackdropChoice.IsLinkText(shown.File)) _backgroundPreview.Show("", shown.Fit, string.Format(T("settings.backgrounds.linkPreview"), shown.File));
        else if (Gone(shown.File))
            _backgroundPreview.Show("", shown.Fit, string.Format(T("settings.backgrounds.missingPreview"), Path.GetFileName(shown.File)));
        else _backgroundPreview.Show(shown.File, shown.Fit, T("settings.backgrounds.drop"));
        _backgroundList.Invalidate();
    }

    private void ChangeBackground(Func<BackgroundChoice, BackgroundChoice> change)
    {
        if (_showingBackground || _loading) return;
        string? app = ShownApp;
        _backgroundEdit = app is null
            ? _backgroundEdit with { Everywhere = change(_backgroundEdit.Everywhere).Validated() }
            : _backgroundEdit with
            {
                Apps = _backgroundEdit.Apps.Select(entry => SameApp(entry.Program, app)
                    ? entry with { Background = change(entry.Background).Validated() } : entry).ToList(),
            };
        ShowBackground(controls: false);
        QueueApplyRefresh();
    }

    private void ChooseBackground()
    {
        string pictures = string.Join(";", BackdropChoice.Pictures.Concat(BackdropChoice.Animations).Select(kind => "*" + kind));
        string videos = string.Join(";", BackdropChoice.Videos.Select(kind => "*" + kind));
        using var dialog = new OpenFileDialog
        {
            Filter = $"{T("settings.backgrounds.files")}|{pictures};{videos}|{T("settings.backgrounds.pictures")}|{pictures}|{T("settings.backgrounds.videos")}|{videos}",
        };
        // Where this one is, or else where any other is: backgrounds tend to be kept together.
        string? folder = new[] { ChoiceOf(ShownApp) }.Append(_backgroundEdit.Everywhere).Concat(_backgroundEdit.Apps.Select(app => app.Background))
            .SelectMany(choice => choice.Items).Select(item => item.File).Where(file => file.Length > 0 && !BackdropChoice.IsLinkText(file))
            .Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists);
        if (folder is not null) dialog.InitialDirectory = folder;
        dialog.Multiselect = true;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (string file in dialog.FileNames) AddItem(file);
    }

    // A new app gets its own background, Glass and Dark levels at once, all as every app has them, so nothing changes until
    // they are changed.
    private void AddBackgroundApp()
    {
        string name = BackgroundPreferences.ProgramKey(_newBackgroundApp.Text);
        _newBackgroundApp.Text = "";
        if (name.Length == 0) return;
        if (!ListedApps().Any(listed => SameApp(listed, name)))
        {
            _backgroundEdit = _backgroundEdit with { Apps = [.. _backgroundEdit.Apps, new AppBackground { Program = name, Background = _backgroundEdit.Everywhere }] };
            _glassApps.Add(MainGlass(name) with { OwnDark = true });
            _listedApps.Add(name);
        }
        ListBackgrounds(name);
        QueueApplyRefresh();
    }

    private void RemoveBackgroundApp()
    {
        if (ShownApp is not { } app) return;
        int at = _backgroundList.SelectedIndex;
        bool hadKeyboard = _backgroundRemoveApp.Focused;
        _backgroundEdit = _backgroundEdit with { Apps = _backgroundEdit.Apps.Where(entry => !SameApp(entry.Program, app)).ToList() };
        _glassApps.RemoveAll(entry => SameApp(entry.Program, app));
        _listedApps.RemoveAll(entry => SameApp(entry, app));
        List<string> apps = ListedApps();
        string? next = at - 2 >= 0 && at - 2 < apps.Count ? apps[at - 2] : null;
        ListBackgrounds(next);
        if (hadKeyboard && !_backgroundRemoveApp.Enabled) _backgroundList.Focus(); // Back at Every app, which stays.
        QueueApplyRefresh();
    }
}
