using static WinScreen.Localization.UiText;
using System.Diagnostics;
using WinScreen.Tray;
using WinScreen.Updates;

namespace WinScreen.Settings;

/// <summary>The settings window. Apply puts the changes into effect and keeps it open; Save does that and closes it.</summary>
public sealed class SettingsWindow : Form
{
    private const int ContentWidth = 490;

    private readonly AppSettings _settings;
    private AppSettings? _pendingImport;
    private readonly NumericUpDown _opacityStep = new() { Minimum = 1, Maximum = 25, Width = 70 };
    private readonly CheckBox _opacityWheel = new() { Text = T("settings.opacity.wheel"), AutoSize = true };
    private readonly CheckBox _layeredGlass = new() { Text = T("settings.effects.layeredGlass"), AutoSize = true };
    private readonly CheckBox _pictureSafeDark = new() { Text = T("settings.effects.pictureSafeDark"), AutoSize = true };
    private readonly NumericUpDown _glassBackground = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassText = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _glassPictures = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly CheckBox _detectDark = new() { Text = T("settings.dark.detect"), AutoSize = true };
    private readonly TextBox _borderColor = new() { Width = 90 };
    private readonly NumericUpDown _borderOpacity = new() { Minimum = 10, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _borderThickness = new() { Minimum = 1, Maximum = 12, Width = 70 };
    private readonly CheckBox _autoAnchors = new() { Text = T("anchors.auto.enable"), AutoSize = true };
    private readonly ComboBox _anchorSide = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _anchorLook = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _anchorLimit = new() { Minimum = 1, Maximum = 10, Width = 70 };
    private readonly NumericUpDown _anchorSize = new() { Minimum = 32, Maximum = 72, Increment = 4, Width = 70 };
    private readonly CheckBox _anchorRecency = new() { Text = T("anchors.recency"), AutoSize = true };
    private readonly CheckBox _anchorAvoid = new() { Text = T("anchors.avoid"), AutoSize = true };
    private readonly ListBox _anchorOnly = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _anchorExcluded = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _newAnchorOnly = ProgramPicker();
    private readonly ComboBox _newAnchorExcluded = ProgramPicker();
    private readonly ComboBox _toolbarPlacement = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _toolbarTrigger = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _toolbarDelay = new() { Minimum = 100, Maximum = 2000, Increment = 100, Width = 90 };
    private readonly CheckedListBox _toolbarButtons = new() { CheckOnClick = true, Width = 455, Height = 225, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Size = new Size(540, 520), Multiline = true };
    private readonly TextBox _actionSearch = new() { Dock = DockStyle.Top };
    private readonly Panel _shortcutEditor = new() { Dock = DockStyle.Fill, Height = 28 };
    private readonly Label _shortcutLabel = new() { AutoSize = true };
    private readonly Dictionary<AppCommand, string> _shortcutProblems = [];
    private readonly Func<AppCommand, bool>? _registered;
    private readonly ListView _features = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };

    private sealed record ToolbarChoice(AppCommand Command)
    {
        public override string ToString() => CommandCatalog.Name(Command).Replace("&", "");
    }
    private readonly ComboBox _pipBackend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ComboBox _pipRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _pipScaling = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NumericUpDown _pipOpacity = new() { Minimum = 10, Maximum = 100, Width = 75 };
    private readonly CheckBox _pipTaskbar = new() { Text = T("pip.taskbar.enable"), AutoSize = true };
    private readonly ComboBox _pipPreview = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly CheckBox _pipCaptureBorder = new() { Text = T("pip.captureBorder.hide"), AutoSize = true };
    private readonly CheckBox _pipDiagnostics = new() { Text = T("pip.diagnostics.includeHardware"), AutoSize = true };
    private readonly List<int> _pipRates = [.. PipPreferences.FrameRates];

    private readonly CheckBox _showNotifications = new()
        { Text = T("settings.notifications.enable"), AutoSize = true };

    private readonly CheckBox _automaticUpdates = new()
        { Text = T("updates.enable"), AutoSize = true };

    private readonly CheckBox _experimental = new()
        { Text = T("settings.experimental.enable"), AutoSize = true };

private readonly ErrorProvider _errors = new();
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private static readonly string[] Languages = ["system", "en", "zh-Hans", "zh-Hant"];
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private static readonly string[] Themes = ["system", "light", "dark"];
    private readonly StartWithWindows _startup;
    private readonly Func<AppSettings, IReadOnlyList<HotkeyProblem>> _apply;
    private readonly Dictionary<AppCommand, TextBox> _hotkeys = [];
    private readonly ListBox _excluded = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _included = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    private readonly ListBox _setups = new()
        { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };

