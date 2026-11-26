using WinScreen.Glass;
using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

// What a window's Glass panel does to the window it is for.
internal sealed record GlassPanelActions(Action<AppGlassLevels> LevelsChanged, Action<BackgroundChoice> BackgroundChanged)
{
    // Null for a part that was left as it was.
    public required Action<AppGlassLevels?, BackgroundChoice?> Save { get; init; }
    public required Func<(AppGlassLevels Levels, BackgroundChoice Background)> Reset { get; init; }
    public required Func<bool> StillOn { get; init; }
    // Which of the window's pictures and videos shows, or less than 0.
    public Func<int> Showing { get; init; } = () => -1;
    public Action<int> Show { get; init; } = _ => { };
}

// Changes show on the one window at once and last until it closes; Save keeps them for its app.
internal sealed class GlassLevelsWindow : ThemedDialog
{
    private readonly NumericUpDown _background = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _text = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _fade = new CurvedNumber { Minimum = 1, Maximum = EffectPreferences.MostFadeRange, Width = 70, Curve = CurvedNumber.FadeCurve };
    private readonly ComboBox _textColor = Picker(160);
    private readonly SwitchBox _shadow = new() { Compact = true, AccessibleName = T("settings.effects.shadow"), AutoSize = true };
    private readonly NumericUpDown _darkPage = new() { Minimum = 0, Maximum = EffectPreferences.MostDarkPage, Width = 70 };
    private readonly NumericUpDown _darkText = new() { Minimum = EffectPreferences.LeastDarkText, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _darkColors = new() { Minimum = 0, Maximum = 100, Width = 70 };

    private readonly ComboBox _items = Picker(250);
    private readonly PlainButton _remove = Small(T("settings.backgrounds.clear"));
    private readonly ComboBox _fit = Picker(190);
    private readonly NumericUpDown _volume = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _speed = new() { Minimum = BackgroundItem.SlowestSpeed, Maximum = BackgroundItem.FastestSpeed, Width = 70 };
    private readonly NumericUpDown _brightness = new() { Minimum = BackgroundItem.DimmestBrightness, Maximum = 100, Width = 70 };
    private readonly PlainButton _applyAll = Small(T("settings.backgrounds.applyAllButton"));
    // On: the Glass rows are the chosen picture or video's own levels; off: the window's.
    private readonly SwitchBox _itemOwn = new() { Compact = true, AccessibleName = T("glassLevels.itemOwn"), AutoSize = true };
    private readonly bool _dark;
    private AppGlassLevels _windowLevels;
    private readonly ComboBox _order = Picker(190);
    private readonly ComboBox _switch = Picker(190);
    private readonly ComboBox _minutes = Picker(100);

    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    private readonly GlassPanelActions _actions;
    private readonly string _program;
    private BackgroundChoice _choice;
    private bool _filling, _levelsChanged, _backgroundChanged;

    // With dark, the window has Dark mode: only Dark's levels show, and the Glass levels and background are kept as they are.
    public GlassLevelsWindow(string program, AppGlassLevels levels, BackgroundChoice background, GlassPanelActions actions,
        bool ownLevels = false, bool ownBackground = false, bool dark = false)
        : base(string.Format(dark ? T("darkLevels.title") : T("glassLevels.title"), program))
    {
        (_program, _choice, _actions, _levelsChanged, _backgroundChanged) = (program, background, actions, ownLevels, ownBackground);
        (_dark, _windowLevels) = (dark, levels);
        StartPosition = FormStartPosition.Manual;
        _textColor.Items.AddRange([T("settings.effects.textColor.asItIs"), T("settings.effects.textColor.light"), T("settings.effects.textColor.dark")]);
        _fit.Items.AddRange([T("settings.backgrounds.fill"), T("settings.backgrounds.fitWhole"),
            T("settings.backgrounds.stretch"), T("settings.backgrounds.tile"), T("settings.backgrounds.center")]);
        _order.Items.AddRange([T("settings.backgrounds.order.inOrder"), T("settings.backgrounds.order.shuffle")]);
        _switch.Items.AddRange([T("settings.backgrounds.switch.videoEnds"), T("settings.backgrounds.switch.timed")]);
        _minutes.Items.AddRange([.. BackgroundChoice.MinuteChoices.Select(minutes => (object)minutes.ToString(System.Globalization.CultureInfo.CurrentCulture))]);

        if (dark)
        {
            Heading(T("window.dark"));
            Slider(T("settings.dark.page"), _darkPage);
            Slider(T("settings.dark.text"), _darkText);
            Slider(T("settings.dark.colors"), _darkColors);
            Note(T("settings.dark.colorsHelp"));
        }
        else
            BuildGlass();
        Fill(levels);
        ShowBackground(0);
        foreach (NumericUpDown box in new[] { _background, _text, _fade, _darkPage, _darkText, _darkColors }) box.ValueChanged += (_, _) => ChangeLevels();
        _textColor.SelectedIndexChanged += (_, _) => ChangeLevels();
        _shadow.CheckedChanged += (_, _) => ChangeLevels();
        _itemOwn.CheckedChanged += (_, _) =>
        {
            if (_filling || _choice.Items.Count == 0) return;
            if (_itemOwn.Checked) ChangeItem(item => item with { GlassLevels = ItemGlass.From(Current()) });
            else
            {
                ChangeItem(item => item with { GlassLevels = null });
                Fill(_windowLevels);
            }
        };

        Below(string.Format(T("glassLevels.help"), program));
        CancelButton = AddAction(T("common.close"), false, Close);
        AddAction(T("glassLevels.reset"), false, () =>
        {
            (AppGlassLevels saved, _choice) = _actions.Reset();
            _levelsChanged = _backgroundChanged = false;
            _windowLevels = saved;
            Fill(saved);
            ShowBackground(0);
        });
        AddAction(T("glassLevels.save"), true, () =>
        {
            // Nothing changed: the levels as they are become the app's own, as the button says.
            _actions.Save(_levelsChanged || !_backgroundChanged ? Current() : null, _backgroundChanged ? _choice : null);
            Close();
        });
        _watch.Tick += (_, _) =>
        {
            // Closes with the window or its effect.
            if (!_actions.StillOn()) { Close(); return; }
            // A set goes on by itself: what shows is what the rows change.
            if (_choice.Items.Count > 1 && MouseButtons == MouseButtons.None && !_items.DroppedDown && Environment.TickCount64 - _chosenAt > 2000 &&
                _actions.Showing() is >= 0 and var showing && showing != At && showing < _items.Items.Count) ShowBackground(showing);
        };
        _watch.Start();
    }

    private void BuildGlass()
    {
        Heading(T("glassLevels.glass"));
        Slider(T("settings.effects.background"), _background);
        Slider(T("settings.effects.text"), _text);
        Slider(T("settings.effects.fade"), _fade);
        Row(T("settings.effects.textColor"), _textColor);
        Row(T("settings.effects.shadow"), _shadow);
        Row(T("glassLevels.itemOwn"), _itemOwn);

        Column();
        Heading(T("settings.backgrounds.file"));
        _items.AccessibleName = T("settings.backgrounds.items");
        Whole(_items);
        PlainButton add = Small(T("common.add")), link = Small(T("settings.backgrounds.addLink"));
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = SettingsStyle.Page };
        buttons.Controls.AddRange([add, link, _remove, _applyAll]);
        Whole(buttons);
        Row(T("settings.backgrounds.fit"), _fit);
        Slider(T("settings.backgrounds.sound"), _volume);
        Slider(T("settings.backgrounds.speed"), _speed);
        Slider(T("settings.backgrounds.brightness"), _brightness);
        Row(T("settings.backgrounds.order"), _order);
        Row(T("settings.backgrounds.switch"), _switch);
        Row(T("settings.backgrounds.pictureMinutes"), _minutes);

        _items.SelectedIndexChanged += (_, _) =>
        {
            if (_filling) return;
            ShowBackground(At);
            if (_choice.Items.Count > 1) _actions.Show(At);
        };
        add.Click += (_, _) => AddFiles();
        link.Click += (_, _) => { if (LinkDialog.Ask(this) is { } address) Add([address]); };
        _remove.Click += (_, _) => Remove();
        _applyAll.Click += (_, _) => Change(choice => choice.WithLookOf(At));
        _fit.SelectedIndexChanged += (_, _) => ChangeItem(item => item with { Fit = (BackdropFit)Math.Max(0, _fit.SelectedIndex) });
        _volume.ValueChanged += (_, _) => ChangeItem(item => item with { Volume = (int)_volume.Value });
        _speed.ValueChanged += (_, _) => ChangeItem(item => item with { Speed = (int)_speed.Value });
        _brightness.ValueChanged += (_, _) => ChangeItem(item => item with { Brightness = (int)_brightness.Value });
        _order.SelectedIndexChanged += (_, _) => Change(choice => choice with { Order = (BackdropOrder)Math.Max(0, _order.SelectedIndex) });
        _switch.SelectedIndexChanged += (_, _) => Change(choice => choice with { Switch = (BackdropSwitch)Math.Max(0, _switch.SelectedIndex) });
        _minutes.SelectedIndexChanged += (_, _) =>
            Change(choice => choice with { PictureMinutes = BackgroundChoice.MinuteChoices[Math.Max(0, _minutes.SelectedIndex)] });
    }

