using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Anchors;

/// <summary>
/// A small floating button for one window. Left-click brings it forward, right-click opens its menu,
/// middle-click closes the window, and dragging a file over it brings the window forward so the file can
/// be dropped there. The button itself can be dragged anywhere.
/// </summary>
internal sealed class AnchorButton : Form
{
    public const int ButtonSize = 48;

    private const int IconInset = 8;
    private const int IconSize = 32;
    private const int DragThreshold = 4;
    private const int SW_MINIMIZE = 6;

    private readonly nint _target;
    private readonly WindowIdentity _identity;
    private readonly Icon? _icon;
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private readonly Rectangle _iconBounds;
    private readonly ContextMenuStrip _menu = new();
    private Point _pressedAt;
    private bool _pressed;
    private bool _dragged;

    public AnchorButton(nint target)
    {
        _target = target;
        _identity = WindowIdentity.Capture(target);
        _icon = LoadIcon(target);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        int dpi = Dpi.Of(target);
        ClientSize = new Size(Dpi.Scale(ButtonSize, dpi), Dpi.Scale(ButtonSize, dpi));
        _iconBounds = new Rectangle(Dpi.Scale(IconInset, dpi), Dpi.Scale(IconInset, dpi), Dpi.Scale(IconSize, dpi),
            Dpi.Scale(IconSize, dpi));
        AllowDrop = true;
        DoubleBuffered = true;
        Text = T("anchor.titlePrefix") + WindowInfo.Title(target);
        _tip.SetToolTip(this, WindowInfo.Title(target) + Environment.NewLine +
                              T("anchor.controls"));
        _menu.Items.Add(T("window.show"), null, (_, _) => BringForward());
        _menu.Items.Add(T("window.minimize"), null, (_, _) =>
        {
            if (_identity.Matches()) User32.ShowWindow(_target, SW_MINIMIZE);
            else Close();
        });
        _menu.Items.Add(T("anchor.closeWindow"), null, (_, _) => CloseTarget());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(T("anchor.remove"), null, (_, _) => Close());
        ContextMenuStrip = _menu;
    }

    public nint Target => _target;

    /// <summary>True for one the app added for a covered window, which it may also take away.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Automatic { get; set; }
    public bool TargetAlive => _identity.Matches();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<nint>? ShowTarget { get; init; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW);
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawIcon(_icon ?? SystemIcons.Application, _iconBounds);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        _dragged = false;
        _pressedAt = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left)
            return;

        if (_dragged || Math.Abs(e.X - _pressedAt.X) >= DragThreshold || Math.Abs(e.Y - _pressedAt.Y) >= DragThreshold)
        {
            _dragged = true;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                Math.Clamp(Left + e.X - _pressedAt.X, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top + e.Y - _pressedAt.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed || _dragged)
        {
            _pressed = false;
            return;
        }

        _pressed = false;
        switch (e.Button)
        {
            case MouseButtons.Left:
                BringForward();
                break;
            case MouseButtons.Right:
                // ContextMenuStrip provides ordinary right-click and keyboard (Shift+F10) access.
                break;
            case MouseButtons.Middle:
                CloseTarget();
                break;
        }
    }

    private void CloseTarget()
    {
        // Close requests are asynchronous and apps may hide or cancel closing. Dismiss our shortcut
        // independently; never force the app to quit or discard an unsaved-work prompt.
        if (_identity.Matches())
            User32.PostMessage(_target, User32.WM_CLOSE, 0, 0);
        Close();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        BringForward();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon?.Dispose();
            _tip.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BringForward()
    {
        if (!_identity.Matches())
        {
            Close();
            return;
        }

        if (ShowTarget is { } show)
        {
            show(_target);
            return;
        }

        // A standalone Anchor cannot safely reveal a source owned by a view.
        if (WindowInfo.IsConcealed(_target)) return;

        if (User32.IsIconic(_target))
            User32.ShowWindow(_target, User32.SW_RESTORE);
        User32.SetForegroundWindow(_target);
    }

    private static Icon? LoadIcon(nint target)
    {
        User32.GetWindowThreadProcessId(target, out uint processId);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            return path is null ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
