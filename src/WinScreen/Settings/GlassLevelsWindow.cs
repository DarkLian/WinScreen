using static WinScreen.Localization.UiText;

namespace WinScreen.Settings;

// Changes show on the one window at once and last until it closes; Save keeps them for its app.
internal sealed class GlassLevelsWindow : ThemedDialog
{
    private readonly NumericUpDown _background = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _text = new() { Minimum = 20, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _fade = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 500 };
    private bool _filling;

    public GlassLevelsWindow(string program, AppGlassLevels levels, Action<AppGlassLevels> changed, Action<AppGlassLevels> save,
        Func<AppGlassLevels> reset, Func<bool> stillOn) : base(string.Format(T("glassLevels.title"), program))
    {
        StartPosition = FormStartPosition.Manual;
        Fill(levels);
        foreach ((string label, NumericUpDown box) in new[]
                 {
                     (T("settings.effects.background"), _background), (T("settings.effects.text"), _text), (T("settings.effects.fade"), _fade),
                 })
        {
            box.ValueChanged += (_, _) => { if (!_filling) changed(Current(program)); };
            Row(label, new NumberSlider(box) { AccessibleName = label.TrimEnd(':', '：', ' ') });
        }
        Note(string.Format(T("glassLevels.help"), program));
        CancelButton = AddAction(T("common.close"), false, Close);
        AddAction(T("glassLevels.reset"), false, () => Fill(reset()));
        AddAction(T("glassLevels.save"), true, () =>
        {
            save(Current(program));
            Close();
        });
        // Closes with the window or its Glass.
        _watch.Tick += (_, _) => { if (!stillOn()) Close(); };
        _watch.Start();
    }

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

    private void Fill(AppGlassLevels levels)
    {
        _filling = true;
        try
        {
            _background.Value = Math.Clamp(levels.BackgroundPercent, 0, 100);
            _text.Value = Math.Clamp(levels.TextPercent, 20, 100);
            _fade.Value = Math.Clamp(levels.FadeRangePercent, 1, 100);
        }
        finally { _filling = false; }
    }

    private AppGlassLevels Current(string program) => new()
    {
        Program = program, BackgroundPercent = (int)_background.Value, TextPercent = (int)_text.Value, FadeRangePercent = (int)_fade.Value,
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Dispose();
        base.OnFormClosed(e);
    }
}
