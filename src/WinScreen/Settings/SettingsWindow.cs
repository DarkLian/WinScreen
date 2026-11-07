using static WinScreen.Localization.UiText;
using System.Diagnostics;
using WinScreen.Tray;
using WinScreen.Updates;

namespace WinScreen.Settings;

/// <summary>The settings window. Saving applies the changes straight away.</summary>
public sealed class SettingsWindow : Form
{
    private const int ContentWidth = 490;

    private readonly AppSettings _settings;
    private readonly ComboBox _pipBackend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ComboBox _pipRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _pipScaling = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NumericUpDown _pipOpacity = new() { Minimum = 10, Maximum = 100, Width = 75 };
    private readonly CheckBox _pipDiagnostics = new() { Text = T("pip.diagnostics.includeHardware"), AutoSize = true };
    private readonly List<int> _pipRates = [.. PipPreferences.FrameRates];

    private readonly CheckBox _showNotifications = new()
        { Text = T("settings.notifications.enable"), AutoSize = true };

    private readonly CheckBox _automaticUpdates = new()
        { Text = T("updates.enable"), AutoSize = true };

    private readonly CheckBox _experimental = new()
        { Text = T("settings.experimental.enable"), AutoSize = true };

    private readonly CheckBox _pipInput = new()
        { Text = T("settings.pip.forwardClicks"), AutoSize = true };

    private readonly ErrorProvider _errors = new();
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private static readonly string[] Languages = ["system", "en", "zh-Hans", "zh-Hant"];
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

