using static WinScreen.Settings.UiText;
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
    private const int DragThreshold = 4;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 100 };
    private DwmThumbnail? _thumbnail;
    private Point _dragStart;
    private bool _pressed;
    private bool _dragged;
    private bool _hid;

    public ShrinkTile(nint source, WindowController windows)
    {
        _source = source;
        _windows = windows;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Accent;
        StartPosition = FormStartPosition.Manual;
        Text = T("Shrunk - ") + WindowInfo.Title(source);
        Size = new Size(TileWidth, 150);
        _watch.Tick += (_, _) => FollowSource();

        if (WindowInfo.TryGetVisibleBounds(source, out Rectangle bounds))
            Location = KeptOnScreen(bounds);
    }

    /// <summary>Raised when Windows refuses something, with a message for the person.</summary>
    public event Action<string>? Problem;

    /// <summary>Opens the tile, then hides the window it stands for.</summary>
    public void ShowOnTop()
    {
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        Show();
        if (!ShowLiveView())
        {
            Problem?.Invoke(T("This window can't be shrunk."));
            Close();
            return;
        }

        try
        {
            WindowController.Pin(Handle);
            _windows.Hide(_source);
        }
        catch (WindowOperationException)
        {
            Close();
            throw;
        }

        _hid = true;
        _watch.Start();
    }

    private bool ShowLiveView()
    {
        _thumbnail = DwmThumbnail.TryCreate(Handle, _source);
        Size sourceSize = _thumbnail?.SourceSize ?? Size.Empty;
        if (_thumbnail is null || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return false;

        var shown = new Rectangle(Point.Empty, sourceSize);
        Width = Math.Clamp(sourceSize.Width / 2, PipGeometry.MinimumWidth, TileWidth);
        Height = PipGeometry.FitHeight(Width, shown);
        _thumbnail.Show(PipGeometry.ViewArea(ClientSize), shown);
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
            Location = new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Dispose();
        _thumbnail?.Dispose();
        _thumbnail = null;
        if (_hid && User32.IsWindow(_source))
        {
            try
            {
                _windows.Show(_source);
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
        if (!User32.IsWindow(_source) || !_windows.IsHidden(_source) || User32.GetForegroundWindow() == _source)
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
