using static WinScreen.Settings.UiText;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// A borderless, always-on-top live view of another window. Drag to move, mouse wheel to resize,
/// Ctrl+drag to crop, Ctrl+double-click to remove the crop. Clicking activates the original by
/// default; verified input forwarding requires an explicit experimental opt-in. Using the view never
/// takes the focus from the app in use.
/// </summary>
public sealed class PipWindow : Form
{
    private const int WheelStep = 40;
    private const int DragThreshold = 4;
    private const int LeaveMargin = 8;
    private const int WheelNotch = 120;
    private const int StartLongSide = 480;
    private const int ScreenMargin = 20;
    private const int CloseButtonSize = 28;
    private const int EscapeHotkey = 0x5750;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_DISPLAYCHANGE = 0x007E;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private readonly PipSource _origin;
    private readonly nint _source;
    private readonly WindowController _windows;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autoMove = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _hideItem;
    private readonly ToolStripItem _goToItem;
    private readonly ToolStripMenuItem _autoMoveItem;
    private readonly ToolStripMenuItem _removeCropItem;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private PipCloseButton? _closeButton;
    private PipStateChip? _chip;
    private PipBackendSelector? _thumbnail;
    private PipPreferences _preferences = new() { Backend = PipBackendPreference.Dwm };
    private ToolStripMenuItem _retryItem = null!;
    private Rectangle _crop = Rectangle.Empty;
    private Rectangle _whole = Rectangle.Empty;
    private Rectangle _lastShown = Rectangle.Empty;
    private int _longSide;
    private nint _foregroundBeforeMenu;
    private bool _escapeClaimed;
    private Point _dragStart;
    private bool _pressed;
    private bool _dragged;
    private bool _selecting;
    private bool _interacting;
    private Rectangle _selection;
    private bool _cleaned;

    [System.ComponentModel.DefaultValue(false)]
    public bool AllowClickForwarding { get; set; }

    /// <summary>Whether this view offers to move itself, which is an experimental effect.</summary>
    [System.ComponentModel.DefaultValue(false)]
    public bool AllowAutoMove { get; set; }

    public PipWindow(nint source, WindowController windows)
        : this(new WindowPipSource(WindowIdentity.Capture(source)), windows)
    {
    }

    internal PipWindow(PipSource source, WindowController windows)
    {
        _origin = source;
        _source = source.Window;
        _windows = windows;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black; // Whatever briefly shows while resizing shouldn't flash.
        SetStyle(ControlStyles.ResizeRedraw, true);
        StartPosition = FormStartPosition.Manual;
        Text = T("PiP - ") + source.Title;
        _longSide = Dpi.Scale(StartLongSide, source.Dpi);
        _tips.SetToolTip(this,
            T("Click: go to the original • Drag: move • Wheel: resize • Ctrl+drag: crop • Right-click: menu"));

        _hideItem = new ToolStripMenuItem(T("&Keep updating (hide original)"), null,
            (_, _) => SetOriginalHidden(!_hideItem!.Checked));
        _autoMoveItem = new ToolStripMenuItem(T("&Move away from text and images"), null,
            (_, _) => SetAutoMove(!_autoMoveItem!.Checked));
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(_autoMoveItem);
        _goToItem = _menu.Items.Add(T("&Go to original window"), null, (_, _) => GoToSource());
        _removeCropItem = new ToolStripMenuItem(T("&Remove crop"), null, (_, _) => SetCrop(Rectangle.Empty));
        _menu.Items.Add(_removeCropItem);
        _menu.Items.Add(T("PiP diagnostics"), null, (_, _) => ShowDiagnostics());
        _retryItem = new ToolStripMenuItem(T("Retry advanced backend"), null, (_, _) => _thumbnail?.Retry());
        _menu.Items.Add(_retryItem);
        _menu.Items.Add(T("&Close PiP"), null, (_, _) => Close());
        _menu.Opening += (_, _) =>
        {
            _retryItem.Visible = _thumbnail?.CanRetry == true;
            _autoMoveItem.Visible = AllowAutoMove;
            _hideItem.Visible = _goToItem.Visible = _origin.IsWindow;
            _removeCropItem.Enabled = !_crop.IsEmpty;
            // A menu only closes on an outside click while its program has the focus.
            _foregroundBeforeMenu = User32.GetForegroundWindow();
            User32.SetForegroundWindow(Handle);
        };
        _menu.Closed += (_, _) => ReturnFocusAfterMenu();
        ContextMenuStrip = _menu;

        _watch.Tick += (_, _) => FollowSource();
        _autoMove.Tick += (_, _) => MoveAwayFromContent();
    }

