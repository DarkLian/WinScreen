using static WinScreen.Localization.UiText;
using WinScreen.Glass;

namespace WinScreen.Settings;

// The list holds Every app and the apps with their own background or Glass levels; the card under it edits the one
// chosen in the list. Every app's card holds the Glass levels every other app starts from.
public sealed partial class SettingsWindow
{
    private const int MostBackgroundRows = 5;
    private const int MostKeptRows = 4;
    private readonly SwitchBox _ownBackground = new() { Text = T("settings.apps.ownBackground"), AutoSize = true };
    private readonly SwitchBox _ownGlass = new() { Text = T("settings.apps.ownGlass"), AutoSize = true };
    private readonly List<Control> _backgroundRows = [];
    // Added here and not yet given anything of their own: they stay in the list until it is filled again.
    private readonly List<string> _listedApps = [];
    private readonly NumericUpDown _appGlassBackground = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appGlassText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _appGlassFade = new CurvedNumber { Minimum = 1, Maximum = EffectPreferences.MostFadeRange, Width = 70, Curve = CurvedNumber.FadeCurve };
    private readonly ComboBox _appGlassTextColor = TextColorPicker();
    private readonly SwitchBox _appGlassShadow = new() { Text = T("settings.effects.shadow"), AutoSize = true };
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
    private readonly PlainButton _backgroundChoose = new() { Text = T("settings.backgrounds.choose"), AutoSize = true };
    private readonly PlainButton _backgroundClear = new() { Text = T("settings.backgrounds.clear"), AutoSize = true };
    private readonly PlainButton _backgroundRemoveApp = new() { Text = T("common.remove"), Enabled = false };
    private readonly ComboBox _backgroundFit = new ThemedCombo { Width = 200 };
    private readonly NumericUpDown _backgroundVolume = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private SettingsCard? _backgroundEditor;
    private SettingRowPanel? _backgroundFileRow;
    private InputFrame? _backgroundFrame;
    private BackgroundPreferences _backgroundEdit = new();
    private bool _showingBackground;

    private FlowLayoutPanel BuildBackgrounds()
    {
        _backgroundFit.Items.AddRange([T("settings.backgrounds.fill"), T("settings.backgrounds.fitWhole"),
            T("settings.backgrounds.stretch"), T("settings.backgrounds.tile"), T("settings.backgrounds.center")]);
        _backgroundList.SelectedIndexChanged += (_, _) => ShowBackground(controls: true);
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
        _backgroundPreview.ChooseAsked += ChooseBackground;
        _backgroundPreview.Dropped += file => ChangeBackground(choice => choice with { File = file });
        _backgroundClear.Click += (_, _) => ChangeBackground(choice => choice with { File = "" });
        _backgroundFit.SelectedIndexChanged += (_, _) =>
            ChangeBackground(choice => choice with { Fit = (BackdropFit)Math.Max(0, _backgroundFit.SelectedIndex) });
        _backgroundVolume.TextChanged += (_, _) => ChangeBackground(choice => choice with { Volume = Typed(_backgroundVolume) });
        _backgroundRemoveApp.Click += (_, _) => RemoveBackgroundApp();
        _backgroundChoose.Margin = new Padding(0, 0, 8, 0);
        _backgroundClear.Margin = Padding.Empty;
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, BackColor = SettingsStyle.Card };
        buttons.Controls.Add(_backgroundChoose);
        buttons.Controls.Add(_backgroundClear);
        _backgroundFileRow = (SettingRowPanel)SettingRow(T("settings.backgrounds.file"), buttons);
        _backgroundRows.AddRange([_backgroundPreview, _backgroundFileRow, SettingRow(T("settings.backgrounds.fit"), _backgroundFit),
            SettingRow(T("settings.backgrounds.sound"), _backgroundVolume)]);
        _ownBackground.CheckedChanged += (_, _) => SetOwnBackground(_ownBackground.Checked);
        _ownGlass.CheckedChanged += (_, _) => SetOwnGlass(_ownGlass.Checked);

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

