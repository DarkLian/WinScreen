using static WinScreen.Settings.UiText;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// A borderless, always-on-top live view of another window. Drag to move, mouse wheel to resize,
/// Ctrl+drag to crop, Ctrl+double-click to remove the crop. A click brings the real window under
/// the cursor and passes the click on, so the view is used like the window itself.
/// </summary>
public sealed class PipWindow : Form
{
    private const int WheelStep = 40;
    private const int DragThreshold = 4;
    private const int LeaveMargin = 8;
    private const byte DimmedOpacity = 90;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autoMove = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _hideItem;
    private readonly ToolStripMenuItem _autoMoveItem;
    private DwmThumbnail? _thumbnail;
    private DwmThumbnail? _selectionView;
    private Rectangle _crop = Rectangle.Empty;
    private Size _lastSourceSize;
    private Point _dragStart;
    private bool _pressed;
    private bool _dragged;
    private bool _selecting;
    private bool _interacting;
    private Rectangle _selection;

    public PipWindow(nint source, WindowController windows)
    {
        _source = source;
        _windows = windows;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Accent;
        StartPosition = FormStartPosition.Manual;
        Text = T("PiP - ") + WindowInfo.Title(source);

        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Size = new Size(480, 300);
        Location = new Point(area.Right - 500, area.Bottom - 320);

        _hideItem = new ToolStripMenuItem(T("&Hide original window"), null, (_, _) => SetOriginalHidden(!_hideItem!.Checked));
        _autoMoveItem = new ToolStripMenuItem(T("&Move away from text and images"), null, (_, _) => SetAutoMove(!_autoMoveItem!.Checked));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_hideItem);
        menu.Items.Add(_autoMoveItem);
        menu.Items.Add(T("&Go to original window"), null, (_, _) => GoToSource());
        menu.Items.Add(T("&Remove crop"), null, (_, _) => SetCrop(Rectangle.Empty));
        menu.Items.Add(T("&Close PiP"), null, (_, _) => Close());
        ContextMenuStrip = menu;