    private long _chosenAt;

    private static ComboBox Picker(int width) => new ThemedCombo { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };

    private static PlainButton Small(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };

    private void Slider(string label, NumericUpDown box) => Row(label, new NumberSlider(box) { AccessibleName = label.TrimEnd(':', '：', ' ') });

    // Where it was opened, kept on that screen.
    internal void ShowAt(Point point)
    {
        CreateControl();
        Rectangle area = Screen.FromPoint(point).WorkingArea;
        Size size = GetPreferredSize(Size.Empty);
        Location = new Point(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        Show();
        Activate();
    }

    private void ChangeLevels()
    {
        if (_filling) return;
        if (!_dark && _itemOwn.Checked && _choice.Items.Count > 0)
        {
            ChangeItem(item => item with { GlassLevels = ItemGlass.From(Current()) });
            return;
        }
        _windowLevels = Current();
        _levelsChanged = true;
        _actions.LevelsChanged(Current());
    }

    private void Fill(AppGlassLevels levels)
    {
        _filling = true;
        try
        {
            _background.Value = Math.Clamp(levels.BackgroundPercent, 0, 100);
            _text.Value = Math.Clamp(levels.TextPercent, 20, 100);
            _fade.Value = Math.Clamp(levels.FadeRangePercent, 1, EffectPreferences.MostFadeRange);
            _textColor.SelectedIndex = (int)levels.TextColor;
            _shadow.Checked = levels.TextShadow;
            _darkPage.Value = Math.Clamp(levels.DarkPagePercent, 0, EffectPreferences.MostDarkPage);
            _darkText.Value = Math.Clamp(levels.DarkTextPercent, EffectPreferences.LeastDarkText, 100);
            _darkColors.Value = Math.Clamp(levels.DarkColorsPercent, 0, 100);
        }
        finally { _filling = false; }
    }

    private AppGlassLevels Current() => new()
    {
        Program = _program, BackgroundPercent = (int)_background.Value, TextPercent = (int)_text.Value, FadeRangePercent = (int)_fade.Value,
        TextColor = (GlassText)Math.Max(0, _textColor.SelectedIndex), TextShadow = _shadow.Checked,
        DarkPagePercent = (int)_darkPage.Value, DarkTextPercent = (int)_darkText.Value, DarkColorsPercent = (int)_darkColors.Value,
    };

    private int At => Math.Max(0, _items.SelectedIndex);

    private static long _refreshes;
    internal static long Refreshes => Interlocked.Read(ref _refreshes);

    private void ShowBackground(int at)
    {
        Interlocked.Increment(ref _refreshes);
        IReadOnlyList<BackgroundItem> items = _choice.Items;
        ItemGlass? itemGlass = null;
        _filling = true;
        try
        {
            string[] names = items.Count == 0 ? [T("settings.backgrounds.none")]
                : [.. items.Select(item => BackdropChoice.IsLinkText(item.File) ? item.File : Path.GetFileName(item.File))];
            if (!names.SequenceEqual(_items.Items.Cast<string>()))
            {
                _items.Items.Clear();
                _items.Items.AddRange([.. names]);
            }
            at = Math.Clamp(at, 0, names.Length - 1);
            if (_items.SelectedIndex != at) _items.SelectedIndex = at;
            BackgroundItem item = items.Count > 0 ? items[at] : new BackgroundItem();
            _fit.SelectedIndex = (int)item.Fit;
            _volume.Value = item.Volume;
            _speed.Value = Math.Clamp(item.Speed, BackgroundItem.SlowestSpeed, BackgroundItem.FastestSpeed);
            _brightness.Value = Math.Clamp(item.Brightness, BackgroundItem.DimmestBrightness, 100);
            _order.SelectedIndex = (int)_choice.Order;
            _switch.SelectedIndex = (int)_choice.Switch;
            _minutes.SelectedIndex = Array.IndexOf(BackgroundChoice.MinuteChoices,
                BackgroundChoice.MinuteChoices.MinBy(minutes => Math.Abs(minutes - _choice.PictureMinutes)));
            bool chosen = items.Count > 0, video = chosen && BackdropChoice.IsVideoText(item.File);
            _items.Enabled = _order.Enabled = _switch.Enabled = _minutes.Enabled = _applyAll.Enabled = items.Count > 1;
            _remove.Enabled = _fit.Enabled = _brightness.Enabled = chosen;
            _volume.Enabled = video;
            _speed.Enabled = video || chosen && BackdropChoice.Kind(item.File) == ".gif";
            _itemOwn.Enabled = chosen;
            _itemOwn.Checked = item.GlassLevels is not null;
            itemGlass = item.GlassLevels;
        }
        finally { _filling = false; }
        // The Glass rows show the levels in use over the one chosen.
        if (!_dark) Fill(itemGlass is { } own ? _windowLevels with
        {
            BackgroundPercent = own.PagePercent, TextPercent = own.TextPercent, FadeRangePercent = own.FadeRangePercent,
            TextColor = own.TextColor, TextShadow = own.TextShadow,
        } : _windowLevels);
        _chosenAt = Environment.TickCount64;
    }

    private void Change(Func<BackgroundChoice, BackgroundChoice> change)
    {
        if (_filling) return;
        _choice = change(_choice).Validated();
        _backgroundChanged = true;
        _actions.BackgroundChanged(_choice);
    }

    private void ChangeItem(Func<BackgroundItem, BackgroundItem> change) => Change(choice => choice.WithItems(choice.Items
        .Select((item, index) => index == At ? change(item) : item)));

    private void AddFiles()
    {
        string pictures = string.Join(";", BackdropChoice.Pictures.Concat(BackdropChoice.Animations).Select(kind => "*" + kind));
        string videos = string.Join(";", BackdropChoice.Videos.Select(kind => "*" + kind));
        using var dialog = new OpenFileDialog
        {
            Filter = $"{T("settings.backgrounds.files")}|{pictures};{videos}|{T("settings.backgrounds.pictures")}|{pictures}|{T("settings.backgrounds.videos")}|{videos}",
            Multiselect = true,
        };
        string? folder = _choice.Items.Select(item => item.File).Where(file => !BackdropChoice.IsLinkText(file))
            .Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists);
        if (folder is not null) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == DialogResult.OK) Add(dialog.FileNames);
    }

    // After the one chosen, taking its fit so a set looks alike; the first of them shows.
    internal void Add(IReadOnlyList<string> files)
    {
        BackgroundItem[] added = [.. files.Select(file => new BackgroundItem { File = file.Trim(), Fit = _choice.Items.ElementAtOrDefault(At)?.Fit ?? BackdropFit.Fill })];
        int at = _choice.Items.Count == 0 ? 0 : At + 1;
        Change(choice => choice.WithItems([.. choice.Items.Take(at), .. added, .. choice.Items.Skip(at)]));
        ShowBackground(at);
        if (_choice.Items.Count > 1) _actions.Show(At);
    }

    private void Remove()
    {
        int at = At;
        Change(choice => choice.WithItems(choice.Items.Where((_, index) => index != at)));
        ShowBackground(Math.Max(0, Math.Min(at, _choice.Items.Count - 1)));
        if (_choice.Items.Count > 1) _actions.Show(At);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Dispose();
        base.OnFormClosed(e);
    }
}