        foreach (NumericUpDown box in new[] { _appGlassBackground, _appGlassText, _appGlassFade })
            box.TextChanged += (_, _) => ChangeGlassApp();
        _appGlassTextColor.SelectedIndexChanged += (_, _) => ChangeGlassApp();
        _appGlassShadow.CheckedChanged += (_, _) => ChangeGlassApp();
        _appGlassRows.Add(SettingRow(T("settings.effects.background"), _appGlassBackground));
        _appGlassRows.Add(SettingRow(T("settings.effects.text"), _appGlassText));
        _appGlassRows.Add(SettingRow(T("settings.effects.fade"), _appGlassFade));
        _appGlassRows.Add(SettingRow(T("settings.effects.textColor"), _appGlassTextColor));
        _appGlassRows.Add(_appGlassShadow);
        _mainGlassRows.AddRange([_layeredGlass,
            SettingRow(T("settings.effects.background"), _glassBackground), SettingRow(T("settings.effects.text"), _glassText),
            SettingRow(T("settings.effects.fade"), _glassFade), Note(T("settings.effects.fadeHelp")),
            SettingRow(T("settings.effects.textColor"), _glassTextColor), Note(T("settings.effects.textColorHelp")), _glassShadow,
            Note(T("settings.effects.help"))]);
        _appGlassNote = Note(T("settings.apps.glassHelp"));
        _backgroundEditor = Card(T("settings.backgrounds.everyApp"), false,
            [_ownBackground, .. _backgroundRows, Note(T("settings.backgrounds.soundHelp")), .. _mainGlassRows, _ownGlass, _appGlassNote, .. _appGlassRows]);

