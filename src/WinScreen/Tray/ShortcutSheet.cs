using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Tray;

internal sealed record ShortcutSheetGroup(string Title, IReadOnlyList<(string Name, string Keys)> Rows);

// Ctrl and Alt held by themselves for a moment, and nothing else pressed meanwhile: AltGr typing and Ctrl+Alt+wheel
// (the opacity wheel) never get that far. Fed the keys as raw input sees them, with the time each came.
internal sealed class ShortcutSheetTrigger
{
    internal const int HoldMilliseconds = 1000;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    internal enum Step { Nothing, Wait, Hide }

    private bool _ctrl, _alt, _spoiled;
    private long _since;

    internal bool Shown { get; private set; }

    internal Step Key(int key, bool down, long at)
    {
        bool ctrl = key is VK_CONTROL or VK_LCONTROL or VK_RCONTROL, alt = key is VK_MENU or VK_LMENU or VK_RMENU;
        if (!ctrl && !alt)
        {
            if (!down || !_ctrl && !_alt) return Step.Nothing;
            _spoiled = true; // A shortcut is being pressed, or Ctrl+Alt is part of something else.
            return HideIfShown();
        }
        bool both = _ctrl && _alt;
        if (ctrl) _ctrl = down;
        else _alt = down;
        if (!_ctrl && !_alt) _spoiled = false;
        if (_ctrl && _alt)
        {
            if (both || _spoiled) return Step.Nothing; // A held key repeats.
            _since = at;
            return Step.Wait;
        }
        return HideIfShown();
    }

    internal Step Mouse()
    {
        if (_ctrl || _alt) _spoiled = true;
        return HideIfShown();
    }

    internal bool Due(long at) => _ctrl && _alt && !_spoiled && !Shown && at - _since >= HoldMilliseconds;

    internal void Showing() => Shown = true;

    // A key Windows takes away before it comes up (Ctrl+Alt+Del, locking the PC) is never seen going up here: what is
    // really held when the moment comes puts that right.
    internal void Correct(bool ctrl, bool alt)
    {
        if (!ctrl) _ctrl = false;
        if (!alt) _alt = false;
        if (!_ctrl && !_alt) _spoiled = false;
    }

    private Step HideIfShown()
    {
        if (!Shown) return Step.Nothing;
        Shown = false;
        return Step.Hide;
    }
}

internal static class ShortcutSheetContents
{
    // Only shortcuts that work now, in Settings' groups and order, then the ways to act with the mouse that are on.
    internal static IReadOnlyList<ShortcutSheetGroup> Of(AppSettings settings, Func<AppCommand, bool> works)
    {
        var groups = new List<ShortcutSheetGroup>();
        foreach (ActionGroup group in Enum.GetValues<ActionGroup>())
        {
            var rows = CommandCatalog.All
                .Where(command => CommandCatalog.GroupOf(command) == group && works(command) &&
                                  settings.Hotkeys.GetValueOrDefault(command) is { Length: > 0 })
                .Select(command => (CommandCatalog.Name(command).Replace("&", ""), settings.Hotkeys[command]))
                .ToList();
            if (rows.Count > 0) groups.Add(new ShortcutSheetGroup(SettingsWindow.GroupName(group), rows));
        }
        var mouse = new List<(string, string)>();
        if (settings.ModifierDrag)
            mouse.Add((T("features.moveResize"), string.Format(T("features.dragShortcut"), settings.ModifierKey == ModifierKey.Win ? "Win" : "Alt")));
        if (settings.WindowMenu)
            mouse.Add((T("features.windowMenu"), settings.WindowMenuGesture switch
            {
                WindowMenuGesture.CtrlShiftRightClick => T("gestures.menu.ctrlShiftRight"),
                WindowMenuGesture.CtrlMiddleClick => T("gestures.menu.ctrlMiddle"),
                _ => T("gestures.menu.ctrlRight"),
            }));
        if (settings.OpacityWheel) mouse.Add((T("features.opacityWheel"), T("features.wheelShortcut")));
        if (mouse.Count > 0) groups.Add(new ShortcutSheetGroup(T("actions.group.mouse"), mouse));
        return groups;
    }
}

// Watches the keys from raw input (AltKeyWatch, the one keyboard registration a process can have) and shows the sheet
// on the UI thread of the control it is given.
internal sealed class ShortcutSheet : IDisposable
{
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private readonly object _gate = new();
    private readonly ShortcutSheetTrigger _trigger = new();
    private readonly System.Threading.Timer _timer;
    private readonly Control _ui;
    private readonly Func<bool> _allowed;
    private readonly Func<IReadOnlyList<ShortcutSheetGroup>> _contents;
    private ShortcutSheetWindow? _window;
    private volatile bool _disposed;

