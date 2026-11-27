using static WinScreen.Localization.UiText;
using System.Drawing.Drawing2D;
using WinScreen.Native;
using WinScreen.Tray;
using WinScreen.Windows;

namespace WinScreen.Pip;

// A frozen copy of a view: a picture of its own, kept on top, that the view's window no longer changes. It holds the
// view's picture as it was, covers already blacked out. Moved, resized, cropped and zoomed as a view is.
internal sealed class StillPicture : Form
{
    private const int WheelStep = 40, WheelNotch = 120, DragThreshold = 4;
    private const double ZoomStep = 1.25;
    private readonly Bitmap _picture;
    private readonly PictureShelf _shelf;
    private readonly ContextMenuStrip _menu = new();
    private Rectangle _crop;
    private RectangleF _zoom = PipGeometry.WholeView;
    private int _longSide;
    private Point _dragStart, _panFrom;
    private bool _pressed, _dragged, _selecting, _panning, _controlAtPress;
    private Rectangle _selection;
    private string _said = "";
    private readonly System.Windows.Forms.Timer _sayFor = new() { Interval = 2500 };

    public StillPicture(Bitmap picture, PictureShelf shelf, string title)
    {
        _picture = picture;
        _shelf = shelf;
        _crop = new Rectangle(Point.Empty, picture.Size);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(79, 140, 255);
        DoubleBuffered = true;
        KeyPreview = true;
        Text = T("still.titlePrefix") + title;
        _sayFor.Tick += (_, _) => { _sayFor.Stop(); _said = ""; Invalidate(); };
        BuildMenu();
    }

    public event Action<string>? Problem;

    internal Rectangle ShownPart => PipGeometry.ZoomedPart(_crop, _zoom);
    internal Rectangle PictureArea => PipGeometry.ViewArea(ClientSize);
    internal string Said => _said;

    // Beside the view it was copied from, at the size the view shows it.
    public void ShowBeside(Rectangle view, int longSide)
    {
        Rectangle area = Screen.FromRectangle(view).WorkingArea;
        _longSide = longSide;
        Size size = PipGeometry.FitLongSide(_longSide, ShownPart, area.Size);
        Rectangle wanted = PipPreviewGeometry.Place(view, size, area);
        IEnumerable<Rectangle> others = Application.OpenForms.OfType<Form>()
            .Where(other => other != this && other.Visible && other is PipWindow or StillPicture).Select(other => other.Bounds);
        Bounds = new Rectangle(PipGeometry.FreeSpot(wanted, others, area, Dpi.Scale(12, DeviceDpi)), size);
        Show();
        try { WindowController.Pin(Handle); }
        catch (WindowOperationException) { TopMost = true; }
    }