        FlowLayoutPanel page = ScrollingPage();
        page.Controls.Add(Card(T("settings.backgrounds.title"), true, Note(T("settings.backgrounds.help")), _backgroundFrame, adding));
        page.Controls.Add(_backgroundEditor);
        page.Controls.Add(Card(T("settings.keptAreas.title"), true, Note(T("settings.keptAreas.help")), _keptFrame, keptButtons));
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
        TextColor = TextColorOf(_glassTextColor), TextShadow = _glassShadow.Checked,
    };

    private static ComboBox TextColorPicker()
    {
        var picker = new ThemedCombo { Width = 200 };
        picker.Items.AddRange([T("settings.effects.textColor.asItIs"), T("settings.effects.textColor.light"), T("settings.effects.textColor.dark")]);
        picker.SelectedIndex = 0;
        return picker;
    }

    private static Glass.GlassText TextColorOf(ComboBox picker) => (Glass.GlassText)Math.Max(0, picker.SelectedIndex);

    private void ShowGlassApp(string? program)
    {
        AppGlassLevels? own = GlassOf(program);
        AppGlassLevels shown = own ?? MainGlass(program ?? "");
        _showingGlassApp = true;
        try
        {
            _ownGlass.Checked = own is not null;
            _appGlassBackground.Value = shown.BackgroundPercent;
            _appGlassText.Value = shown.TextPercent;
            _appGlassFade.Value = shown.FadeRangePercent;
            _appGlassTextColor.SelectedIndex = (int)shown.TextColor;
            _appGlassShadow.Checked = shown.TextShadow;
        }
        finally { _showingGlassApp = false; }
        foreach (Control row in _appGlassRows) row.Enabled = own is not null;
    }

    private void ChangeGlassApp()
    {
        if (_showingGlassApp || _loading || GlassOf(ShownApp) is not { } app) return;
        int at = _glassApps.IndexOf(app);
        _glassApps[at] = app with
        {
            BackgroundPercent = Typed(_appGlassBackground), TextPercent = Typed(_appGlassText), FadeRangePercent = Typed(_appGlassFade),
            TextColor = TextColorOf(_appGlassTextColor), TextShadow = _appGlassShadow.Checked,
        };
        QueueApplyRefresh();
    }

    private void SetOwnGlass(bool on)
    {
        if (_showingGlassApp || _showingBackground || _loading || ShownApp is not { } program) return;
        AppGlassLevels? own = GlassOf(program);
        if (on == own is not null) return;
        if (on) _glassApps.Add(MainGlass(program));
        else _glassApps.Remove(own!);
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

    private static bool IsVideo(string file) => BackdropChoice.Videos.Contains(Path.GetExtension(file).ToLowerInvariant());

    private string DetailOf(string? program)
    {
        if (program is not null && _backgroundEdit.OwnOf(program) is null) return T("settings.apps.everyBackground");
        BackgroundChoice choice = ChoiceOf(program);
        if (choice.File.Length == 0) return program is null ? T("settings.backgrounds.none") : T("settings.backgrounds.appNone");
        string name = Path.GetFileName(choice.File);
        return choice.Volume > 0 && IsVideo(choice.File) ? string.Format(T("settings.backgrounds.withSound"), name, choice.Volume) : name;
    }

    private static int Typed(NumericUpDown box) =>
        decimal.TryParse(box.Text, out decimal typed) ? (int)Math.Clamp(typed, box.Minimum, box.Maximum) : (int)box.Value;

    private void FillBackgrounds(BackgroundPreferences values)
    {
        _backgroundEdit = values.Validated();
        ListBackgrounds(ShownApp);
    }

    private void ListBackgrounds(string? select)
    {
        BackgroundEntry Entry(string? program) =>
            new(program, program ?? T("settings.backgrounds.everyApp"), () => ChoiceOf(program), () => DetailOf(program));
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
                _backgroundEditor.Text = app ?? T("settings.backgrounds.everyApp");
                _backgroundFit.SelectedIndex = (int)choice.Fit;
                _backgroundVolume.Value = choice.Volume;
            }
            finally { _showingBackground = false; }
            _backgroundRemoveApp.Enabled = app is not null; // Every app stays.
            // Every app's own Glass levels are the ones every app starts from.
            _ownBackground.Visible = _ownGlass.Visible = app is not null;
            _appGlassNote?.Visible = app is not null;
            foreach (Control row in _appGlassRows) row.Visible = app is not null;
            foreach (Control row in _mainGlassRows) row.Visible = app is null;
            ShowGlassApp(app);
        }
        bool own = app is null || _backgroundEdit.OwnOf(app) is not null;
        _showingBackground = true;
        try { _ownBackground.Checked = own; }
        finally { _showingBackground = false; }
        foreach (Control row in _backgroundRows) row.Enabled = own;
        bool chosen = choice.File.Length > 0;
        _backgroundClear.Enabled = chosen;
        _backgroundVolume.Enabled = chosen && IsVideo(choice.File);
        _backgroundFileRow.Description = DetailOf(app);
        _backgroundPreview.Show(choice.File, choice.Fit, T("settings.backgrounds.drop"));
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
            .Select(choice => choice.File).Where(file => file.Length > 0).Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists);
        if (folder is not null) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ChangeBackground(choice => choice with { File = dialog.FileName });
    }

    // A new app gets its own background and Glass levels at once, both as every app has them, so nothing changes until
    // they are changed.
    private void AddBackgroundApp()
    {
        string name = BackgroundPreferences.ProgramKey(_newBackgroundApp.Text);
        _newBackgroundApp.Text = "";
        if (name.Length == 0) return;
        if (!ListedApps().Any(listed => SameApp(listed, name)))
        {
            _backgroundEdit = _backgroundEdit with { Apps = [.. _backgroundEdit.Apps, new AppBackground { Program = name, Background = _backgroundEdit.Everywhere }] };
            _glassApps.Add(MainGlass(name));
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