    internal ShortcutSheet(Control ui, Func<bool> allowed, Func<IReadOnlyList<ShortcutSheetGroup>> contents)
    {
        _ui = ui;
        _allowed = allowed;
        _contents = contents;
        _timer = new System.Threading.Timer(_ => Check());
        AltKeyWatch.Key += OnKey;
        AltKeyWatch.Start();
    }

    internal bool Showing => _window is { Visible: true };

    // Each sheet is a new window, for whatever must leave WinScreen's own windows alone (Dark mode).
    internal Action<nint>? Opened { get; init; }

    private void OnKey(int key, bool down)
    {
        ShortcutSheetTrigger.Step step;
        lock (_gate) step = _trigger.Key(key, down, Environment.TickCount64);
        if (step == ShortcutSheetTrigger.Step.Wait) _timer.Change(ShortcutSheetTrigger.HoldMilliseconds + 20, Timeout.Infinite);
        else if (step == ShortcutSheetTrigger.Step.Hide) Post(Close);
    }

    // The mouse was used with the keys held: they are for something else.
    internal void Cancel()
    {
        ShortcutSheetTrigger.Step step;
        lock (_gate) step = _trigger.Mouse();
        if (step == ShortcutSheetTrigger.Step.Hide) Post(Close);
    }

    private static bool Held(int key) => (User32.GetAsyncKeyState(key) & 0x8000) != 0;

    private void Check()
    {
        if (_disposed) return;
        bool ctrl = Held(VK_CONTROL), alt = Held(VK_MENU);
        bool button = Held(VK_LBUTTON) || Held(VK_RBUTTON) || Held(VK_MBUTTON);
        lock (_gate)
        {
            _trigger.Correct(ctrl, alt);
            if (button) _trigger.Mouse();
            if (!_trigger.Due(Environment.TickCount64)) return;
            _trigger.Showing();
        }
        Post(Open);
    }

    private void Post(Action action)
    {
        if (_disposed || !_ui.IsHandleCreated) return;
        try { _ui.BeginInvoke(action); }
        catch (InvalidOperationException closing) { Diagnostics.Log.Swallowed(closing); }
    }

    private void Open()
    {
        if (_disposed || !_allowed()) return;
        IReadOnlyList<ShortcutSheetGroup> groups = _contents();
        if (groups.Count == 0) return;
        _window?.Dispose();
        _window = new ShortcutSheetWindow(groups);
        _window.ShowAround(Cursor.Position);
        Opened?.Invoke(_window.Handle);
        Diagnostics.Log.Event("shortcutSheet.shown");
    }

    private void Close()
    {
        _window?.Dispose();
        _window = null;
    }

    public void Dispose()
    {
        _disposed = true;
        AltKeyWatch.Key -= OnKey;
        _timer.Dispose();
        _window?.Dispose();
        _window = null;
    }
}

// Never active and never in the way: clicks pass through it to whatever is under it.
internal sealed class ShortcutSheetWindow : Form
{
    private readonly IReadOnlyList<ShortcutSheetGroup> _groups;
    private readonly List<(Rectangle Place, string Text, Font Font, Color Color, bool Keys)> _parts = [];
    private int _dpi = Dpi.Default;