        _watch.Tick += (_, _) => FollowSource();
        _autoMove.Tick += (_, _) => MoveAwayFromContent();
    }

    /// <summary>Raised when Windows refuses something, with a message for the person.</summary>
    public event Action<string>? Problem;

    /// <summary>The part of the source window being shown; empty means all of it.</summary>
    public Rectangle Crop => _crop;

    /// <summary>
    /// Opens the window and pins it. Form.TopMost isn't used because Windows can drop it without
    /// saying so; <see cref="WindowController.Pin"/> checks the result.
    /// </summary>
    public void ShowOnTop()
    {
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        Show();
        WindowController.Pin(Handle);
    }

    /// <summary>Crops the view to a part of the source window, or shows all of it when empty.</summary>
    public void SetCrop(Rectangle crop)
    {
        _crop = crop;
        FitToSource();
    }

    /// <summary>
    /// Turns moving away from text and images on or off. Telling what is under the view needs a look
    /// at the screen without the view on it, so while this is on the view is left out of anything that
    /// captures the screen, screenshots and screen sharing included.
    /// </summary>
    public void SetAutoMove(bool on)
    {
        if (!User32.SetWindowDisplayAffinity(Handle, on ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE))
        {
            Problem?.Invoke(T("This version of Windows can't move the view by itself."));
            return;
        }

        _autoMoveItem.Checked = on;
        _autoMove.Enabled = on;
    }

    /// <summary>Makes the original invisible, or brings it back. The view keeps updating either way.</summary>
    public void SetOriginalHidden(bool hidden)
    {
        try
        {
            if (hidden)
                _windows.Hide(_source);
            else
                _windows.Show(_source);
            _hideItem.Checked = hidden;
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _thumbnail = DwmThumbnail.TryCreate(Handle, _source);
        if (_thumbnail is null)
        {
            Problem?.Invoke(T("This window can't be shown in Picture-in-Picture."));
            Close();
            return;
        }

        FitToSource();
        _watch.Start();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int widest = Screen.FromControl(this).WorkingArea.Width;
        Width = Math.Clamp(Width + (e.Delta > 0 ? WheelStep : -WheelStep), PipGeometry.MinimumWidth, widest);
        FitToSource();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        _dragStart = e.Location;
        _pressed = true;
        _dragged = false;
        if ((ModifierKeys & Keys.Control) != 0 && _thumbnail is not null)
            BeginSelection(_thumbnail);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        if (_selecting)
        {
            _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location), PipGeometry.ViewArea(ClientSize));
            ShowSelection();
        }
        else if (_dragged || Math.Abs(e.X - _dragStart.X) >= DragThreshold || Math.Abs(e.Y - _dragStart.Y) >= DragThreshold)
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
        if (_selecting)
        {
            EndSelection();
            if (_selection.Width < 10 || _selection.Height < 10 || _thumbnail is null)
                FitToSource();
            else
                SetCrop(PipGeometry.SelectionToSource(_selection, PipGeometry.ViewArea(ClientSize), ShownPart(_thumbnail.SourceSize)));
        }
        else if (!_dragged && (ModifierKeys & Keys.Control) == 0)
        {
            Interact(e.Location);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if ((ModifierKeys & Keys.Control) != 0)
            SetCrop(Rectangle.Empty);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Dispose();
        _autoMove.Dispose();
        EndSelection();
        EndInteraction();
        if (_hideItem.Checked)
            SetOriginalHidden(false);
        _thumbnail?.Dispose();
        _thumbnail = null;
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Puts the real window under the cursor with the clicked spot exactly beneath it, then passes the
    /// click on. Windows and apps see ordinary input, so this works in any program, keyboard included.
    /// The window goes back when the cursor leaves it.
    /// </summary>
    private void Interact(Point point)
    {
        if (_thumbnail is null || _interacting)
            return;

        Rectangle view = PipGeometry.ViewArea(ClientSize);
        if (!view.Contains(point))
            return;

        Size sourceSize = _thumbnail.SourceSize;
        Point spot = PipGeometry.PointToSource(point, view, ShownPart(sourceSize));
        Point cursor = PointToScreen(point);
        Point position = PipGeometry.PeekPosition(cursor, spot, sourceSize, Screen.FromPoint(cursor).WorkingArea);

        try
        {
            position = _windows.BeginPeek(_source, position);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
            return;
        }

        _interacting = true;
        var target = new Point(position.X + spot.X, position.Y + spot.Y);
        Cursor.Position = target;

        // A maximized original stays behind this view, so the view itself may be over that spot.
        if (!User32.IsZoomed(_source) || !Bounds.Contains(target))
            User32.ClickLeftButton();
    }

    private void EndInteraction()
    {
        if (!_interacting)
            return;

        _interacting = false;
        try
        {
            _windows.EndPeek(_source);
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// DWM draws thumbnails above everything in this window, so nothing can be painted over the view.
    /// Instead the view dims and a second thumbnail keeps the selected part at full brightness.
    /// </summary>
    private void BeginSelection(DwmThumbnail view)
    {
        _selecting = true;
        _selection = Rectangle.Empty;
        _selectionView = DwmThumbnail.TryCreate(Handle, _source);
        BackColor = Color.Black;
        view.Show(PipGeometry.ViewArea(ClientSize), ShownPart(view.SourceSize), DimmedOpacity);
    }

    private void ShowSelection()
    {
        if (_selectionView is null || _thumbnail is null || _selection.IsEmpty)
            return;

        Rectangle part = PipGeometry.SelectionToSource(_selection, PipGeometry.ViewArea(ClientSize), ShownPart(_thumbnail.SourceSize));
        if (!part.IsEmpty)
            _selectionView.Show(_selection, part);
    }

    private void EndSelection()
    {
        _selecting = false;
        _selectionView?.Dispose();
        _selectionView = null;
        BackColor = Accent;
    }

    private Rectangle ShownPart(Size sourceSize) => _crop.IsEmpty ? new Rectangle(Point.Empty, sourceSize) : _crop;

    private void FitToSource()
    {
        if (_thumbnail is null)
            return;

        Size sourceSize = _thumbnail.SourceSize;
        Rectangle shown = ShownPart(sourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            return;

        _lastSourceSize = sourceSize;
        if (_selecting)
            return;

        Height = PipGeometry.FitHeight(Width, shown);
        _thumbnail.Show(PipGeometry.ViewArea(ClientSize), shown);
    }

    private void FollowSource()
    {
        if (!User32.IsWindow(_source))
        {
            Close();
            return;
        }

        if (_hideItem.Checked && !_windows.IsHidden(_source))
            _hideItem.Checked = false;

        if (_interacting && ShouldEndInteraction())
        {
            EndInteraction();
            Activate();
        }

        if (!_interacting && _thumbnail is not null && _thumbnail.SourceSize != _lastSourceSize)
            FitToSource();
    }

    /// <summary>Leaves the view alone while it's being used; the mouse over it means it's wanted there.</summary>
    private void MoveAwayFromContent()
    {
        if (_pressed || _interacting || _selecting || MouseButtons != MouseButtons.None || Bounds.Contains(Cursor.Position))
            return;

        Rectangle area = Screen.FromControl(this).WorkingArea;
        bool[,] busy;
        try
        {
            busy = ScreenContent.BusyCells(area);
        }
        catch (ExternalException)
        {
            // Windows refuses screen captures while the PC is locked or asking for permission.
            return;
        }

        if (PipGeometry.CalmerSpot(busy, area, Bounds) is { } spot)
            Location = spot;
    }

    private bool ShouldEndInteraction()
    {
        if (!_windows.IsPeeking(_source))
            return true;
        if (MouseButtons != MouseButtons.None)
            return false;
        if (User32.GetForegroundWindow() != _source)
            return true;
        if (User32.IsZoomed(_source))
            return Bounds.Contains(Cursor.Position);

        return !User32.GetWindowRect(_source, out Rect r)
               || !Rectangle.Inflate(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), LeaveMargin, LeaveMargin).Contains(Cursor.Position);
    }

    private void GoToSource()
    {
        if (_hideItem.Checked)
            SetOriginalHidden(false);
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        User32.SetForegroundWindow(_source);
    }
}
