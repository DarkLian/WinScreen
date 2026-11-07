using static WinScreen.Settings.UiText;
using WinScreen.Tray;
using WinScreen.Updates;

namespace WinScreen.Settings;

/// <summary>The settings window. Saving applies the changes straight away.</summary>
public sealed class SettingsWindow : Form
{
    private readonly (AppCommand Command, string Label)[] Commands =
    [
        (AppCommand.TogglePin, T("Pin on top")),
        (AppCommand.OpacityUp, T("More opaque")),
        (AppCommand.OpacityDown, T("More see-through")),
        (AppCommand.ToggleClickThrough, T("Click-through")),
        (AppCommand.OpenPip, T("Picture-in-Picture")),
        (AppCommand.ToggleDark, T("Dark mode")),
        (AppCommand.ToggleGlass, T("Glass mode")),
        (AppCommand.ToggleAnchor, T("Anchor")),
        (AppCommand.ToggleDefaultOpacity, T("See-through at the default level")),
        (AppCommand.ToggleShrink, T("Shrink")),
        (AppCommand.ToggleCrop, T("Crop")),
        (AppCommand.SaveWindowSetup, T("Remember setup for the program")),
        (AppCommand.RestoreAll, T("Restore all windows")),
        (AppCommand.RestoreWindow, T("Restore this window")),
    ];