    private readonly ComboBox _newExcluded = ProgramPicker();
    private readonly ComboBox _newIncluded = ProgramPicker();

    private readonly NumericUpDown _defaultOpacity = new()
    {
        Minimum = AppSettings.LowestOpacityPercent,
        Maximum = AppSettings.HighestOpacityPercent,
        Width = 70,
    };

    private readonly CheckBox _startWithWindows = new() { Text = T("settings.startWithWindows"), AutoSize = true };

    private readonly CheckBox _showToolbar = new()
        { Text = T("settings.toolbar.enable"), AutoSize = true };

    private readonly CheckBox _pauseInFullScreen =
        new() { Text = T("settings.fullScreen.pause"), AutoSize = true };

    private readonly CheckBox _allowPip =
        new() { Text = T("settings.fullScreen.allowPip"), AutoSize = true };

    private readonly CheckBox _modifierDrag = new()
        { Text = T("gestures.moveResize.enable"), AutoSize = true };

    private readonly CheckBox _snapWhileDragging = new() { Text = T("gestures.moveResize.snap"), AutoSize = true };
    private readonly ComboBox _modifierKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };

    private readonly ListBox _modifierExclusions = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _newModifierExclusion = ProgramPicker();
    private readonly CheckBox _windowMenu = new() { Text = T("gestures.menu.enable"), AutoSize = true };
    private readonly ComboBox _windowMenuGesture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ListBox _windowMenuExclusions = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _newMenuExclusion = ProgramPicker();

    private readonly CheckBox _pausePin = new()
        { Text = T("settings.pin.pauseMaximized"), AutoSize = true };

    private readonly CheckBox _opaqueOnHover = new()
        { Text = T("settings.opacity.solidOnHover"), AutoSize = true };

    private readonly CheckBox _highlightPinned = new()
        { Text = T("settings.pin.highlight"), AutoSize = true };

    private readonly CheckBox _pipAutoMove = new()
        { Text = T("settings.pip.autoMove"), AutoSize = true };

    private readonly HashSet<ProgramSetup> _removedSetups = [];
    private readonly Button _applyButton = new() { Text = T("settings.apply"), AutoSize = true, Enabled = false };
    private string _savedState = "";
    private bool _refreshQueued;

    private readonly Label _status = new()
        { AutoSize = true, ForeColor = SystemColors.WindowText, MaximumSize = new Size(500, 0) };

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
            _pipAutoMove.Enabled = _experimental.Checked;
            if (_hotkeys.Count > 0) RefreshFeatures();
        };
        _language.Items.AddRange([T("settings.language.system"), "English", "简体中文", "繁體中文"]);
        _theme.Items.AddRange([T("settings.theme.system"), T("settings.theme.light"), T("settings.theme.dark")]);

        Text = T("settings.window.title");
        Icon = TrayApplication.LoadIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 610);
        MinimumSize = new Size(580, 360);
        Padding = new Padding(12);

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
        tabs.Selected += (_, _) => { if (tabs.SelectedTab == featuresPage) RefreshFeatures(); };
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

        var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(tabs, 0, 0);
        page.Controls.Add(_status, 0, 1);
        page.Controls.Add(BuildButtons(), 0, 2);
        Controls.Add(page);

        LoadValues();
        _pipAutoMove.Enabled = _experimental.Checked;
        WatchForChanges(this);
        foreach (TextBox box in _hotkeys.Values) box.TextChanged += (_, _) => QueueApplyRefresh();
        MarkSaved();
        ResumeLayout(true);
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
    }

    /// <summary>Rechecks whether there's anything to apply. Changing something and changing it back leaves nothing.</summary>
    internal void RefreshApply()
    {
        _refreshQueued = false;
        if (!IsDisposed) _applyButton.Enabled = _pendingImport is not null || CurrentState() != _savedState;
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

    private static ComboBox ProgramPicker()
    {
        var picker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        picker.HandleCreated += (_, _) =>
            Native.User32.SendTextMessage(picker.Handle, Native.User32.CB_SETCUEBANNER, 0,
                T("programs.nameHint"));
        picker.DropDown += (_, _) =>
        {
            picker.Items.Clear();
            picker.Items.AddRange(RunningPrograms().Cast<object>().ToArray());
        };
        return picker;
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
                    if (process.MainWindowHandle != 0 && process.Id != Environment.ProcessId)
                        names.Add(process.ProcessName);
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
        var tab = new TabPage(title) { Padding = new Padding(8) };
        tab.Controls.Add(content);
        return tab;
    }

    private static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth - 30, 0), Margin = new Padding(22, 0, 3, 3),
    };

    /// <summary>A titled group of options.</summary>
    private static GroupBox Section(string title, params Control[] items)
    {
        var options = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        };
        foreach (Control item in items)
        {
            if (item is CheckBox option)
            {
                option.MaximumSize = new Size(ContentWidth - 30, 0);
                option.Margin = new Padding(3, 1, 3, 1);
            }

            options.Controls.Add(item);
        }

        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(ContentWidth - 4, 0), Margin = new Padding(0, 0, 0, 6),
        };
        group.Controls.Add(options);
        return group;
    }

    private FlowLayoutPanel BuildGeneral()
    {
        var language = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        language.Controls.Add(new Label
            { Text = T("settings.language"), AutoSize = true, Margin = new Padding(3, 6, 8, 0) });
        _language.Width = 150;
        language.Controls.Add(_language);

        var theme = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        theme.Controls.Add(new Label { Text = T("settings.theme"), AutoSize = true, Margin = new Padding(3, 6, 8, 0) });
        theme.Controls.Add(_theme);

        var startup = new List<Control> { language, theme, _startWithWindows, _showNotifications, _automaticUpdates };
        bool updatesWork = VelopackUpdateSession.IsConfigured && VelopackUpdateSession.IsInstalled;
        _automaticUpdates.Enabled = updatesWork;
        if (!VelopackUpdateSession.IsConfigured)
            startup.Add(Note(T("updates.repositoryRequired")));
        else if (!VelopackUpdateSession.IsInstalled)
            startup.Add(Note(T("updates.installedOnly")));

        var page = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("settings.startupAndUpdates"), startup.ToArray()));
        page.Controls.Add(Section(T("settings.windows"), _pauseInFullScreen, _allowPip));
        page.Controls.Add(Section(T("settings.experimental"), _experimental));
        page.Controls.Add(BuildTransfer());
        page.Controls.Add(new Label
        {
            Text = string.Format(T("app.version"), AppInfo.Version), AutoSize = true,
            ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 0),
        });
        return page;
    }

    private FlowLayoutPanel BuildModifierDrag()
    {
        _modifierKey.Items.AddRange(["Alt", "Win"]);
        var key = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        key.Controls.Add(new Label { Text = T("gestures.moveResize.modifier"), Width = 145, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
        key.Controls.Add(_modifierKey);
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("gestures.moveResize.title"), _modifierDrag, key, _snapWhileDragging,
            ProgramRules(T("gestures.exclusions"), _modifierExclusions, _newModifierExclusion)));
        page.Controls.Add(Note(T("gestures.moveResize.help")));
        _windowMenuGesture.Items.AddRange([T("gestures.menu.ctrlRight"), T("gestures.menu.ctrlShiftRight"), T("gestures.menu.ctrlMiddle")]);
        page.Controls.Add(Section(T("window.menu"), _windowMenu, _windowMenuGesture,
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
        static Control Row(string label, Control control)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(new Label { Text = label, Width = 145, AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            row.Controls.Add(control);
            return row;
        }
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("pip.open"), Row(T("pip.rendering"), _pipBackend), Row(T("pip.frameRate"), _pipRate),
            Row(T("pip.scaling"), _pipScaling), Row(T("pip.opacityPercent"), _pipOpacity), _pipCaptureBorder, _pipDiagnostics));
        page.Controls.Add(Note(T("pip.rendering.compatibilityHelp")));
        page.Controls.Add(Note(T("pip.rendering.captureIndicatorHelp")));
        page.Controls.Add(Note(T("pip.diagnostics.retryHelp")));
        page.Controls.Add(Section(T("settings.experimental"), _pipAutoMove, Note(T("settings.pip.autoMoveHelp"))));
        page.Controls.Add(Section(T("pip.hover.title"), _pipTaskbar, Row(T("pip.hover.mode"), _pipPreview), Note(T("pip.hover.windowsOnly"))));
        return page;
    }

    private TableLayoutPanel BuildPrograms()
    {
        var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        page.Controls.Add(Note(T("programs.precedence")), 0, 0);
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.Controls.Add(BuildProgramList(T("programs.exclusions"), _excluded, _newExcluded), 0, 1);
        page.Controls.Add(
            BuildProgramList(T("programs.inclusions"), _included, _newIncluded),
            0, 2);
        return page;
    }

    private static GroupBox BuildProgramList(string title, ListBox list, ComboBox newProgram)
    {
        var add = new Button { Text = T("common.add"), AutoSize = true };
        add.Click += (_, _) => AddTyped(list, newProgram);
        newProgram.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            AddTyped(list, newProgram);
        };
        var remove = new Button { Text = T("common.remove"), AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is { } selected)
                list.Items.Remove(selected);
        };

        var table = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(list, 0, 0);
        table.Controls.Add(remove, 1, 0);
        table.Controls.Add(newProgram, 0, 1);
        table.Controls.Add(add, 1, 1);

        var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
        group.Controls.Add(table);
        return group;
    }

    private GroupBox BuildSetups()
    {
        var remove = new Button { Text = T("common.remove"), AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (_setups.SelectedItem is ProgramSetup selected)
                RemoveSetup(selected);
        };

        var hint = new Label
        {
            Text = T(
                "settings.setups.help"),
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Margin = new Padding(0, 0, 0, 8),
        };

        var table = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(hint, 0, 0);
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(_setups, 0, 1);
        table.Controls.Add(remove, 1, 1);

        var group = new GroupBox { Text = T("settings.savedSetups"), Dock = DockStyle.Fill };
        group.Controls.Add(table);
        return group;
    }

    private FlowLayoutPanel BuildButtons()
    {
        var save = new Button { Text = T("settings.save"), AutoSize = true };
        save.Click += (_, _) =>
        {
            if (SaveChanges())
                Close();
        };
        var cancel = new Button { Text = T("common.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;
        _applyButton.Click += (_, _) => ApplyChanges();

        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.Add(save);
        row.Controls.Add(cancel);
        row.Controls.Add(_applyButton);
        return row;
    }

    private Control BuildTransfer()
    {
        var export = new Button { Text = T("settings.transfer.export"), AutoSize = true };
        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", DefaultExt = "json", FileName = "WinScreen-settings.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { SettingsTransfer.Export(_settings, dialog.FileName); _status.Text = T("settings.transfer.exported"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        };
        var import = new Button { Text = T("settings.transfer.import"), AutoSize = true };
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
        return Section(T("settings.transfer.title"), Note(T("settings.transfer.paths")), export, import);
    }

    private FlowLayoutPanel BuildCustomization()
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("settings.opacity.title"), SettingRow(T("settings.opacity.default"), _defaultOpacity),
            SettingRow(T("settings.opacity.step"), _opacityStep), _opacityWheel, _opaqueOnHover));
        page.Controls.Add(Section(T("settings.pin.border"), _pausePin, _highlightPinned, SettingRow(T("settings.pin.color"), _borderColor),
            SettingRow(T("settings.pin.opacity"), _borderOpacity), SettingRow(T("settings.pin.thickness"), _borderThickness)));
        page.Controls.Add(_detectDark);
        page.Controls.Add(Section(T("settings.effects.title"), _layeredGlass,
            SettingRow(T("settings.effects.background"), _glassBackground), SettingRow(T("settings.effects.text"), _glassText),
            SettingRow(T("settings.effects.pictures"), _glassPictures), _pictureSafeDark, Note(T("settings.effects.help"))));
        return page;
    }

    private static Control SettingRow(string text, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 8, 0) });
        row.Controls.Add(control);
        return row;
    }

    private Control BuildToolbar()
    {
        _toolbarPlacement.Items.AddRange([T("toolbar.placement.left"), T("toolbar.placement.centre"), T("toolbar.placement.right")]);
        _toolbarTrigger.Items.AddRange([T("toolbar.trigger.titleBar"), T("toolbar.trigger.minimize")]);
        foreach (AppCommand command in ToolbarPreferences.DefaultButtons()) _toolbarButtons.Items.Add(new ToolbarChoice(command));
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("toolbar.settings.title"), _showToolbar, SettingRow(T("toolbar.placement.label"), _toolbarPlacement),
            SettingRow(T("toolbar.trigger.label"), _toolbarTrigger), SettingRow(T("toolbar.delay.label"), _toolbarDelay),
            Note(T("toolbar.trigger.help"))));
        page.Controls.Add(Section(T("toolbar.buttons.label"), _toolbarButtons, Note(T("toolbar.buttons.help"))));
        return page;
    }

    private Control BuildAnchors()
    {
        _anchorSide.Items.AddRange([T("anchors.side.right"), T("anchors.side.left"), T("anchors.side.near")]);
        _anchorLook.Items.AddRange([T("anchors.look.dark"), T("anchors.look.light")]);
        var page = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        GroupBox options = Section(T("anchors.settings.title"), _autoAnchors, Note(T("anchors.auto.help")),
            SettingRow(T("anchors.side.label"), _anchorSide), SettingRow(T("anchors.look.label"), _anchorLook),
            SettingRow(T("anchors.limit"), _anchorLimit), SettingRow(T("anchors.size"), _anchorSize),
            _anchorRecency, _anchorAvoid, Note(T("anchors.avoid.help")), Note(T("programs.alwaysLeftAlone")));
        page.Controls.Add(options, 0, 0);
        page.SetColumnSpan(options, 2);
        page.Controls.Add(BuildProgramList(T("anchors.auto.only"), _anchorOnly, _newAnchorOnly), 0, 1);
        page.Controls.Add(BuildProgramList(T("anchors.auto.excluded"), _anchorExcluded, _newAnchorExcluded), 1, 1);
        return page;
    }

    private static Control ProgramRules(string title, ListBox list, ComboBox picker)
    {
        GroupBox group = BuildProgramList(title, list, picker);
        group.Dock = DockStyle.None;
        group.Size = new Size(ContentWidth - 30, 140);
        return group;
    }

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
            var box = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, ShortcutsEnabled = false,
                AccessibleName = CommandCatalog.Name(command) };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Tab) return;
                e.SuppressKeyPress = true;
                CaptureHotkey(command, e.KeyData, HotkeyText.WinKeyDown);
            };
            _hotkeys[command] = box;
        }
        var clear = new Button { Text = T("actions.clear"), AutoSize = true };
        var reset = new Button { Text = T("actions.reset"), AutoSize = true };
        void SetSelected(bool defaults)
        {
            if (_features.SelectedItems.Count == 0 || _features.SelectedItems[0].Tag is not AppCommand command) return;
            _hotkeys[command].Text = defaults ? new AppSettings().Hotkeys.GetValueOrDefault(command, "") : "";
            _hotkeys[command].Focus();
        }
        clear.Click += (_, _) => SetSelected(false);
        reset.Click += (_, _) => SetSelected(true);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.AddRange([clear, reset]);
        var editor = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1 };
        editor.Controls.Add(_shortcutLabel);
        editor.Controls.Add(_shortcutEditor);
        editor.Controls.Add(buttons);
        editor.Controls.Add(Note(T("settings.hotkeys.captureHelp")));
        _features.SelectedIndexChanged += (_, _) => ShowShortcutEditor();
        _actionSearch.AccessibleName = T("actions.search");
        _actionSearch.PlaceholderText = T("actions.search");
        _actionSearch.TextChanged += (_, _) => RefreshFeatures();
        var page = new Panel { Dock = DockStyle.Fill };
        page.Controls.Add(_features);
        page.Controls.Add(editor);
        var help = new Label { Text = T("features.help"), AutoSize = true, Dock = DockStyle.Top };
        page.Controls.Add(help);
        page.Controls.Add(_actionSearch);
        page.Resize += (_, _) => help.MaximumSize = new Size(page.ClientSize.Width, 0);
        _features.ShowItemToolTips = true;
        _features.Resize += (_, _) =>
        {
            int width = Math.Max(200, _features.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            double[] shares = [0.28, 0.15, 0.18, 0.18, 0.21];
            for (int i = 0; i < shares.Length; i++) _features.Columns[i].Width = (int)(width * shares[i]);
        };
        return page;
    }

    private void ShowShortcutEditor()
    {
        _shortcutEditor.Controls.Clear();
        _shortcutLabel.Text = "";
        if (_features.SelectedItems.Count == 0 || _features.SelectedItems[0].Tag is not AppCommand command) return;
        _shortcutLabel.Text = CommandCatalog.Name(command);
        _shortcutEditor.Controls.Add(_hotkeys[command]);
    }

    internal void RefreshFeatures()
    {
        object? selected = _features.SelectedItems.Count > 0 ? _features.SelectedItems[0].Tag : null;
        _features.BeginUpdate();
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
            var row = new ListViewItem(values) { Tag = page };
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
        if (_features.SelectedItems.Count == 0 && _features.Items.Count > 0) _features.Items[0].Selected = true;
        ShowShortcutEditor();
    }
}
