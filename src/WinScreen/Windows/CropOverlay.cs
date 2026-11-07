using static WinScreen.Settings.UiText;
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

    public CropOverlay(Rectangle window)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ForeColor = Color.White;
        Opacity = 0.55;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Text = T("WinScreen crop");
        Bounds = window;
    }

    public event Action<Rectangle>? Chosen;

    public void ShowOnTop()
    {
        Show();
        WindowController.Pin(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_pressed)
        {
            using var brush = new SolidBrush(Accent);
            e.Graphics.FillRectangle(brush, _selection);
            return;
        }

        const TextFormatFlags centred = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.WordBreak;
        TextRenderer.DrawText(e.Graphics, T("Drag over the part to keep. Esc cancels."), Font, ClientRectangle,
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
            return;

        _selection = Rectangle.Intersect(PipGeometry.Between(_dragStart, e.Location), ClientRectangle);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed)
            return;

        _pressed = false;
        bool bigEnough = _selection.Width >= SmallestSide && _selection.Height >= SmallestSide;
        Rectangle part = _selection;
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
        Close();
    }
}
