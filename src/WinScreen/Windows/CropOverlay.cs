using static WinScreen.Localization.UiText;
using WinScreen.Pip;

namespace WinScreen.Windows;

/// <summary>
/// Covers a window for a moment so the part to keep can be dragged out. Escape, a right-click or
/// clicking elsewhere cancels. <see cref="Chosen"/> gives the part in window coordinates.
/// </summary>
public sealed class CropOverlay : Form
{
    private const int SmallestSide = 20;

    private static readonly Color Accent = Color.FromArgb(79, 140, 255);

    private Point _dragStart;
    private bool _pressed;
    private bool _finished;
    private Rectangle _selection;
    private readonly Rectangle _covers;
    private ElementFinder? _finder;
    private Rectangle _hint;

    public CropOverlay(Rectangle window)
    {
        _covers = window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ForeColor = Color.White;
        Opacity = 0.55;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Text = T("crop.title");
        Bounds = window;
    }

    public event Action<Rectangle>? Chosen;

    public void ShowOnTop()
    {
        Show();
        WindowController.Pin(Handle);
        _finder = new ElementFinder(Handle);
        _finder.Found += ShowHint;
    }

    /// <summary>Marks the control under the pointer, so a click can take it without any dragging.</summary>
    private void ShowHint(Rectangle? found)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() =>
        {
            Rectangle hint = found is { } rectangle
                ? Rectangle.Intersect(new Rectangle(rectangle.Location - (Size)_covers.Location, rectangle.Size), ClientRectangle)
                : Rectangle.Empty;
            if (hint == _hint || _pressed)
                return;
            _hint = hint;
            Invalidate();
        });
    }

    /// <summary>Moving onto a monitor with other scaling must not change what's covered.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Bounds = _covers;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_pressed)
        {
            using var brush = new SolidBrush(Accent);
            e.Graphics.FillRectangle(brush, _selection);
            TextRenderer.DrawText(e.Graphics, $"{_selection.Width} × {_selection.Height} px", Font,
                new Point(Math.Max(0, _selection.Left), Math.Max(0, _selection.Top - Font.Height - 4)), ForeColor, BackColor);
            return;
        }

        if (_hint.Width > 0 && _hint.Height > 0)
        {
            using var pen = new Pen(Accent, 2) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
            e.Graphics.DrawRectangle(pen, _hint);
        }

        const TextFormatFlags centred = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.WordBreak;
        TextRenderer.DrawText(e.Graphics, T("crop.instructions"), Font, ClientRectangle,
            ForeColor, centred);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            Finish();
        }
        else if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _selection = Rectangle.Empty;
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed)
        {
            _finder?.Ask(PointToScreen(e.Location));
            return;
        }

        _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location), ClientRectangle);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        _pressed = false;
        Rectangle part = _selection;
        // A press and release with no real drag takes the highlighted control.
        if (part.Width < SmallestSide && part.Height < SmallestSide && _hint.Width > 0 && _hint.Height > 0)
            part = _hint;
        bool bigEnough = part.Width >= SmallestSide && part.Height >= SmallestSide;
        Finish();
        if (bigEnough)
            Chosen?.Invoke(part);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Finish();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Finish();
    }

    private void Finish()
    {
        if (_finished)
            return;

        _finished = true;
        _finder?.Dispose();
        _finder = null;
        Close();
    }
}