    internal ShortcutSheetWindow(IReadOnlyList<ShortcutSheetGroup> groups)
    {
        _groups = groups;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = T("actions.sheet.title");
        BackColor = MenuStyle.Back;
        DoubleBuffered = true;
        Opacity = 0.97;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT);
            return parameters;
        }
    }

    internal IReadOnlyList<string> Lines => _parts.Select(part => part.Text).ToList();

    private int Scale(int value) => Dpi.Scale(value, _dpi);

    // Laid out for the display it shows on before it is made, so Windows never has to scale it there.
    internal void ShowAround(Point point)
    {
        Screen screen = Screen.FromPoint(point);
        nint monitor = User32.MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        _dpi = Math.Max(Dpi.Default, Dpi.ScaleOf(monitor) * Dpi.Default / 100);
        Size size = Arrange(screen.WorkingArea);
        Rectangle area = screen.WorkingArea;
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
        Show();
        int round = 2; // DWMWCP_ROUND
        Dwmapi.DwmSetWindowAttribute(Handle, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, round, sizeof(int));
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        Windows.TopLayer.Raise(Handle);
    }

    // Groups go down a column and on into the next, as few columns as fit the display's height.
    private Size Arrange(Rectangle area)
    {
        Font title = MenuStyle.ForDpi(MenuStyle.MenuTitleFont, _dpi);
        Font heading = MenuStyle.ForDpi(MenuStyle.MenuHeaderFont, _dpi);
        Font text = MenuStyle.ForDpi(MenuStyle.MenuFont, _dpi);
        Font small = MenuStyle.ForDpi(MenuStyle.SmallFont, _dpi);
        const TextFormatFlags Measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        Size Of(string words, Font font) => DirectText.MeasureText(words, font, Size.Empty, Measure);
        int pad = Scale(20), row = Math.Max(Of("Ag", text).Height + Scale(12), Scale(30)), headingRow = Of("Ag", heading).Height + Scale(22);
        int keyPad = Scale(8), gap = Scale(32), columnGap = Scale(36);

        var entries = new List<(ShortcutSheetGroup Group, int Row)>(); // Row -1 is the group's heading.
        foreach (ShortcutSheetGroup group in _groups)
        {
            entries.Add((group, -1));
            for (int index = 0; index < group.Rows.Count; index++) entries.Add((group, index));
        }
        int Height((ShortcutSheetGroup Group, int Row) entry) => entry.Row < 0 ? headingRow : row;
        int nameWidth = 0, keysWidth = 0;
        foreach (ShortcutSheetGroup group in _groups)
            foreach ((string name, string keys) in group.Rows)
            {
                nameWidth = Math.Max(nameWidth, Of(name, text).Width);
                keysWidth = Math.Max(keysWidth, Of(keys, text).Width + keyPad * 2);
            }
        int columnWidth = Math.Max(nameWidth + gap + keysWidth, _groups.Max(group => Of(group.Title, heading).Width));
        Size titleSize = Of(T("actions.sheet.title"), title), footerSize = Of(T("actions.sheet.footer"), small);
        int room = area.Height * 8 / 10 - pad * 2 - titleSize.Height - footerSize.Height - Scale(28);
        int total = entries.Sum(Height);
        int columns = 1;
        while (columns < 4 && (Split(columns).Max(column => column.Sum(Height)) > room) &&
               (columns + 1) * (columnWidth + columnGap) <= area.Width * 9 / 10)
            columns++;
        List<List<(ShortcutSheetGroup Group, int Row)>> Split(int count)
        {
            int target = (int)Math.Ceiling(total / (double)count);
            var split = new List<List<(ShortcutSheetGroup, int)>> { new() };
            for (int index = 0; index < entries.Count; index++)
            {
                List<(ShortcutSheetGroup, int)> current = split[^1];
                int used = current.Sum(Height);
                // A heading never ends a column on its own.
                int needs = Height(entries[index]) + (entries[index].Row < 0 && index + 1 < entries.Count ? row : 0);
                if (current.Count > 0 && used + needs > target && split.Count < count) split.Add([]);
                split[^1].Add(entries[index]);
            }
            return split;
        }

        _parts.Clear();
        int top = pad;
        _parts.Add((new Rectangle(pad, top, Math.Max(titleSize.Width, columnWidth), titleSize.Height), T("actions.sheet.title"), title, MenuStyle.Text, false));
        top += titleSize.Height + Scale(14);
        int left = pad, tallest = 0;
        foreach (List<(ShortcutSheetGroup Group, int Row)> column in Split(columns))
        {
            int y = top;
            foreach ((ShortcutSheetGroup group, int index) in column)
            {
                if (index < 0)
                {
                    // Space above a group that follows another; the words sit at the bottom of the room.
                    _parts.Add((new Rectangle(left, y + Scale(10), columnWidth, headingRow - Scale(12)), group.Title, heading, MenuStyle.Muted, false));
                    y += headingRow;
                    continue;
                }
                (string name, string keys) = group.Rows[index];
                _parts.Add((new Rectangle(left, y, nameWidth, row), name, text, MenuStyle.Text, false));
                int keysAt = left + columnWidth - (Of(keys, text).Width + keyPad * 2);
                _parts.Add((new Rectangle(keysAt, y + Scale(4), Of(keys, text).Width + keyPad * 2, row - Scale(8)), keys, text, MenuStyle.Text, true));
                y += row;
            }
            tallest = Math.Max(tallest, y - top);
            left += columnWidth + columnGap;
        }
        int width = left - columnGap + pad;
        int footerTop = top + tallest + Scale(14);
        _parts.Add((new Rectangle(pad, footerTop, width - pad * 2, footerSize.Height), T("actions.sheet.footer"), small, MenuStyle.Muted, false));
        return new Size(Math.Max(width, titleSize.Width + pad * 2), footerTop + footerSize.Height + pad);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var line = new Pen(MenuStyle.Line)) g.DrawRectangle(line, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        foreach ((Rectangle place, string words, Font font, Color color, bool keys) in _parts)
        {
            if (keys)
            {
                using var path = Settings.SettingsStyle.Rounded(place, Scale(4));
                using var fill = new SolidBrush(MenuStyle.Hover);
                g.FillPath(fill, path);
                DirectText.DrawText(g, words, font, place, color, Flags | TextFormatFlags.HorizontalCenter);
            }
            else DirectText.DrawText(g, words, font, place, color, Flags);
        }
    }
}
