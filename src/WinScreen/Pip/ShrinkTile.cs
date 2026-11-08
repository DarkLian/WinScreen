using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// A window shrunk to a small live tile where its corner was. The real window keeps its size, so
/// nothing inside it rearranges; it is only made invisible until the tile is clicked, or until
/// someone switches to it from the taskbar or Alt-Tab.
/// </summary>
public sealed class ShrinkTile : Form
{
    private const int TileWidth = 240;
    private const int TileHeight = 150;
    private const int DragThreshold = 4;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private readonly nint _source;
    private readonly WindowIdentity _identity;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 100 };
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly int _width;
    private DwmThumbnail? _thumbnail;
    private Point _dragStart;
    private bool _pressed;
    private bool _dragged;
    private bool _hid;

    public ShrinkTile(nint source, WindowController windows)
    {
        _source = source;
        _identity = WindowIdentity.Capture(source);
        _windows = windows;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Accent;
        StartPosition = FormStartPosition.Manual;
        Text = T("shrink.titlePrefix") + WindowInfo.Title(source);
        int dpi = Dpi.Of(source);
        _width = Dpi.Scale(TileWidth, dpi);
        Size = new Size(_width, Dpi.Scale(TileHeight, dpi));
        _tip.SetToolTip(this, T("shrink.controls"));
        _menu.Items.Add(T("window.show"), null, (_, _) => Close());
        ContextMenuStrip = _menu;
        _watch.Tick += (_, _) => FollowSource();

        if (WindowInfo.TryGetVisibleBounds(source, out Rectangle bounds))
            Location = KeptOnScreen(bounds);
    }

    /// <summary>Raised when Windows refuses something, with a message for the person.</summary>
    public event Action<string>? Problem;

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

    /// <summary>
    /// Opens the tile, then hides the window it stands for. The live view is ready before the tile
    /// appears, and being created on top needs no focus, so Windows has nothing to refuse.
    /// </summary>
    public void ShowOnTop()
    {
        _identity.Require();
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        _ = Handle;
        if (!ShowLiveView())
        {
            Problem?.Invoke(T("errors.shrink.unavailable"));
            Close();
            return;
        }

        try
        {
            Show();
            _hid = true; // A partially failed conceal must still be revealed by OnFormClosed.
            _windows.HideOwned(_source, this);
            User32.SetForegroundWindow(Handle); // Keys mustn't go to a window that can't be seen.
            User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
        catch (WindowOperationException)
        {
            Close();
            throw;
        }

        _watch.Start();
    }

    private bool ShowLiveView()
    {
        _thumbnail = DwmThumbnail.TryCreate(Handle, _source);
        Size sourceSize = _thumbnail?.SourceSize ?? Size.Empty;
        if (_thumbnail is null || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return false;

        Rectangle shown = WindowInfo.VisiblePart(_source, sourceSize);
        Width = Math.Clamp(shown.Width / 2, PipGeometry.MinimumWidth, _width);
        Size = PipGeometry.FitWithin(Width, shown, Screen.FromHandle(_source).WorkingArea.Size);
        Location = KeptOnScreen(Bounds);
        try
        {
            _thumbnail.Show(PipGeometry.ViewArea(ClientSize), shown);
        }
        catch (WindowOperationException)
        {
            return false;
        }

        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        _dragStart = e.Location;
        _pressed = true;
        _dragged = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        if (_dragged || Math.Abs(e.X - _dragStart.X) >= DragThreshold || Math.Abs(e.Y - _dragStart.Y) >= DragThreshold)
        {
            _dragged = true;
            Location = PipGeometry.KeepInside(
                new Rectangle(new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y), Size),
                Screen.FromPoint(Cursor.Position).WorkingArea);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        _pressed = false;
        if (!_dragged)
            Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_hid && _identity.Matches())
        {
            try { _windows.ShowOwned(_source, this); _hid = false; }
            catch (WindowOperationException failure) { Problem?.Invoke(failure.Message); e.Cancel = true; }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tip.Dispose();
        _menu.Dispose();
        _watch.Dispose();
        _thumbnail?.Dispose();
        _thumbnail = null;
        if (_hid && _identity.Matches())
        {
            try
            {
                _windows.ShowOwned(_source, this);
                User32.SetForegroundWindow(_source);
            }
            catch (WindowOperationException failure)
            {
                Problem?.Invoke(failure.Message);
            }
        }

        base.OnFormClosed(e);
    }

    private void FollowSource()
    {
        if (!_identity.Matches() || !_windows.IsHidden(_source) || User32.GetForegroundWindow() == _source)
            Close();
    }

    private Point KeptOnScreen(Rectangle bounds)
    {
        Rectangle area = Screen.FromRectangle(bounds).WorkingArea;
        return new Point(
            Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
}
