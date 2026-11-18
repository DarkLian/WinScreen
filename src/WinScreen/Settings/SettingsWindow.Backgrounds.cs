using static WinScreen.Localization.UiText;
using WinScreen.Glass;

namespace WinScreen.Settings;

// The list holds the backgrounds being edited; the card under it edits the one chosen in the list.
public sealed partial class SettingsWindow
{
    private const int MostBackgroundRows = 5;
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
        _backgroundEditor = Card(T("settings.backgrounds.everyApp"), false, _backgroundPreview, _backgroundFileRow,
            SettingRow(T("settings.backgrounds.fit"), _backgroundFit), SettingRow(T("settings.backgrounds.sound"), _backgroundVolume),
            Note(T("settings.backgrounds.soundHelp")));

        FlowLayoutPanel page = ScrollingPage();
        // Glass's own settings decide how much of the background shows, so they come first.
        page.Controls.Add(Card(T("settings.glass.title"), true, _layeredGlass,
            SettingRow(T("settings.effects.background"), _glassBackground), SettingRow(T("settings.effects.text"), _glassText),
            Note(T("settings.effects.help"))));
        page.Controls.Add(Card(T("settings.backgrounds.title"), true, Note(T("settings.backgrounds.help")), _backgroundFrame, adding));
        page.Controls.Add(_backgroundEditor);
        ListBackgrounds(null);
        return page;
    }

    private string? ShownApp => (_backgroundList.SelectedItem as BackgroundEntry)?.Program;

    private BackgroundChoice ChoiceOf(string? program) => program is null ? _backgroundEdit.Everywhere : _backgroundEdit.OwnOf(program) ?? new();

    private static bool IsVideo(string file) => BackdropChoice.Videos.Contains(Path.GetExtension(file).ToLowerInvariant());

    private string DetailOf(string? program)
    {
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
        foreach (AppBackground app in _backgroundEdit.Apps) _backgroundList.Items.Add(Entry(app.Program));
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
        }
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
                Apps = _backgroundEdit.Apps.Select(entry => string.Equals(entry.Program, app, StringComparison.OrdinalIgnoreCase)
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

    private void AddBackgroundApp()
    {
        string name = BackgroundPreferences.ProgramKey(_newBackgroundApp.Text);
        _newBackgroundApp.Text = "";
        if (name.Length == 0) return;
        if (_backgroundEdit.OwnOf(name) is null)
            _backgroundEdit = _backgroundEdit with { Apps = [.. _backgroundEdit.Apps, new AppBackground { Program = name }] };
        ListBackgrounds(name);
        QueueApplyRefresh();
    }

    private void RemoveBackgroundApp()
    {
        if (ShownApp is not { } app) return;
        int at = _backgroundList.SelectedIndex;
        bool hadKeyboard = _backgroundRemoveApp.Focused;
        _backgroundEdit = _backgroundEdit with
        {
            Apps = _backgroundEdit.Apps.Where(entry => !string.Equals(entry.Program, app, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        string? next = at - 1 >= 1 ? _backgroundEdit.Apps[at - 2].Program : null;
        ListBackgrounds(next);
        if (hadKeyboard && !_backgroundRemoveApp.Enabled) _backgroundList.Focus(); // Back at Every app, which stays.
        QueueApplyRefresh();
    }
}
