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
    private bool _byMinimize;
    private int _awayTime;
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
            StayOrHide(cursor);
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
        // The chosen buttons can all be unavailable, such as only Dark mode with Experimental off.
        if (!HasButtons)
        {
            _hoverTime = 0;
            return;
        }

        var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        // With Either, resting on the buttons counts as the minimize trigger; anywhere else on the bar's spot, the
        // title bar.
        bool minimizeTrigger = _preferences.Trigger == ToolbarTrigger.MinimizeButton ||
                               (_preferences.Trigger == ToolbarTrigger.Either && PlaceFor(window, dpi, true).Zone.Contains(cursor));
        (Rectangle bar, Rectangle zone) = PlaceFor(window, dpi, minimizeTrigger);
        int still = Dpi.Scale(StillPixels, dpi);
        bool moved = Math.Abs(cursor.X - _hoverPoint.X) > still ||
                     Math.Abs(cursor.Y - _hoverPoint.Y) > still;
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
        _byMinimize = minimizeTrigger;
        _awayTime = 0;
        Location = bar.Location;
        ShowStates();
        Show();
        KeepAbove(Handle, foreground);
    }

    private (Rectangle Bar, Rectangle Zone) PlaceFor(Rectangle window, int dpi, bool minimizeTrigger)
    {
        Rectangle area = Screen.FromRectangle(window).WorkingArea;
        int reserved = Dpi.Scale(CaptionButtonsWidth, dpi);
        int caption = Dpi.Scale(CaptionHeight, dpi);
        var bar = new Rectangle(ToolbarGeometry.PlaceAt(window, Size, area, reserved, caption, _preferences.Placement), Size);
        if (!minimizeTrigger) return (bar, ToolbarGeometry.HoverZone(window, bar, area, reserved, caption));
        bar.Y = ToolbarGeometry.BelowCaption(window, Height, area, caption);
        return (bar, ToolbarGeometry.CaptionButtonsZone(window, area, reserved, caption));
    }

    // The owner, 28 Nov: one look with the pointer a few pixels off the bar was enough to close it.
    private void StayOrHide(Point cursor)
    {
        if (!TargetIsCurrent(sameBounds: false))
        {
            HideFor(ToolbarHide.WindowChanged);
            return;
        }

        bool pressed = MouseButtons != MouseButtons.None;
        if (User32.GetWindowRect(_window, out Rect r) &&
            Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) is var now && now != _targetBounds)
        {
            // Dragged or resized by hand, the bar would be in the way; moved by the app or a button, it goes along.
            if (pressed)
            {
                HideFor(ToolbarHide.Dragged);
                return;
            }

            int dpi = Dpi.Of(_window);
            Arrange(dpi);
            (Rectangle bar, _hoverZone) = PlaceFor(now, dpi, _byMinimize);
            _targetBounds = now;
            Location = bar.Location;
            KeepAbove(Handle, _window);
        }

        int margin = Dpi.Scale(KeepOpenMargin, Dpi.Of(_window));
        bool nearby = Rectangle.Inflate(Rectangle.Union(Bounds, _hoverZone), margin, margin).Contains(cursor);
        _awayTime = nearby ? 0 : _awayTime + Interval;
        if (ToolbarGeometry.Leaves(nearby, _awayTime, pressed) is { } reason)
            HideFor(reason);
    }

    private void HideFor(ToolbarHide reason)
    {
        Hide();
        Diagnostics.Log.Event("toolbar.hide", ("reason", reason), ("program", Diagnostics.LogValue.ProgramOf(_window)));
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
        if (Answers(window, cursor).Contains(wanted)) return true;
        // A browser's tabs answer "content" (the owner, 28 Nov: no bar over a Chrome tab), yet they share their row
        // with the window's own minimize, maximize and close buttons: that row is the title bar.
        return trigger == ToolbarTrigger.TitleBar && Answer(window, cursor) == User32.HTCLIENT && InButtonRow(window, cursor.Y);
    }

    private static bool InButtonRow(nint window, int y)
    {
        if (!User32.GetWindowRect(window, out Rect r)) return false;
        int dpi = Dpi.Of(window);
        int right = Math.Min(r.Right, Screen.FromHandle(window).Bounds.Right) - 1;
        int left = Math.Max(r.Left, right - Dpi.Scale(CaptionButtonsWidth, dpi));
        for (int x = right; x > left; x -= Dpi.Scale(6, dpi))
            if (Answers(window, new Point(x, y)).Any(hit => hit is User32.HTMINBUTTON or User32.HTMAXBUTTON or User32.HTCLOSE))
                return true;
        return false;
    }

    private static IEnumerable<nint> Answers(nint window, Point point)
    {
        yield return Answer(window, point);
        nint under = User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        if (under != 0 && under != window && User32.GetAncestor(under, User32.GA_ROOT) == window)
            yield return Answer(under, point);
    }

    private static nint Answer(nint window, Point point) =>
        User32.SendMessageTimeout(window, User32.WM_NCHITTEST, 0, (nint)((point.Y << 16) | (point.X & 0xFFFF)), AbortIfHung,
            CaptionCheckMilliseconds, out nint hit) != 0 ? hit : -1;

    private void ShowStates()
    {
        foreach ((AppCommand command, Button button) in _buttons)
        {
            Color back = _isOn(command, _window) ? OnBack : Background;
            if (button.BackColor != back) button.BackColor = back;
        }
    }

    private bool TargetIsCurrent(bool sameBounds = true) => _identity?.IsSameWindow() == true &&
                                      User32.GetForegroundWindow() == _window && _canUse(_window) &&
                                      User32.GetWindowRect(_window, out var current) &&
                                      (!sameBounds || Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom) ==
                                      _targetBounds);

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