    /// <summary>Raised when Windows refuses something, with a message for the person.</summary>
    public event Action<string>? Problem;

    /// <summary>Raised for something the person should know that isn't a failure.</summary>
    public event Action<string>? Notice;

    /// <summary>The part of the source window being shown; empty means all of it.</summary>
    public Rectangle Crop => _crop;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE);
            return parameters;
        }
    }

    /// <summary>
    /// Opens the view with its picture already in place. It's created on top rather than pinned
    /// afterwards, because pinning a window needs the focus, which Windows can refuse and which belongs
    /// to the app in use.
    /// </summary>
    public void ShowOnTop()
    {
        _origin.Require();
        // A minimized window has no shape or picture to show, so opening a view needs it back, and says so.
        bool restored = User32.IsIconic(_source);
        if (restored)
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
        try
        {
            _thumbnail = new PipBackendSelector(_origin, Handle, _preferences);
            _thumbnail.Problem += text => Problem?.Invoke(text);
            OpenInFreeSpot(_thumbnail);
            Show();
            StayOnTop();
            if ((User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) == 0)
                WindowController.Pin(Handle);
            _watch.Start();
            if (restored)
                Notice?.Invoke(string.Format(T("\"{0}\" was minimized, so it was restored to start its view."),
                    WindowInfo.Title(_source)));
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void ApplyPipPreferences(PipPreferences preferences)
    {
        _preferences = preferences.Validated();
        _thumbnail?.Configure(_preferences);
    }

    internal PipBackendKind BackendKind => _thumbnail?.Kind ?? PipBackendKind.DwmThumbnail;
    internal PipSessionState BackendState => _thumbnail?.State ?? PipSessionState.Stopped;
    internal string DiagnosticSnapshot => _thumbnail?.Snapshot() ?? "Stopped";

    private void ShowDiagnostics()
    {
        var dialog = new Form { Text = T("PiP diagnostics"), Size = new Size(700, 380), StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, Text = DiagnosticSnapshot.Replace("\n", Environment.NewLine) };
        var copy = new Button { Text = T("Copy diagnostics"), Dock = DockStyle.Bottom };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text.Text); }
            catch (ExternalException) { Problem?.Invoke(T("The clipboard is busy. Try copying diagnostics again.")); }
        };
        dialog.Controls.Add(text); dialog.Controls.Add(copy);
        using (dialog) dialog.ShowDialog();
    }

    /// <summary>Crops the view to a part of the source window, or shows all of it when empty.</summary>
    /// <param name="crop">The part to show, measured from the window's visible top-left corner.</param>
    public void SetCrop(Rectangle crop)
    {
        _crop = crop;
        Attempt(() => FitToSource());
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
        if (!_origin.IsWindow)
            return;
        try
        {
            _origin.Require();
            if (hidden && _thumbnail is null)
                throw new WindowOperationException(
                    T("The preview is not ready; its original window will not be hidden."));
            if (hidden)
            {
                _windows.Hide(_source);
                if (User32.GetForegroundWindow() == _source)
                    User32.SetForegroundWindow(Handle); // Keys mustn't go to a window that can't be seen.
                StayOnTop();
            }
            else
            {
                _windows.Show(_source);
            }

            _hideItem.Checked = hidden;
        }
        catch (WindowOperationException failure)
        {
            if (hidden && _origin.IsAvailable && _windows.IsHidden(_source))
            {
                try
                {
                    _windows.Show(_source);
                }
                catch (WindowOperationException)
                {
                }
            }

            _hideItem.Checked = _origin.IsAvailable && _windows.IsHidden(_source);
            Problem?.Invoke(failure.Message);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DISPLAYCHANGE || (m.Msg == 0x0218 && (m.WParam == 7 || m.WParam == 18)))
        {
            PipDisplay.InvalidateRefresh();
            _thumbnail?.DisplayChanged();
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        if (m.Msg == User32.WM_HOTKEY && m.WParam == EscapeHotkey)
        {
            CancelPress();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Accent, PipGeometry.Border) { Alignment = PenAlignment.Inset };
        e.Graphics.DrawRectangle(border, new Rectangle(Point.Empty, ClientSize));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _longSide = _longSide * e.DeviceDpiNew / Math.Max(1, e.DeviceDpiOld);
        Attempt(() => FitToSource());
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_pressed || _lastShown.IsEmpty)
            return;

        // Touchpads send many tiny steps, so the size follows how far the wheel really moved.
        int step = (int)Math.Round(Dpi.Scale(WheelStep, DeviceDpi) * (e.Delta / (double)WheelNotch));
        if (step == 0)
            step = Math.Sign(e.Delta);

        Rectangle area = Screen.FromControl(this).WorkingArea;
        int longSide = Math.Clamp(_longSide + step, Dpi.Scale(PipGeometry.MinimumWidth, DeviceDpi),
            Math.Max(area.Width, area.Height));
        Size size = PipGeometry.FitLongSide(longSide, _lastShown, area.Size);
        if (step < 0 && Math.Min(size.Width, size.Height) < Dpi.Scale(PipGeometry.MinimumHeight, DeviceDpi))
            return; // Any smaller and there would be nothing left to grab.

        _longSide = Math.Max(size.Width, size.Height);
        Point pointer = PointToScreen(e.Location);
        Attempt(() => FitToSource(pointer));
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
        {
            IPipRenderBackend view = _thumbnail;
            Attempt(() => BeginSelection(view));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        if (_selecting)
        {
            _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location),
                PipGeometry.ViewArea(ClientSize));
            Attempt(ShowSelection);
        }
        else if (_dragged || Math.Abs(e.X - _dragStart.X) >= DragThreshold ||
                 Math.Abs(e.Y - _dragStart.Y) >= DragThreshold)
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
        if (_selecting)
        {
            EndSelection();
            if (_selection.Width < 10 || _selection.Height < 10 || _thumbnail is null)
            {
                SetCrop(_crop);
            }
            else
            {
                Rectangle part = PipGeometry.SelectionToSource(_selection, PipGeometry.ViewArea(ClientSize),
                    ShownPart(_thumbnail.SourceSize));
                part.Offset(-_whole.X, -_whole.Y);
                SetCrop(part);
            }
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
        Cleanup();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _watch.Dispose();
        _autoMove.Dispose();
        ClaimEscape(false);
        // Teardown must never submit another draw: the source or destination HWND may already be gone.
        _selecting = false;
        _thumbnail?.Dispose();
        _thumbnail = null;
        EndInteraction();
        if (_hideItem.Checked && _origin.IsAvailable)
            SetOriginalHidden(false);
        _tips.Dispose();
        _closeButton?.Dispose();
        _chip?.Dispose();
        _menu.Dispose();
    }

    /// <summary>
    /// Puts the real window under the cursor with the clicked spot exactly beneath it, then passes the
    /// click on. Windows and apps see ordinary input, so this works in any program, keyboard included.
    /// The window goes back when the cursor leaves it.
    /// </summary>
    private void Interact(Point point)
    {
        if (_thumbnail is null || _interacting || !_origin.IsWindow)
            return;
        if (!AllowClickForwarding)
        {
            GoToSource();
            return;
        }

        Rectangle view = PipGeometry.ViewArea(ClientSize);
        if (!view.Contains(point))
            return;

        Size sourceSize = _thumbnail.SourceSize;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            GoToSource();
            return;
        }

        Point spot = PipGeometry.PointToSource(point, view, ShownPart(sourceSize));
        Point cursor = PointToScreen(point);
        Point position = PipGeometry.PeekPosition(cursor, spot, sourceSize, Screen.FromPoint(cursor).WorkingArea);

        try
        {
            _origin.Require();
            if (_windows.IsClickThrough(_source))
            {
                GoToSource();
                return;
            }

            position = _windows.BeginPeek(_source, position);
            _interacting = true;
            var target = new Point(position.X + spot.X, position.Y + spot.Y);
            // Activation and hit-test must agree after repositioning; a transparent, covered, cloaked,
            // replaced, or refused source must never turn into a desktop click.
            Cursor.Position = target;
            if (!_origin.IsAvailable || User32.GetForegroundWindow() != _source || !WindowInfo.IsAvailable(_source) ||
                (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) != 0 ||
                User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = target.X, Y = target.Y }),
                    User32.GA_ROOT) != _source)
                throw new WindowOperationException(
                    T(
                        "The PiP click was cancelled because its target could not be verified. Use Go to original window."));
            User32.ClickLeftButton();
        }
        catch (WindowOperationException failure)
        {
            EndInteraction();
            Cursor.Position = cursor;
            Problem?.Invoke(failure.Message);
            GoToSource();
            return;
        }
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
    /// Dims the picture while keeping the selected region bright. DWM uses a second thumbnail;
    /// native capture renders the corresponding mask over its cached source texture.
    /// </summary>
    private void BeginSelection(IPipRenderBackend view)
    {
        _selecting = true;
        _selection = Rectangle.Empty;
        BackColor = Color.Black;
        view.SetSelection(Rectangle.Empty);
    }

    private void ShowSelection() => _thumbnail?.SetSelection(_selection);

    private void EndSelection()
    {
        _selecting = false;
        Attempt(() => _thumbnail?.SetSelection(null));
    }

    /// <summary>Drops a press whose release never arrived, or that Esc called off, and undoes its effects.</summary>
    private void CancelPress()
    {
        _pressed = false;
        _dragged = false;
        if (!_selecting)
            return;
        EndSelection();
        Attempt(() => FitToSource());
    }

    /// <summary>
    /// The view doesn't take the focus, so Esc can't reach it the usual way. It's claimed only during a
    /// crop drag: a pointer merely resting on the view mustn't take Esc from a dialog or a game.
    /// </summary>
    private void ClaimEscape(bool claim)
    {
        if (claim == _escapeClaimed || !IsHandleCreated)
            return;
        _escapeClaimed = claim;
        if (claim)
            User32.RegisterHotKey(Handle, EscapeHotkey, User32.MOD_NOREPEAT, (uint)Keys.Escape);
        else
            User32.UnregisterHotKey(Handle, EscapeHotkey);
    }

    private void ReturnFocusAfterMenu()
    {
        if (!IsHandleCreated || IsDisposed || User32.GetForegroundWindow() != Handle)
            return;
        bool hiddenSource = _foregroundBeforeMenu == _source && _hideItem.Checked;
        if (_foregroundBeforeMenu != 0 && !hiddenSource)
            User32.SetForegroundWindow(_foregroundBeforeMenu);
    }

    /// <summary>Puts this above other topmost windows, which a hidden original just went above too.</summary>
    private void StayOnTop()
    {
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    /// <summary>A view that couldn't update says so and stays open; the next change can put it right.</summary>
    private void Attempt(Action action)
    {
        try
        {
            action();
        }
        catch (WindowOperationException failure)
        {
            Problem?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// The part of the source on show, in preview coordinates. A minimized window has nothing to measure,
    /// so it keeps its last shape. The crop itself is left alone, so it comes back whole when a source
    /// that shrank past it grows again.
    /// </summary>
    private Rectangle ShownPart(Size sourceSize)
    {
        if (_whole.IsEmpty || !_origin.IsMinimized)
            _whole = _origin.Whole(sourceSize);
        if (_crop.IsEmpty)
            return _whole;

        var cropped = new Rectangle(_whole.X + _crop.X, _whole.Y + _crop.Y, _crop.Width, _crop.Height);
        cropped.Intersect(_whole);
        return cropped.Width > 0 && cropped.Height > 0 ? cropped : _whole;
    }

    private void OpenInFreeSpot(IPipRenderBackend view)
    {
        Rectangle shown = ShownPart(view.SourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            throw new WindowOperationException(T("The source has no drawable preview surface."));

        Rectangle area = _origin.Screen.WorkingArea;
        int margin = Dpi.Scale(ScreenMargin, _origin.Dpi);
        Size size = PipGeometry.FitLongSide(_longSide, shown, area.Size);
        var wanted = new Rectangle(area.Right - size.Width - margin, area.Bottom - size.Height - margin,
            size.Width, size.Height);
        var others = Application.OpenForms.OfType<PipWindow>().Where(other => other != this && other.Visible)
            .Select(other => other.Bounds);
        Bounds = new Rectangle(PipGeometry.FreeSpot(wanted, others, area, margin), size);
        _lastShown = shown;
        view.SetViewport(PipGeometry.ViewArea(ClientSize), shown);
    }

    /// <param name="pointer">Keeps this screen point on the same spot of the view; otherwise it holds its corner.</param>
    private void FitToSource(Point? pointer = null)
    {
        if (_thumbnail is null)
            return;

        Rectangle shown = ShownPart(_thumbnail.SourceSize);
        if (shown.Width <= 0 || shown.Height <= 0)
            throw new WindowOperationException(T("The source has no drawable preview surface."));

        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size size = PipGeometry.FitLongSide(_longSide, shown, area.Size);
        Point place = pointer is { } spot
            ? PipGeometry.KeepUnderPointer(Bounds, size, spot)
            : PipGeometry.KeepCorner(Bounds, size, area);
        Bounds = new Rectangle(PipGeometry.KeepInside(new Rectangle(place, size), area), size);
        _lastShown = shown;
        _thumbnail.SetViewport(PipGeometry.ViewArea(ClientSize), shown);
    }

    private void FollowSource()
    {
        if (!_origin.IsAvailable)
        {
            Close();
            return;
        }

        try
        {
            _thumbnail?.Poll(ClientSize);
        }
        catch (WindowOperationException failure)
        {
            // If even the independent compatibility surface fails, close through normal restoration.
            // Repeating the same draw from this timer would spam the tray and strand a hidden source.
            Problem?.Invoke(failure.Message);
            Close();
            return;
        }
        if (_hideItem.Checked && !_windows.IsHidden(_source))
            _hideItem.Checked = false;
        if (_pressed && MouseButtons == MouseButtons.None)
            CancelPress();
        FollowPointer();
        ShowState();

        if (_interacting && ShouldEndInteraction())
            EndInteraction();
        if (_interacting || _pressed || _thumbnail is null)
            return;

        try
        {
            if (ShownPart(_thumbnail.SourceSize) != _lastShown)
                FitToSource();
        }
        catch (WindowOperationException)
        {
            // Apps can be briefly unmeasurable while they redraw; the next tick tries again.
        }
    }

    /// <summary>Shows the close button while the pointer is on the view, and keeps it in the corner.</summary>
    private void FollowPointer()
    {
        bool over = Bounds.Contains(Cursor.Position);
        ClaimEscape(_selecting);
        if (!over || _interacting || _selecting || _pressed)
        {
            _closeButton?.Hide();
            return;
        }

        int size = Dpi.Scale(CloseButtonSize, DeviceDpi);
        _closeButton ??= new PipCloseButton(this, T("&Close PiP").Replace("&", ""), size);
        _closeButton.Location = new Point(Right - size - PipGeometry.Border, Top + PipGeometry.Border);
        if (_closeButton.Visible)
            return;
        _closeButton.Show();
        User32.SetWindowPos(_closeButton.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    /// <summary>Says when the picture may be out of date, without claiming more than WinScreen can tell.</summary>
    private void ShowState()
    {
        PipChipState state = PipStateChip.Decide(_origin.IsMinimized, BackendState,
            Bounds.Contains(Cursor.Position), _interacting || _selecting || _pressed);
        if (state == PipChipState.None)
        {
            _chip?.Hide();
            return;
        }

        if (_chip is null)
        {
            _chip = new PipStateChip(this);
            _chip.Chosen += KeepUpdating;
        }

        _chip.Show(state, new Point(Left + PipGeometry.Border, Bottom - PipGeometry.Border));
    }

    /// <summary>
    /// The person's way out of a minimized source: conceal it first so it never flashes up, then bring it
    /// back without taking the focus. Only a click on the chip does this, never WinScreen by itself.
    /// </summary>
    internal void KeepUpdating()
    {
        SetOriginalHidden(true);
        if (_hideItem.Checked && User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_SHOWNOACTIVATE);
        StayOnTop();
    }

    /// <summary>Leaves the view alone while it's being used; the mouse over it means it's wanted there.</summary>
    private void MoveAwayFromContent()
    {
        if (_pressed || _interacting || _selecting || MouseButtons != MouseButtons.None ||
            Bounds.Contains(Cursor.Position))
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
               || !Rectangle.Inflate(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), LeaveMargin, LeaveMargin)
                   .Contains(Cursor.Position);
    }

    private void GoToSource()
    {
        if (!_origin.IsWindow)
            return;
        if (!_origin.IsAvailable)
        {
            Close();
            return;
        }

        if (_hideItem.Checked)
            SetOriginalHidden(false);
        if (User32.IsIconic(_source))
            User32.ShowWindow(_source, User32.SW_RESTORE);
        User32.SetForegroundWindow(_source);
    }
}
