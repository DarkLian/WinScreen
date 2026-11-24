using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Tray;

// Never takes focus, so the window it acts on stays active.
public sealed class WindowToolbar : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int ButtonSize = 34;
    private const int GroupGap = 8;
    private const int GlyphSize = 15;
    private const int CaptionHeight = 34;
    private const int CaptionButtonsWidth = 150;
    private const int KeepOpenMargin = 12;
    private const int StillPixels = 4;
    private const int Interval = 60;
    private const uint AbortIfHung = 0x2;
    private const uint CaptionCheckMilliseconds = 100;

    private static readonly Color Background = Color.FromArgb(32, 32, 32);
    private static readonly Color Hover = Color.FromArgb(70, 70, 70);
    private static readonly Color OnBack = Color.FromArgb(40, 62, 86);

    private readonly Func<nint, bool> _canUse;
    private readonly Func<AppCommand, nint, bool> _isOn;
    private readonly Func<AppCommand, bool> _isAvailable;
    private readonly Action<AppCommand, nint> _run;
    private readonly Dictionary<AppCommand, Button> _buttons = [];
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = Interval };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private nint _window;
    private WindowIdentity? _identity;
    private int _hoverTime;
    private Point _hoverPoint;
    private Rectangle _targetBounds;
    private Rectangle _hoverZone;
    private string _arrangement = "";
    private ToolbarPreferences _preferences = new();
    private bool _enabled;

    public void Configure(ToolbarPreferences preferences)
    {
        _preferences = preferences.Validated();
        _arrangement = "";
        _hoverTime = 0;
        Hide();
        Arrange(DeviceDpi);
        _watch.Enabled = _enabled && _preferences.Buttons.Count > 0;
    }

    public WindowToolbar(Func<nint, bool> canUse, Func<AppCommand, nint, bool> isOn, Func<AppCommand, bool> isAvailable,
        Action<AppCommand, nint> run)
    {
        _canUse = canUse;
        _isOn = isOn;
        _isAvailable = isAvailable;
        _run = run;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Text = T("toolbar.title");

        foreach (AppCommand command in CommandCatalog.ToolbarButtons)
        {
            string name = CommandCatalog.ButtonName(command);
            var button = new Button
            {
                FlatStyle = FlatStyle.Flat,
                ImageAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Background,
                TabStop = false,
                AccessibleName = name,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Hover;
            button.Click += (_, _) =>
            {
                if (!TargetIsCurrent())
                {
                    Hide();
                    return;
                }

                if (CommandCatalog.OpensMenuOnToolbar(command) && OpenMenu is { } open)
                {
                    Rectangle shown = button.RectangleToScreen(button.ClientRectangle);
                    open(command, _window, new Point(shown.Left, shown.Bottom));
                    return;
                }
                _run(command, _window);
                ShowStates();
                // Pin on top puts the window over the bar that is still open on it.
                KeepAbove(Handle, _window);
            };
            _tips.SetToolTip(button, name);
            _buttons[command] = button;
            Controls.Add(button);
        }

        Arrange(Dpi.Default);
        _watch.Tick += (_, _) => Watch();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<AppCommand, nint, Point>? OpenMenu { get; init; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<nint, bool> ByPosition { get; init; } = _ => false;

    internal IReadOnlySet<AppCommand> ShownButtons =>
        _buttons.Keys.Where(command => _preferences.Buttons.Contains(command) && _isAvailable(command)).ToHashSet();

    public nint Window => Visible ? _window : 0;

    internal bool HasButtons { get; private set; }

    public void Enable(bool on)
    {
        _enabled = on;
        _watch.Enabled = on && _preferences.Buttons.Count > 0;
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

    private static char ButtonIcon(AppCommand command) => MenuParts.IconOf(command) ?? MenuParts.More;


    // WinForms has just rescaled the bar on top of the size it was given for that display, so it is laid out again.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _arrangement = "";
        Arrange(e.DeviceDpiNew);
    }

    private void Arrange(int dpi)
    {
        AppCommand[] available = _buttons.Keys.Where(command => _preferences.Buttons.Contains(command) && _isAvailable(command)).ToArray();
        string arrangement = dpi + ":" + string.Join(",", available);
        if (arrangement == _arrangement)
            return;

        _arrangement = arrangement;
        int iconPixels = Dpi.Scale(GlyphSize + 3, dpi);
        int size = Dpi.Scale(ButtonSize, dpi);
        int gap = Dpi.Scale(GroupGap, dpi);
        int left = 0, right = 0;
        bool groupVisible = false;
        foreach ((AppCommand command, Button button) in _buttons)
        {
            // Read from the list: a button reports itself as hidden until the toolbar is on screen.
            bool shown = available.Contains(command);
            button.Image = ColorIcons.Of(ButtonIcon(command), iconPixels);
            button.Visible = shown;
            if (shown)
            {
                groupVisible = true;
                button.Bounds = new Rectangle(left, 0, size, size);
                left += size;
                right = left;
            }

            if (CommandCatalog.EndsGroup(command))
            {
                if (groupVisible) left += gap;
                groupVisible = false;
            }
        }

        HasButtons = available.Length > 0;
        ClientSize = new Size(Math.Max(1, right), size);
    }

    private void Watch()
    {
        Point cursor = Cursor.Position;
        if (Visible)
        {
            bool nearby = Rectangle.Inflate(Rectangle.Union(Bounds, _hoverZone), KeepOpenMargin, KeepOpenMargin)
                .Contains(cursor);
            if (!nearby || !TargetIsCurrent())
                Hide();
            return;
        }

        nint foreground = User32.GetForegroundWindow();
        if (MouseButtons != MouseButtons.None || !_canUse(foreground) ||
            !User32.GetWindowRect(foreground, out Rect r))
        {
            _hoverTime = 0;
            return;
        }

        int dpi = Dpi.Of(foreground);
        Arrange(dpi);
        // The chosen buttons can all be unavailable, such as only effects with Experimental off.
        if (!HasButtons)
        {
            _hoverTime = 0;
            return;
        }

        var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        Rectangle area = Screen.FromRectangle(window).WorkingArea;
        int reserved = Dpi.Scale(CaptionButtonsWidth, dpi);
        int caption = Dpi.Scale(CaptionHeight, dpi);
        var bar = new Rectangle(ToolbarGeometry.PlaceAt(window, Size, area, reserved, caption, _preferences.Placement), Size);
        Rectangle buttonsZone = ToolbarGeometry.CaptionButtonsZone(window, area, reserved, caption);
        // With Either, resting on the buttons counts as the minimize trigger; anywhere else on the bar's spot, the
        // title bar.
        bool minimizeTrigger = _preferences.Trigger == ToolbarTrigger.MinimizeButton ||
                               (_preferences.Trigger == ToolbarTrigger.Either && buttonsZone.Contains(cursor));
        if (minimizeTrigger) bar.Y = ToolbarGeometry.BelowCaption(window, Height, area, caption);
        Rectangle zone = minimizeTrigger ? buttonsZone : ToolbarGeometry.HoverZone(window, bar, area, reserved, caption);
        bool moved = Math.Abs(cursor.X - _hoverPoint.X) > StillPixels ||
                     Math.Abs(cursor.Y - _hoverPoint.Y) > StillPixels;
        if (!zone.Contains(cursor) || moved)
        {
            _hoverPoint = cursor;
            _hoverTime = 0;
            return;
        }

        _hoverTime += Interval;
        if (_hoverTime < _preferences.HoverDelayMilliseconds)
            return;

        _hoverTime = 0;
        // An app that draws its own top row answers "content" there: a rule can say to go by position alone.
        if (!ByPosition(foreground) && !MatchesTrigger(foreground, cursor, minimizeTrigger ? ToolbarTrigger.MinimizeButton : ToolbarTrigger.TitleBar))
            return;

        try
        {
            _identity = WindowIdentity.Observe(foreground);
        }
        catch (WindowOperationException)
        {
            return;
        }

        _window = foreground;
        _targetBounds = window;
        _hoverZone = zone;
        Location = bar.Location;
        ShowStates();
        Show();
        KeepAbove(Handle, foreground);
    }

    // Asks the window itself, so content drawn at an app's top doesn't count as its title bar. Some apps draw their
    // title bar in a child window (File Explorer, Windows Terminal) and the main window answers "content" all along its
    // top, so the window under the pointer is asked too.
    internal static bool MatchesTrigger(nint window, Point cursor, ToolbarTrigger trigger)
    {
        // The minimize button where Windows draws it, when Windows says: the app's own answer can be off by a button.
        if (trigger == ToolbarTrigger.MinimizeButton &&
            WindowInfo.CaptionButtonBounds(window, WindowInfo.CaptionButton.Minimize) is { } drawn)
            return drawn.Contains(cursor);
        nint wanted = trigger == ToolbarTrigger.MinimizeButton ? User32.HTMINBUTTON : User32.HTCAPTION;
        nint position = (nint)((cursor.Y << 16) | (cursor.X & 0xFFFF));
        if (Answers(window) == wanted) return true;
        nint under = User32.WindowFromPoint(new NativePoint { X = cursor.X, Y = cursor.Y });
        return under != 0 && under != window && User32.GetAncestor(under, User32.GA_ROOT) == window && Answers(under) == wanted;

        nint Answers(nint asked) => User32.SendMessageTimeout(asked, User32.WM_NCHITTEST, 0, position, AbortIfHung,
            CaptionCheckMilliseconds, out nint hit) != 0 ? hit : -1;
    }

    private void ShowStates()
    {
        foreach ((AppCommand command, Button button) in _buttons)
        {
            Color back = _isOn(command, _window) ? OnBack : Background;
            if (button.BackColor != back) button.BackColor = back;
        }
    }

    private bool TargetIsCurrent() => _identity?.IsSameWindow() == true &&
                                      User32.GetForegroundWindow() == _window && _canUse(_window) &&
                                      User32.GetWindowRect(_window, out var current) &&
                                      Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom) ==
                                      _targetBounds;

    // A pinned window is topmost too, and Show alone can leave the bar under it. Windows also doesn't let WinScreen,
    // not being in front, put the bar over the pinned window in front, so then the window goes just under the bar
    // instead: going lower is always allowed.
    internal static void KeepAbove(nint bar, nint window)
    {
        WinScreen.Windows.TopLayer.Raise(bar);
        if (IsAbove(window, bar))
            User32.SetWindowPos(window, bar, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private static bool IsAbove(nint upper, nint lower)
    {
        for (nint w = User32.GetWindow(lower, 3); w != 0; w = User32.GetWindow(w, 3)) // GW_HWNDPREV
            if (w == upper) return true;
        return false;
    }
}
