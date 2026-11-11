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
    private Rectangle _iconBounds;
    private bool _marksView;
    private bool _excluded;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 100 };
    private Pip.PipHoverPreview? _preview;
    private long _hoverSince;
    private int _size = ButtonSize;
    private string? _title;
    private Point _pressedAt;
    private bool _pressed;
    private bool _dragged;

    public AnchorButton(nint target)
    {
        _target = target;
        _identity = WindowIdentity.Capture(target);
        (_icon, Program) = LoadIcon(target);
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
        AccessibleDescription = T("anchor.controls"); // Said by a screen reader; nothing pops up on hover.
        _menu.Items.Add(T("window.show"), null, (_, _) => BringForward());
        _menu.Items.Add(T("window.minimize"), null, (_, _) =>
        {
            if (_identity.Matches()) User32.ShowWindow(_target, SW_MINIMIZE);
            else Close();
        });
        _menu.Items.Add(T("anchor.closeWindow"), null, (_, _) => CloseTarget());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(T("anchor.remove"), null, (_, _) => Close());
        Tray.MenuRenderer.Apply(_menu);
        ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => DropPreview();
        _previewTimer.Tick += (_, _) => FollowPreview();
    }

    /// <summary>The program the window belongs to, which decides its icon. Null when it couldn't be read.</summary>
    public string? Program { get; }

    /// <summary>How long the pointer rests before the window's live picture shows; 0 for never.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int PreviewDelay { get; set; }

    /// <summary>The short title shown beside the icon, for an Anchor whose icon others share. Null shows the icon alone.</summary>
    internal string? Title => _title;

    /// <summary>True while the window's live picture shows beside the Anchor.</summary>
    internal bool Previewing => _preview is not null;

    public nint Target => _target;

    /// <summary>True for one the app added for a covered window, which it may also take away.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Automatic { get; set; }

    /// <summary>True once the person has dragged it. It stays where they put it.</summary>
    public bool Placed { get; private set; }

    /// <summary>Sets how it looks now. Skips anything unchanged, because this runs after every window change.</summary>
    internal void Apply(int size, double opacity, bool light, bool marksView, bool excludeFromCapture, string? title = null)
    {
        int width = title is null ? size : size * 3;
        if (ClientSize.Width != width || ClientSize.Height != size || _size != size)
        {
            // An Anchor that grows for its title keeps the edge nearest its side of the screen where it was.
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            bool keepRight = Left + Width / 2 > area.Left + area.Width / 2;
            int right = Right;
            _size = size;
            ClientSize = new Size(width, size);
            if (keepRight) Left = right - Width;
            int inset = Math.Max(2, size / 6);
            _iconBounds = new Rectangle(inset, inset, size - 2 * inset, size - 2 * inset);
            Invalidate();
        }

        if (_title != title)
        {
            _title = title;
            Invalidate();
        }

        if (Math.Abs(Opacity - opacity) > 0.001) Opacity = opacity;
        Color back = light ? Color.FromArgb(240, 240, 240) : Color.FromArgb(32, 32, 32);
        if (BackColor != back) BackColor = back;
        if (_marksView != marksView)
        {
            _marksView = marksView;
            AccessibleDescription = T("anchor.controls") + (marksView ? " " + T("anchors.controls.view") : "");
            Invalidate();
        }

        if (_excluded != excludeFromCapture && IsHandleCreated)
        {
            _excluded = excludeFromCapture;
            User32.SetWindowDisplayAffinity(Handle, excludeFromCapture ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE);
        }
    }
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
        if (_title is not null)
        {
            int left = _size - _iconBounds.Left / 2;
            var words = new Rectangle(left, 2, ClientSize.Width - left - _iconBounds.Left, ClientSize.Height - 4);
            using var font = new Font(Tray.MenuStyle.Font.FontFamily, Math.Max(7f, _size / 4.4f), GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, _title, font, words, BackColor.GetBrightness() > 0.5 ? Color.Black : Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        if (_marksView)
        {
            // A notch in the corner, so a window with a view reads differently without relying on colour.
            int notch = Math.Max(6, _size / 4);
            int right = ClientSize.Width, bottom = ClientSize.Height;
            using var brush = new SolidBrush(BackColor.GetBrightness() > 0.5 ? Color.Black : Color.White);
            e.Graphics.FillPolygon(brush, [new Point(right, bottom - notch), new Point(right, bottom), new Point(right - notch, bottom)]);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverSince = Environment.TickCount64;
        if (PreviewDelay > 0) _previewTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        DropPreview();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) DropPreview();
    }

    /// <summary>
    /// Shows the window's live picture beside the Anchor once the pointer has rested on it. A minimized window has
    /// no picture to show, so the title in its tooltip is all there is.
    /// </summary>
    private void FollowPreview()
    {
        bool resting = !IsDisposed && Visible && ClientRectangle.Contains(PointToClient(Cursor.Position)) && !_pressed &&
                       !_menu.Visible && PreviewDelay > 0 && _identity.Matches() && !User32.IsIconic(_target) &&
                       !WindowInfo.IsConcealed(_target);
        if (!resting)
        {
            DropPreview();
            return;
        }
        if (_preview is not null || Environment.TickCount64 - _hoverSince < PreviewDelay) return;
        try
        {
            if (!User32.GetWindowRect(_target, out Rect r) || r.Right <= r.Left || r.Bottom <= r.Top) return;
            var source = new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            Size size = Pip.PipGeometry.FitLongSide(Dpi.Scale(360, DeviceDpi), source, area.Size);
            _preview = new Pip.PipHoverPreview(this, _target);
            if (_excluded) User32.SetWindowDisplayAffinity(_preview.Handle, User32.WDA_EXCLUDEFROMCAPTURE);
            _preview.UpdatePreview(source, Pip.PipPreviewGeometry.Place(Bounds, size, area));
            TopLayer.Raise(_preview.Handle);
        }
        catch (WindowOperationException)
        {
            DropPreview(); // A window that refuses a picture keeps its Anchor working as before.
        }
    }

    private void DropPreview()
    {
        _previewTimer.Stop();
        _preview?.Dispose();
        _preview = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        DropPreview();
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
            Placed = true;
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
            DropPreview();
            _previewTimer.Dispose();
            _icon?.Dispose();
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

    private static (Icon? Icon, string? Program) LoadIcon(nint target)
    {
        User32.GetWindowThreadProcessId(target, out uint processId);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            return (path is null ? null : Icon.ExtractAssociatedIcon(path), path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return (null, null);
        }
    }
}