    private readonly ComboBox _modifierKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };

    private readonly TextBox _modifierExclusions = new() { Width = 455 };
    private readonly CheckBox _windowMenu = new() { Text = T("gestures.menu.enable"), AutoSize = true };
    private readonly ComboBox _windowMenuGesture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly TextBox _windowMenuExclusions = new() { Width = 455 };

    private readonly CheckBox _pausePin = new()
        { Text = T("settings.pin.pauseMaximized"), AutoSize = true };

    private readonly CheckBox _opaqueOnHover = new()
        { Text = T("settings.opacity.solidOnHover"), AutoSize = true };

    private readonly CheckBox _highlightPinned = new()
        { Text = T("settings.pin.highlight"), AutoSize = true };

    private readonly CheckBox _pipAutoMove = new()
        { Text = T("settings.pip.autoMove"), AutoSize = true };

    private readonly HashSet<ProgramSetup> _removedSetups = [];

    private readonly Label _status = new()
        { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(500, 0) };

    public SettingsWindow(AppSettings settings, StartWithWindows startup,
        Func<AppSettings, IReadOnlyList<HotkeyProblem>> apply)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F); // Every size below is written for 100% display scaling.
        AutoScaleMode = AutoScaleMode.Dpi;
        _settings = settings;
        _startup = startup;
        _apply = apply;
        _errors.ContainerControl = this;
        _experimental.CheckedChanged += (_, _) =>
        {
            _pipInput.Enabled = _experimental.Checked;
            _pipAutoMove.Enabled = _experimental.Checked;
        };
        _language.Items.AddRange([T("settings.language.system"), "English", "简体中文", "繁體中文"]);

        Text = T("settings.window.title");
        Icon = TrayApplication.LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var tabs = new TabControl { Size = new Size(540, 520) };
        tabs.TabPages.Add(Tab(T("settings.general"), BuildGeneral()));
        tabs.TabPages.Add(Tab(T("settings.hotkeys"), BuildHotkeys()));
        tabs.TabPages.Add(Tab(T("settings.programs"), BuildPrograms()));
        tabs.TabPages.Add(Tab(T("settings.savedSetups"), BuildSetups()));
        tabs.TabPages.Add(Tab(T("pip.open"), BuildPip()));
        tabs.TabPages.Add(Tab(T("gestures.title"), BuildModifierDrag()));

        var page = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        page.Controls.Add(tabs);
        page.Controls.Add(_status);
        page.Controls.Add(BuildButtons());
        Controls.Add(page);

        LoadValues();
        _pipInput.Enabled = _experimental.Checked;
        _pipAutoMove.Enabled = _experimental.Checked;
        ResumeLayout(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _errors.Dispose();
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
        var draft = _settings.Clone();
        foreach ((AppCommand command, TextBox box) in _hotkeys)
        {
            draft.Hotkeys[command] = box.Text;
            _errors.SetError(box, "");
        }

        // A name typed but not yet added is still what the person meant.
        AddTyped(_excluded, _newExcluded);
        AddTyped(_included, _newIncluded);
        draft.ExcludedPrograms = _excluded.Items.Cast<string>().ToList();
        draft.IncludedPrograms = _included.Items.Cast<string>().ToList();
        draft.SavedSetups.RemoveAll(setup => _removedSetups.Any(removed =>
            removed.Program == setup.Program && removed.ExecutablePath == setup.ExecutablePath));
        draft.ShowToolbar = _showToolbar.Checked;
        draft.ShowNotifications = _showNotifications.Checked;
        draft.AutomaticUpdates = _automaticUpdates.Checked;
        draft.ExperimentalFeatures = _experimental.Checked;
        draft.PipClickForwarding = _pipInput.Checked;
        draft.StartWithWindows = _startWithWindows.Checked;
        draft.Language = Languages[Math.Max(0, _language.SelectedIndex)];
        draft.PauseInFullScreen = _pauseInFullScreen.Checked;
        draft.AllowPipInFullScreen = _allowPip.Checked;
        draft.ModifierDrag = _modifierDrag.Checked;
        draft.ModifierKey = (ModifierKey)Math.Max(0, _modifierKey.SelectedIndex);
        draft.WindowMenu = _windowMenu.Checked;
        draft.WindowMenuGesture = (WindowMenuGesture)Math.Max(0, _windowMenuGesture.SelectedIndex);
        draft.WindowMenuExclusions = _windowMenuExclusions.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        draft.ModifierExclusions = _modifierExclusions.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        draft.PausePinWhenMaximized = _pausePin.Checked;
        draft.OpaqueOnHover = _opaqueOnHover.Checked;
        draft.HighlightPinned = _highlightPinned.Checked;
        draft.PipAutoMove = _pipAutoMove.Checked;
        draft.DefaultOpacityPercent = (int)_defaultOpacity.Value;
        draft.Pip.Backend = (PipBackendPreference)Math.Max(0, _pipBackend.SelectedIndex);
        draft.Pip.FrameRate = _pipRates[Math.Max(0, _pipRate.SelectedIndex)];
        draft.Pip.Scaling = (PipScaling)Math.Max(0, _pipScaling.SelectedIndex);
        draft.Pip.OpacityPercent = (int)_pipOpacity.Value;
        draft.Pip.ShowDiagnostics = _pipDiagnostics.Checked;
        try
        {
            IReadOnlyList<HotkeyProblem> problems = _apply(draft);
            _status.Text = string.Join(Environment.NewLine, problems.Select(problem => problem.Message));
            foreach (HotkeyProblem problem in problems)
                if (_hotkeys.TryGetValue(problem.Command, out TextBox? box))
                    _errors.SetError(box, problem.Message);
            if (problems.Count > 0)
                return false;
            _startup.IsOn = _startWithWindows.Checked;
            _settings.CopyFrom(draft);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException)
        {
            _status.Text = failure.Message;
            return false;
        }
    }

    internal void AddExcluded(string program) => AddProgram(_excluded, program);

    internal void AddIncluded(string program) => AddProgram(_included, program);

    internal void SetDefaultOpacity(int percent) => _defaultOpacity.Value = percent;

    /// <summary>Forgets a program's saved setup, the way the Remove button does.</summary>
    internal void RemoveSetup(ProgramSetup setup)
    {
        _setups.Items.Remove(setup);
        _removedSetups.Add(setup);
    }

    private static void AddProgram(ListBox list, string program)
    {
        string name = program.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length > 0 && !list.Items.Contains(name))
            list.Items.Add(name);
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
            nameof(AppSettings.PipClickForwarding) => _pipInput,
            nameof(AppSettings.PauseInFullScreen) => _pauseInFullScreen,
            nameof(AppSettings.AllowPipInFullScreen) => _allowPip,
            nameof(AppSettings.ModifierDrag) => _modifierDrag,
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
        foreach ((AppCommand command, TextBox box) in _hotkeys)
            box.Text = _settings.Hotkeys.GetValueOrDefault(command, "");
        _excluded.Items.AddRange(_settings.ExcludedPrograms.Cast<object>().ToArray());
        _included.Items.AddRange(_settings.IncludedPrograms.Cast<object>().ToArray());
        _setups.Items.AddRange(_settings.SavedSetups.Cast<object>().ToArray());
        _startWithWindows.Checked = _startup.IsOn;
        _showToolbar.Checked = _settings.ShowToolbar;
        _showNotifications.Checked = _settings.ShowNotifications;
        _automaticUpdates.Checked = _settings.AutomaticUpdates;
        _experimental.Checked = _settings.ExperimentalFeatures;
        _pipInput.Checked = _settings.PipClickForwarding;
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, _settings.Language));
        _pauseInFullScreen.Checked = _settings.PauseInFullScreen;
        _allowPip.Checked = _settings.AllowPipInFullScreen;
        _modifierDrag.Checked = _settings.ModifierDrag;
        _modifierKey.SelectedIndex = (int)_settings.ModifierKey;
        _windowMenu.Checked = _settings.WindowMenu;
        _windowMenuGesture.SelectedIndex = (int)_settings.WindowMenuGesture;
        _windowMenuExclusions.Text = string.Join(", ", _settings.WindowMenuExclusions);
        _modifierExclusions.Text = string.Join(", ", _settings.ModifierExclusions);
        _pausePin.Checked = _settings.PausePinWhenMaximized;
        _opaqueOnHover.Checked = _settings.OpaqueOnHover;
        _highlightPinned.Checked = _settings.HighlightPinned;
        _pipAutoMove.Checked = _settings.PipAutoMove;
        PipPreferences pip = _settings.Pip.Validated();
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
        _defaultOpacity.Value = Math.Clamp(_settings.DefaultOpacityPercent, AppSettings.LowestOpacityPercent,
            AppSettings.HighestOpacityPercent);
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

        var startup = new List<Control> { language, _startWithWindows, _showNotifications, _automaticUpdates };
        bool updatesWork = VelopackUpdateSession.IsConfigured && VelopackUpdateSession.IsInstalled;
        _automaticUpdates.Enabled = updatesWork;
        if (!VelopackUpdateSession.IsConfigured)
            startup.Add(Note(T("updates.repositoryRequired")));
        else if (!VelopackUpdateSession.IsInstalled)
            startup.Add(Note(T("updates.installedOnly")));

        var opacity = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        opacity.Controls.Add(new Label
            { Text = T("settings.opacity.default"), AutoSize = true, Margin = new Padding(3, 6, 4, 0) });
        opacity.Controls.Add(_defaultOpacity);
        opacity.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });

        var page = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(Section(T("settings.startupAndUpdates"), startup.ToArray()));
        page.Controls.Add(Section(T("settings.windows"), _showToolbar, _pauseInFullScreen, _allowPip, _pausePin, _opaqueOnHover,
            _highlightPinned, opacity));
        page.Controls.Add(Section(T("settings.experimental"), _experimental, _pipInput, _pipAutoMove,
            Note(T("settings.pip.autoMoveHelp"))));
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
        page.Controls.Add(Section(T("gestures.moveResize.title"), _modifierDrag, key,
            new Label { Text = T("gestures.exclusions"), AutoSize = true },
            _modifierExclusions));
        page.Controls.Add(Note(T("gestures.moveResize.help")));
        _windowMenuGesture.Items.AddRange([T("gestures.menu.ctrlRight"), T("gestures.menu.ctrlShiftRight"), T("gestures.menu.ctrlMiddle")]);
        page.Controls.Add(Section(T("window.menu"), _windowMenu, _windowMenuGesture,
            new Label { Text = T("gestures.exclusions"), AutoSize = true }, _windowMenuExclusions));
        page.Controls.Add(Note(T("gestures.menu.help")));
        page.Controls.Add(Note(T("gestures.hookHelp")));
        return page;
    }

    private FlowLayoutPanel BuildPip()
    {
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
            Row(T("pip.scaling"), _pipScaling), Row(T("pip.opacityPercent"), _pipOpacity), _pipDiagnostics));
        page.Controls.Add(Note(T("pip.rendering.compatibilityHelp")));
        page.Controls.Add(Note(T("pip.rendering.captureIndicatorHelp")));
        page.Controls.Add(Note(T("pip.diagnostics.retryHelp")));
        return page;
    }

    private GroupBox BuildHotkeys()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
        AppCommand[] commands = CommandCatalog.All.ToArray();
        table.RowCount = commands.Length + 1; // The spare row takes the leftover height.
        foreach (AppCommand command in commands)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var box = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, ShortcutsEnabled = false };
            box.KeyDown += (_, e) =>
            {
                e.SuppressKeyPress = true;
                CaptureHotkey(command, e.KeyData, HotkeyText.WinKeyDown);
            };
            _hotkeys[command] = box;
            table.Controls.Add(new Label
                { Text = CommandCatalog.Name(command), AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(box);
        }

        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var group = new GroupBox
            { Text = T("settings.hotkeys.captureHelp"), Dock = DockStyle.Fill };
        group.Controls.Add(table);
        return group;
    }

    private TableLayoutPanel BuildPrograms()
    {
        var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.Controls.Add(BuildProgramList(T("programs.exclusions"), _excluded, _newExcluded), 0, 0);
        page.Controls.Add(
            BuildProgramList(T("programs.inclusions"), _included, _newIncluded),
            0, 1);
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

        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.Add(save);
        row.Controls.Add(cancel);
        return row;
    }
}
