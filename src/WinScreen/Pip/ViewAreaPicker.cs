using WinScreen.Windows;

namespace WinScreen.Pip;

// One part of a view's picture, dragged over; a click without a drag picks all of it (Rectangle.Empty). Esc, a
// right-click or leaving for another window cancels.
internal sealed class ViewAreaPicker : Form
{
    private const int SmallestSide = 6;
    private readonly Rectangle _covers;
    private readonly string _words;
    private CropOverlay.Instructions? _instructions;
    private Point _start;
    private bool _pressed, _finished;
    private Rectangle _selection;

    public ViewAreaPicker(Rectangle area, string title, string words)
    {
        _covers = area;
        _words = words;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.35;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Text = title;
        Bounds = area;
    }

    // In the picker's own pixels, which are the screen area it was given.
    public event Action<Rectangle>? Chosen;

    public void ShowOnTop()
    {
        Show();
        try { WindowController.Pin(Handle); }
        catch (WindowOperationException) { TopMost = true; }
        Activate();
        _instructions = new CropOverlay.Instructions(this, _words);
        _instructions.ShowOver(_covers);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_pressed || _selection.Width <= 0 || _selection.Height <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(79, 140, 255));
        using var pen = new Pen(Color.White, 2) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
        e.Graphics.FillRectangle(brush, _selection);
        e.Graphics.DrawRectangle(pen, _selection);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Finish(null); return; }
        if (e.Button != MouseButtons.Left) return;
        _start = e.Location;
        _selection = Rectangle.Empty;
        _pressed = true;
        _instructions?.Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        _selection = Rectangle.Intersect(PipGeometry.Between(_start, e.Location), ClientRectangle);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        Finish(_selection.Width >= SmallestSide && _selection.Height >= SmallestSide ? _selection : Rectangle.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Finish(null);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Finish(null);
    }

    // For tests: as a drag over part, or a click when part is empty.
    internal void Choose(Rectangle part) => Finish(part);

    private void Finish(Rectangle? part)
    {
        if (_finished) return;
        _finished = true;
        _instructions?.Close();
        Close();
        if (part is { } chosen) Chosen?.Invoke(chosen);
    }
}
