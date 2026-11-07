using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>Temporary focus rights and menu lifetime. Created only when a menu is requested.</summary>
internal sealed class WindowMenuHost : Form
{
    private const int FirstHotkey = 0x5760, EscapeHotkey = 0x5780;
    private readonly ContextMenuStrip _menu;
    private readonly nint _previous;
    private readonly Point _point;
    private readonly List<int> _claimed = [];
    private readonly System.Windows.Forms.Timer _deadline = new() { Interval = 150 };
    private bool _shown, _closing;
    internal bool ForceFallback;
    internal bool NeedsHook;
    internal Action? FallbackOpened;

    public WindowMenuHost(ContextMenuStrip menu, Point point)
    {
        _menu = menu;
        _point = point;
        _previous = User32.GetForegroundWindow();
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(point, new Size(1, 1));
        Opacity = 0;
        Text = "WinScreen.WindowMenu";
        _menu.Closed += (_, _) => Finish();
        _deadline.Tick += (_, _) => ShowMenu(false);
    }

    protected override bool ShowWithoutActivation => true;

    public void Open()
    {
        _ = Handle;
        if (!ForceFallback)
        {
            // Claim every modifier combination before injecting: releasing a gesture modifier mid-flight
            // must never let the otherwise unassigned key reach the application beneath the menu.
            foreach (byte key in new byte[] { 0xE8, 0xE9, 0xEA })
            {
                bool registered = true;
                for (uint modifiers = 0; modifiers < 16; modifiers++)
                {
                    int id = FirstHotkey + (int)modifiers;
                    if (!User32.RegisterHotKey(Handle, id, modifiers | User32.MOD_NOREPEAT, key)) { registered = false; break; }
                    _claimed.Add(id);
                }
                if (registered)
                {
                    _deadline.Start();
                    keybd_event(key, 0, 0, 0);
                    keybd_event(key, 0, 2, 0);
                    return;
                }
                ReleaseKeys();
            }
        }
        ShowMenu(false);
    }

    private void ReleaseKeys()
    {
        foreach (int id in _claimed) User32.UnregisterHotKey(Handle, id);
        _claimed.Clear();
    }

    private void ShowMenu(bool earned)
    {
        if (_shown || _closing) return;
        _shown = true;
        _deadline.Stop();
        // Keep reservations until close: an injected key queued on another input desktop stays harmless.
        Show();
        bool activated = earned && User32.SetForegroundWindow(Handle);
        if (!activated)
        {
            User32.RegisterHotKey(Handle, EscapeHotkey, User32.MOD_NOREPEAT, (uint)Keys.Escape);
            NeedsHook = true;
            FallbackOpened?.Invoke();
        }
        _menu.Show(this, PointToClient(_point));
    }

    internal void OutsidePress(Point point)
    {
        if (!_shown || ContainsPoint(_menu, point)) return;
        _menu.Close(ToolStripDropDownCloseReason.AppClicked);
    }

    private static bool ContainsPoint(ToolStripDropDown menu, Point point) => menu.Visible &&
        (menu.Bounds.Contains(point) || menu.Items.OfType<ToolStripMenuItem>()
            .Any(item => item.HasDropDownItems && ContainsPoint(item.DropDown, point)));

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == User32.WM_HOTKEY)
        {
            if ((int)m.WParam == EscapeHotkey) _menu.Close(ToolStripDropDownCloseReason.Keyboard);
            else if ((int)m.WParam is >= FirstHotkey and < FirstHotkey + 16) ShowMenu(true);
            return;
        }
        base.WndProc(ref m);
    }

    private void Finish()
    {
        if (_closing) return;
        _closing = true;
        _deadline.Stop();
        nint foreground = User32.GetForegroundWindow();
        bool returnFocus = foreground == Handle || foreground == _menu.Handle;
        ReleaseKeys();
        User32.UnregisterHotKey(Handle, EscapeHotkey);
        Hide();
        if (returnFocus && User32.IsWindow(_previous)) User32.SetForegroundWindow(_previous);
        BeginInvoke(Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _deadline.Stop();
        if (_menu.Visible) _menu.Close();
        ReleaseKeys();
        User32.UnregisterHotKey(Handle, EscapeHotkey);
        if (User32.GetForegroundWindow() == Handle && User32.IsWindow(_previous))
            User32.SetForegroundWindow(_previous);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _deadline.Dispose();
            if (IsHandleCreated) { ReleaseKeys(); User32.UnregisterHotKey(Handle, EscapeHotkey); }
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
