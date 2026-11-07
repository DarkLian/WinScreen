using static WinScreen.Settings.UiText;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Tray;

/// <summary>
/// A small bar of buttons that appears on the title bar of the window in use when the mouse rests
/// there. It never takes focus, so the window it acts on stays active.
/// </summary>
public sealed class WindowToolbar : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int ButtonSize = 34;
    private const int TitleBarHeight = 34;
    private const int HoverDelayMilliseconds = 350;
    private const int Interval = 60;

    private static readonly Color Background = Color.FromArgb(32, 32, 32);
    private static readonly Color Hover = Color.FromArgb(70, 70, 70);
    private static readonly Color On = Color.FromArgb(79, 140, 255);

    private readonly (AppCommand Command, string Glyph, string Name)[] Items =
    [
        (AppCommand.TogglePin, "", T("Pin on top")),
        (AppCommand.OpenPip, "", T("Picture-in-Picture")),
        (AppCommand.OpacityDown, "\uEC8A", T("More see-through")),
        (AppCommand.OpacityUp, "\uE706", T("More opaque")),
        (AppCommand.ToggleClickThrough, "", T("Click-through")),
        (AppCommand.ToggleDark, "", T("Dark mode")),
        (AppCommand.ToggleGlass, "\uE790", T("Glass mode")),
        (AppCommand.ToggleAnchor, "\uE8B7", T("Anchor")),
        (AppCommand.ToggleDefaultOpacity, "\uE7B3", T("See-through at the default level")),
        (AppCommand.ToggleShrink, "\uE73F", T("Shrink")),
        (AppCommand.ToggleCrop, "\uE7A8", T("Crop")),
        (AppCommand.SaveWindowSetup, "\uE74E", T("Remember this setup for the program")),
    ];

    private readonly Func<nint, bool> _canUse;
    private readonly Func<AppCommand, nint, bool> _isOn;
    private readonly Action<AppCommand, nint> _run;
    private readonly Dictionary<AppCommand, Button> _buttons = [];
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = Interval };
    private nint _window;
    private WindowIdentity? _identity;
    private int _hoverTime;
    private Rectangle _targetBounds;
    private readonly ToolTip _tips = new();

    public WindowToolbar(Func<nint, bool> canUse, Func<AppCommand, nint, bool> isOn, Action<AppCommand, nint> run)
    {
        _canUse = canUse;
        _isOn = isOn;
        _run = run;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Text = T("WinScreen toolbar");
        ClientSize = new Size(ButtonSize * Items.Length, ButtonSize);

        for (int i = 0; i < Items.Length; i++)
        {
            (AppCommand command, string glyph, string name) = Items[i];
            var button = new Button
            {
                Text = glyph,
                Font = new Font("Segoe MDL2 Assets", 12),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Background,
                Bounds = new Rectangle(i * ButtonSize, 0, ButtonSize, ButtonSize),
                TabStop = false,
                AccessibleName = name,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Hover;
            button.Click += (_, _) =>
            {
                if (!TargetIsCurrent()) { Hide(); return; }
                _run(command, _window);
                ShowStates();
            };
            _tips.SetToolTip(button, name);
            _buttons[command] = button;
            Controls.Add(button);
        }

        _watch.Tick += (_, _) => Watch();
    }

    /// <summary>The window the toolbar is showing for, or none.</summary>
    public nint Window => Visible ? _window : 0;

    public void Enable(bool on)
    {
        _watch.Enabled = on;
        if (!on)
            Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Watch()
    {
        Point cursor = Cursor.Position;
        if (Visible)
        {
            bool overToolbar = Rectangle.Inflate(Bounds, 12, 12).Contains(cursor);
            if (!overToolbar || !TargetIsCurrent())
                Hide();
            return;
        }

        nint foreground = User32.GetForegroundWindow();
        if (!_canUse(foreground) || !User32.GetWindowRect(foreground, out Rect r))
        {
            _hoverTime = 0;
            return;
        }

        var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (!TitleBarMiddle(bounds).Contains(cursor))
        {
            _hoverTime = 0;
            return;
        }

        _hoverTime += Interval;
        if (_hoverTime < HoverDelayMilliseconds)
            return;

        _hoverTime = 0;
        try { _identity = WindowIdentity.Capture(foreground); }
        catch (WindowOperationException) { return; }
        _window = foreground;
        _targetBounds = bounds;
        Location = new Point(bounds.Left + (bounds.Width - Width) / 2, bounds.Top);
        ShowStates();
        Show();
    }

    /// <summary>The middle of the title bar, away from the tabs and the window buttons.</summary>
    private static Rectangle TitleBarMiddle(Rectangle window)
    {
        return new Rectangle(window.Left + window.Width / 4, window.Top, window.Width / 2, TitleBarHeight);
    }

    private void ShowStates()
    {
        foreach ((AppCommand command, Button button) in _buttons)
            button.ForeColor = _isOn(command, _window) ? On : Color.White;
    }

    private bool TargetIsCurrent() => _identity?.Matches() == true &&
        User32.GetForegroundWindow() == _window && _canUse(_window) &&
        User32.GetWindowRect(_window, out var current) &&
        Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom) == _targetBounds;
}