    private readonly AppSettings _settings;
    private readonly CheckBox _showNotifications = new() { Text = T("Show notifications"), AutoSize = true };
    private readonly CheckBox _automaticUpdates = new() { Text = T("Download updates automatically; install when WinScreen exits"), AutoSize = true };
    private readonly CheckBox _experimental = new() { Text = T("Enable experimental effects (Dark, Glass, Crop, Shrink, auto-move)"), AutoSize = true };
    private readonly CheckBox _pipInput = new() { Text = T("Forward PiP clicks (experimental; off uses Go to original)"), AutoSize = true };
    private readonly ErrorProvider _errors = new();
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private static readonly string[] Languages = ["system", "en", "zh-Hans", "zh-Hant"];
    private readonly StartWithWindows _startup;
    private readonly Func<AppSettings, IReadOnlyList<string>> _apply;
    private readonly Dictionary<AppCommand, TextBox> _hotkeys = [];
    private readonly ListBox _excluded = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _included = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _setups = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _newExcluded = new() { Dock = DockStyle.Fill, PlaceholderText = T("Program name, for example ExampleProgram.exe") };
    private readonly TextBox _newIncluded = new() { Dock = DockStyle.Fill, PlaceholderText = T("Program name, for example ExampleProgram.exe") };
    private readonly NumericUpDown _defaultOpacity = new()
    {
        Minimum = AppSettings.LowestOpacityPercent,
        Maximum = AppSettings.HighestOpacityPercent,
        Width = 70,
    };
    private readonly CheckBox _startWithWindows = new() { Text = T("Start WinScreen when I sign in"), AutoSize = true };
    private readonly CheckBox _showToolbar = new() { Text = T("Show the toolbar when the mouse rests on a title bar"), AutoSize = true };
    private readonly CheckBox _pauseInFullScreen = new() { Text = T("Turn off during games and full-screen apps"), AutoSize = true };
    private readonly CheckBox _pausePin = new() { Text = T("Pause pinning while a pinned window is maximized"), AutoSize = true };
    private readonly CheckBox _opaqueOnHover = new() { Text = T("Make see-through windows solid while the mouse is over them"), AutoSize = true };
    private readonly CheckBox _highlightPinned = new() { Text = T("Put a red border around pinned windows"), AutoSize = true };
    private readonly CheckBox _pipAutoMove = new() { Text = T("Move Picture-in-Picture views away from text and images"), AutoSize = true };
    private readonly HashSet<ProgramSetup> _removedSetups = [];
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(500, 0) };

    public SettingsWindow(AppSettings settings, StartWithWindows startup, Func<AppSettings, IReadOnlyList<string>> apply)
    {
        _settings = settings;
        _startup = startup;
        _apply = apply;
        _errors.ContainerControl = this;
        _experimental.CheckedChanged += (_, _) =>
        {
            _pipInput.Enabled = _experimental.Checked;
            _pipAutoMove.Enabled = _experimental.Checked;
        };
        _language.Items.AddRange([T("System default"), "English", "简体中文", "繁體中文"]);

        Text = T("WinScreen Settings");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var tabs = new TabControl { Size = new Size(540, 490) };
        tabs.TabPages.Add(Tab(T("General"), BuildGeneral()));
        tabs.TabPages.Add(Tab(T("Hotkeys"), BuildHotkeys()));
        tabs.TabPages.Add(Tab(T("Programs"), BuildPrograms()));
        tabs.TabPages.Add(Tab(T("Saved setups"), BuildSetups()));

        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        page.Controls.Add(tabs);
        page.Controls.Add(_status);
        page.Controls.Add(BuildButtons());
        Controls.Add(page);

        LoadValues();
        _pipInput.Enabled = _experimental.Checked;
        _pipAutoMove.Enabled = _experimental.Checked;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _errors.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Called when a key combination is pressed in a hotkey box.</summary>
    internal void CaptureHotkey(AppCommand command, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.Delete or Keys.Back)
        {
            _hotkeys[command].Text = "";
            return;
        }

        bool isModifierAlone = key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin;
        bool hasModifier = (keyData & (Keys.Control | Keys.Alt)) != 0;
        if (!isModifierAlone && hasModifier)
            _hotkeys[command].Text = HotkeyText.Format(keyData);
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
        draft.ExcludedPrograms = _excluded.Items.Cast<string>().ToList();
        draft.IncludedPrograms = _included.Items.Cast<string>().ToList();
        draft.SavedSetups.RemoveAll(setup => _removedSetups.Any(removed => removed.Program == setup.Program && removed.ExecutablePath == setup.ExecutablePath));
        draft.ShowToolbar = _showToolbar.Checked;
        draft.ShowNotifications = _showNotifications.Checked;
        draft.AutomaticUpdates = _automaticUpdates.Checked;
        draft.ExperimentalFeatures = _experimental.Checked;
        draft.PipClickForwarding = _pipInput.Checked;
        draft.StartWithWindows = _startWithWindows.Checked;
        draft.Language = Languages[Math.Max(0, _language.SelectedIndex)];
        draft.PauseInFullScreen = _pauseInFullScreen.Checked;
        draft.PausePinWhenMaximized = _pausePin.Checked;
        draft.OpaqueOnHover = _opaqueOnHover.Checked;
        draft.HighlightPinned = _highlightPinned.Checked;
        draft.PipAutoMove = _pipAutoMove.Checked;
        draft.DefaultOpacityPercent = (int)_defaultOpacity.Value;
        try
        {
            IReadOnlyList<string> taken = _apply(draft);
            _status.Text = taken.Count == 0 ? "" : T("Another app already uses: ") + string.Join(", ", taken);
            foreach (var box in _hotkeys.Values)
                if (box.Text.Length > 0 && taken.Any(problem => problem.Contains(box.Text, StringComparison.Ordinal)))
                    _errors.SetError(box, _status.Text);
            if (taken.Count > 0)
                return false;
            _startup.IsOn = _startWithWindows.Checked;
            _settings.CopyFrom(draft);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
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
        _pausePin.Checked = _settings.PausePinWhenMaximized;
        _opaqueOnHover.Checked = _settings.OpaqueOnHover;
        _highlightPinned.Checked = _settings.HighlightPinned;
        _pipAutoMove.Checked = _settings.PipAutoMove;
        _defaultOpacity.Value = Math.Clamp(_settings.DefaultOpacityPercent, AppSettings.LowestOpacityPercent, AppSettings.HighestOpacityPercent);
    }

    private static TabPage Tab(string title, Control content)
    {
        var tab = new TabPage(title) { Padding = new Padding(8) };
        tab.Controls.Add(content);
        return tab;
    }

    private FlowLayoutPanel BuildGeneral()
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        page.Controls.Add(new Label { Text = T("Language (restart WinScreen to apply)"), AutoSize = true });
        page.Controls.Add(_language);
        foreach (CheckBox option in new[] { _showNotifications, _automaticUpdates, _experimental, _pipInput, _startWithWindows, _showToolbar, _pauseInFullScreen, _pausePin, _opaqueOnHover, _highlightPinned, _pipAutoMove })
        {
            option.MaximumSize = new Size(490, 0);
            page.Controls.Add(option);
        }
        if (!VelopackUpdateSession.IsConfigured)
            page.Controls.Add(new Label { Text = T("Automatic updates need a release repository configured in this build."), AutoSize = true, MaximumSize = new Size(490, 0) });
        page.Controls.Add(new Label { Text = T("Views that do this stay out of screenshots and screen sharing."), AutoSize = true, Margin = new Padding(20, 0, 0, 0) });

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        row.Controls.Add(new Label { Text = T("Default see-through level: solid at"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        row.Controls.Add(_defaultOpacity);
        row.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        page.Controls.Add(row);
        return page;
    }

    private GroupBox BuildHotkeys()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        foreach ((AppCommand command, string label) in Commands)
        {
            var box = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, ShortcutsEnabled = false };
            box.KeyDown += (_, e) =>
            {
                e.SuppressKeyPress = true;
                CaptureHotkey(command, e.KeyData);
            };
            _hotkeys[command] = box;
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(box);
        }

        var group = new GroupBox { Text = T("Click a box and press the keys, or Delete to clear"), Dock = DockStyle.Fill };
        group.Controls.Add(table);
        return group;
    }

    private TableLayoutPanel BuildPrograms()
    {
        var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        page.Controls.Add(BuildProgramList(T("Programs WinScreen leaves alone"), _excluded, _newExcluded), 0, 0);
        page.Controls.Add(BuildProgramList(T("Only work on these programs (leave empty for all programs)"), _included, _newIncluded), 0, 1);
        return page;
    }

    private static GroupBox BuildProgramList(string title, ListBox list, TextBox newProgram)
    {
        var add = new Button { Text = T("&Add"), AutoSize = true };
        add.Click += (_, _) =>
        {
            AddProgram(list, newProgram.Text);
            newProgram.Text = "";
        };
        var remove = new Button { Text = T("&Remove"), AutoSize = true };
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
        var remove = new Button { Text = T("&Remove"), AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (_setups.SelectedItem is ProgramSetup selected)
                RemoveSetup(selected);
        };

        var hint = new Label
        {
            Text = T("Set a window up the way you like it and press the Remember setup hotkey. Its program's windows then start that way: pinned, see-through and dark. Click-through, Glass, Crop and Shrink aren't remembered."),
            AutoSize = true,
            MaximumSize = new Size(490, 0),
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

        var group = new GroupBox { Text = T("Windows that start set up"), Dock = DockStyle.Fill };
        group.Controls.Add(table);
        return group;
    }

    private FlowLayoutPanel BuildButtons()
    {
        var save = new Button { Text = T("&Save"), AutoSize = true };
        save.Click += (_, _) =>
        {
            if (SaveChanges())
                Close();
        };
        var cancel = new Button { Text = T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;

        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.Add(save);
        row.Controls.Add(cancel);
        return row;
    }
}