    private void BuildMenu()
    {
        // Capture as the view's: it copies, saves or both, as Settings' Capture says. Ctrl+C and Ctrl+S still do one each.
        // The same icons as these items have in the view's menu and the window menu.
        _menu.Items.Add(T("pip.picture.copy"), MenuParts.Icon(MenuParts.Copy), (_, _) => CapturePicture());
        _menu.Items.Add(T("pip.picture.copyText"), MenuParts.Icon(MenuParts.Text), (_, _) => CopyText());
        var opacity = new ToolStripMenuItem(T("pip.opacity"), MenuParts.Icon(MenuParts.Brightness));
        foreach (int percent in new[] { 100, 75, 50, 25 })
            opacity.DropDownItems.Add(new ToolStripMenuItem($"{percent}%", null, (sender, _) =>
            {
                Opacity = percent / 100.0;
                foreach (ToolStripMenuItem item in opacity.DropDownItems) item.Checked = item == sender;
            }) { Checked = percent == 100 });
        _menu.Items.Add(opacity);
        _menu.Items.Add(T("pip.zoom.reset"), MenuParts.Icon(MenuParts.Crop), (_, _) => ResetCrop());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(T("still.close"), MenuParts.Icon(MenuParts.Close), (_, _) => Close()) { ShortcutKeyDisplayString = "Esc" });
        MenuRenderer.Apply(_menu);
        ContextMenuStrip = _menu;
    }

    internal Bitmap ShownPicture() => _picture.Clone(ShownPart, _picture.PixelFormat);

    internal void CapturePicture()
    {
        try
        {
            using Bitmap shown = ShownPicture();
            (string? file, string? problem) = _shelf.Keep(shown);
            if (problem is not null) Problem?.Invoke(string.Format(T("errors.pip.saveFailed"), problem));
            // The view's "Click to show it" doesn't hold here: a click on the copy shows no file.
            if (file is not null) Say(T("still.saved"));
            else if (_shelf.Copies()) Say(T("pip.state.pictureCopied"));
        }
        catch (System.Runtime.InteropServices.ExternalException failure)
        {
            Problem?.Invoke(string.Format(T("errors.pip.copyFailed"), failure.Message));
        }
    }

    internal void CopyPicture()
    {
        try
        {
            using Bitmap shown = ShownPicture();
            _shelf.PutPicture(shown);
            Say(T("pip.state.pictureCopied"));
        }
        catch (System.Runtime.InteropServices.ExternalException failure)
        {
            Problem?.Invoke(string.Format(T("errors.pip.copyFailed"), failure.Message));
        }
    }

    internal string? SavePicture()
    {
        try
        {
            using Bitmap shown = ShownPicture();
            string file = _shelf.Save(shown);
            Say(T("still.saved"));
            return file;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException or NotSupportedException)
        {
            Problem?.Invoke(string.Format(T("errors.pip.saveFailed"), failure.Message));
            return null;
        }
    }

    private void CopyText()
    {
        var picker = new ViewAreaPicker(RectangleToScreen(PictureArea), T("pip.copyText.title"), T("pip.copyText.instructions"));
        picker.Chosen += async part =>
        {
            try
            {
            Rectangle shown = ShownPart;
            Rectangle inShown = part.IsEmpty ? new Rectangle(Point.Empty, shown.Size)
                : ViewPicture.InPicture(part with { X = part.X + PictureArea.X, Y = part.Y + PictureArea.Y }, PictureArea, shown.Size);
            if (inShown.Width <= 0 || inShown.Height <= 0) return;
            using Bitmap piece = _picture.Clone(inShown with { X = inShown.X + shown.X, Y = inShown.Y + shown.Y }, _picture.PixelFormat);
            string? said = await ViewText.CopyFrom(this, piece, _shelf, text => Problem?.Invoke(text));
            await UiThread.Back(this);
            if (said is not null && !IsDisposed) Say(said);
            }
            catch (Exception failure)
            {
                Diagnostics.Log.Error("still.textFailed", failure);
                if (!IsDisposed) Problem?.Invoke(string.Format(T("errors.pip.textFailed"), failure.Message));
            }
        };
        picker.ShowOnTop();
    }

    private void ResetCrop()
    {
        _zoom = PipGeometry.WholeView;
        _crop = new Rectangle(Point.Empty, _picture.Size);
        Fit();
    }

    private void Say(string words)
    {
        _said = words;
        _sayFor.Stop();
        _sayFor.Start();
        Invalidate();
    }

    private void Fit(Point? pointer = null)
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Size size = PipGeometry.FitLongSide(_longSide, ShownPart, area.Size);
        Point place = pointer is { } spot ? PipGeometry.KeepUnderPointer(Bounds, size, spot) : PipGeometry.KeepCorner(Bounds, size, area);
        Bounds = new Rectangle(PipGeometry.KeepInside(new Rectangle(place, size), area), size);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        Rectangle area = PictureArea;
        Rectangle shown = ShownPart;
        bool enlarging = area.Width > shown.Width * 2;
        graphics.InterpolationMode = enlarging ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using (var look = new System.Drawing.Imaging.ImageAttributes())
        {
            look.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(_picture, area, shown.X, shown.Y, shown.Width, shown.Height, GraphicsUnit.Pixel, look);
        }
        if (_selecting && _selection.Width > 0 && _selection.Height > 0)
        {
            using var dim = new Region(area);
            dim.Exclude(_selection);
            using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            graphics.FillRegion(shade, dim);
        }
        if (_said.Length > 0)
        {
            Size text = TextRenderer.MeasureText(_said, Font);
            int padding = Dpi.Scale(6, DeviceDpi);
            var chip = new Rectangle(area.Left, area.Bottom - text.Height - padding, text.Width + 2 * padding, text.Height + padding);
            using var back = new SolidBrush(Color.FromArgb(32, 32, 32));
            graphics.FillRectangle(back, chip);
            TextRenderer.DrawText(graphics, _said, Font, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_pressed) return;
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Rectangle shown = PictureArea;
            _zoom = PipGeometry.ZoomAround(_zoom, new PointF((e.X - shown.Left) / (float)shown.Width, (e.Y - shown.Top) / (float)shown.Height),
                Math.Pow(ZoomStep, e.Delta / (double)WheelNotch));
            Invalidate();
            return;
        }
        int step = (int)Math.Round(Dpi.Scale(WheelStep, DeviceDpi) * (e.Delta / (double)WheelNotch));
        if (step == 0) step = Math.Sign(e.Delta);
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int longSide = Math.Clamp(_longSide + step, Dpi.Scale(PipGeometry.MinimumWidth, DeviceDpi), Math.Max(work.Width, work.Height));
        Size size = PipGeometry.FitLongSide(longSide, ShownPart, work.Size);
        if (step < 0 && Math.Min(size.Width, size.Height) < Dpi.Scale(PipGeometry.MinimumHeight, DeviceDpi)) return;
        _longSide = Math.Max(size.Width, size.Height);
        Fit(PointToScreen(e.Location));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Middle && _zoom != PipGeometry.WholeView)
        {
            _panning = true;
            _panFrom = e.Location;
        }
        if (e.Button != MouseButtons.Left) return;
        _dragStart = e.Location;
        _pressed = true;
        _dragged = false;
        _controlAtPress = (ModifierKeys & Keys.Control) != 0;
        _selecting = _controlAtPress;
        _selection = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning && e.Button == MouseButtons.Middle)
        {
            Rectangle area = PictureArea;
            _zoom = PipGeometry.Pan(_zoom, (_panFrom.X - e.X) / (float)area.Width * _zoom.Width, (_panFrom.Y - e.Y) / (float)area.Height * _zoom.Height);
            _panFrom = e.Location;
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left || !_pressed) return;
        if (_selecting)
        {
            _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location), PictureArea);
            Invalidate();
        }
        else if (_dragged || Math.Abs(e.X - _dragStart.X) >= DragThreshold || Math.Abs(e.Y - _dragStart.Y) >= DragThreshold)
        {
            _dragged = true;
            Location = PipGeometry.KeepInside(new Rectangle(new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y), Size),
                Screen.FromPoint(Cursor.Position).WorkingArea);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) _panning = false;
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        if (!_selecting) return;
        _selecting = false;
        if (_selection.Width >= 10 && _selection.Height >= 10)
        {
            _crop = PipGeometry.SelectionToSource(_selection, PictureArea, ShownPart);
            _zoom = PipGeometry.WholeView;
            Fit();
        }
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _controlAtPress) ResetCrop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyData == (Keys.Control | Keys.C)) CopyPicture();
        else if (e.KeyData == (Keys.Control | Keys.S)) SavePicture();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW);
            return parameters;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture.Dispose();
            _menu.Dispose();
            _sayFor.Dispose();
        }
        base.Dispose(disposing);
    }
}
