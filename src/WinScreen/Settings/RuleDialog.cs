using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Settings;

/// <summary>
/// Changes one saved setup ("rule", roadmap P1-5): which of its program's windows it is for, and what they get. Says
/// how many open windows it is for as the fields change.
/// </summary>
internal sealed class RuleDialog : Form
{
    private readonly ProgramSetup _rule;
    private readonly TextBox _title = new() { Width = 260 }, _class = new() { Width = 260 };
    private readonly CheckBox _pinned = Check(CommandCatalog.Name(AppCommand.TogglePin)), _dark = Check(CommandCatalog.Name(AppCommand.ToggleDark)),
        _borderless = Check(CommandCatalog.Name(AppCommand.ToggleBorderless)), _anchor = Check(CommandCatalog.Name(AppCommand.ToggleAnchor)),
        _noToolbar = Check(T("rule.noToolbar")), _everyTime = Check(T("rule.everyTime")), _off = Check(T("rule.off"));
    private readonly NumericUpDown _opacity = new() { Minimum = AppSettings.LowestOpacityPercent, Maximum = 100, Width = 90 };
    private readonly ComboBox _place = Choice(), _display = Choice();
    private readonly Label _matches = new() { AutoSize = true, UseMnemonic = false };
    private readonly string[] _displays;

    public RuleDialog(ProgramSetup rule)
    {
        _rule = rule;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = string.Format(T("rule.title"), rule.Program);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _title.Text = rule.TitleContains ?? "";
        _class.Text = rule.WindowClass ?? "";
        _pinned.Checked = rule.Pinned;
        _opacity.Value = Math.Clamp((int)Math.Round(rule.Alpha * 100.0 / 255), AppSettings.LowestOpacityPercent, 100);
        _dark.Checked = rule.Dark;
        _place.Items.Add(T("rule.asItIs"));
        foreach (AppCommand command in Enum.GetValues<AppCommand>().Where(command => CommandCatalog.AnchorOf(command) is not null))
            _place.Items.Add(CommandCatalog.Name(command));
        _place.SelectedIndex = rule.Place is { } place ? (int)place + 1 : 0;
        Screen[] screens = Screen.AllScreens;
        _displays = screens.Select(screen => screen.DeviceName).ToArray();
        _display.Items.Add(T("rule.asItIs"));
        for (int i = 0; i < screens.Length; i++)
            _display.Items.Add($"{i + 1}: {screens[i].Bounds.Width} × {screens[i].Bounds.Height}");
        int shown = Array.FindIndex(_displays, name => string.Equals(name, rule.Display, StringComparison.OrdinalIgnoreCase));
        if (shown < 0 && !string.IsNullOrEmpty(rule.Display))
        {
            _display.Items.Add(rule.Display); // A display that isn't connected now stays chosen.
            shown = _display.Items.Count - 2;
            _displays = [.. _displays, rule.Display];
        }
        _display.SelectedIndex = shown + 1;
        _borderless.Checked = rule.Borderless;
        _anchor.Checked = rule.Anchor;
        _noToolbar.Checked = rule.NoToolbar;
        _everyTime.Checked = rule.EveryTime;
        _off.Checked = rule.Off;

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        void Row(string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false });
            table.Controls.Add(control);
        }
        void Whole(Control control)
        {
            table.Controls.Add(control);
            table.SetColumnSpan(control, 2);
        }

        Row(T("rule.titleContains"), _title);
        Whole(new Label { Text = T("rule.titleHint"), AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText });
        Row(T("rule.windowClass"), _class);
        Whole(_matches);
        Whole(_pinned);
        Row(T("rule.opacity"), _opacity);
        Whole(_dark);
        Row(T("rule.place"), _place);
        Row(T("rule.display"), _display);
        Whole(_borderless);
        Whole(_anchor);
        Whole(_noToolbar);
        Whole(_everyTime);
        Whole(_off);
        var apply = new Button { Text = T("common.apply"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = T("common.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        table.Controls.Add(apply);
        table.Controls.Add(cancel);
        AcceptButton = apply;
        CancelButton = cancel;
        Controls.Add(table);

        _title.TextChanged += (_, _) => CountMatches();
        _class.TextChanged += (_, _) => CountMatches();
        CountMatches();
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false };

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    /// <summary>The rule as the fields say, kept as the same saved setup so the Settings list doesn't lose its place.</summary>
    public ProgramSetup Result()
    {
        _rule.TitleContains = Blank(_title.Text);
        _rule.WindowClass = Blank(_class.Text);
        _rule.Pinned = _pinned.Checked;
        _rule.Alpha = (byte)Math.Round((int)_opacity.Value * 255 / 100.0);
        _rule.Dark = _dark.Checked;
        _rule.Place = _place.SelectedIndex > 0 ? (WindowAnchor)(_place.SelectedIndex - 1) : null;
        _rule.Display = _display.SelectedIndex > 0 ? _displays[_display.SelectedIndex - 1] : null;
        _rule.Borderless = _borderless.Checked;
        _rule.Anchor = _anchor.Checked;
        _rule.NoToolbar = _noToolbar.Checked;
        _rule.EveryTime = _everyTime.Checked;
        _rule.Off = _off.Checked;
        return _rule;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>How many open main windows of the program the title and class written so far are for.</summary>
    private void CountMatches()
    {
        var probe = new ProgramSetup { Program = _rule.Program, ExecutablePath = _rule.ExecutablePath, TitleContains = Blank(_title.Text), WindowClass = Blank(_class.Text) };
        int count = 0;
        for (nint window = User32.GetWindow(User32.GetDesktopWindow(), 5); window != 0; window = User32.GetWindow(window, 2))
        {
            if (!User32.IsWindowVisible(window) || !WindowInfo.IsMainWindow(window)) continue;
            bool sameProgram = string.IsNullOrEmpty(probe.ExecutablePath)
                ? string.Equals(WindowInfo.ProgramOf(window), probe.Program, StringComparison.OrdinalIgnoreCase)
                : string.Equals(WindowInfo.ExecutablePath(window), probe.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            if (sameProgram && probe.MatchesWindow(WindowInfo.Title(window), WindowInfo.ClassName(window))) count++;
        }
        _matches.Text = string.Format(T("rule.matchesNow"), count);
    }
}
