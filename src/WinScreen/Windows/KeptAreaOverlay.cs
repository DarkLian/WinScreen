using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Pip;

namespace WinScreen.Windows;

// Covers the window's frame, the same bounds Glass draws over, so a marked rectangle is in the effect's own pixels.
public sealed class KeptAreaOverlay : Form
{
    private const int SmallestSide = 20;

    private static readonly Color Blue = Color.FromArgb(79, 140, 255);
    private readonly Color Accent = Blue;
    private readonly string _instructions = T("keptAreas.instructions");
    private readonly bool _hints = true;

    private readonly Rectangle _covers;
    private readonly List<Rectangle> _areas;
    private readonly int _most;
    private Point _dragStart;
    private bool _pressed;
    private bool _finished;
    private Rectangle _selection;
    private Rectangle _hint;
    private ElementFinder? _finder;
    private CropOverlay.Instructions? _words;

    public KeptAreaOverlay(Rectangle frame, IEnumerable<Rectangle> areas, int most)
    {
        _covers = frame;
        _areas = areas.Select(area => Rectangle.Intersect(area, new Rectangle(Point.Empty, frame.Size))).Where(area => !area.IsEmpty).ToList();
        _most = most;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ForeColor = Color.White;
        Opacity = 0.55;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Text = T("keptAreas.title");
        Bounds = frame;
    }

    // Marking something else the same way, such as a view's covers: its own words and fill, and no highlighted parts
    // (they would be the parts of whatever is under it).
    internal KeptAreaOverlay(Rectangle frame, IEnumerable<Rectangle> areas, int most, string title, string instructions, Color fill)
        : this(frame, areas, most)
    {
        Text = title;
        _instructions = instructions;
        Accent = fill;
        _hints = false;
    }

    // The areas when the person is done; never raised for Esc.
    public event Action<IReadOnlyList<Rectangle>>? Done;

    internal IReadOnlyList<Rectangle> Areas => _areas;

    public void ShowOnTop()
    {
        Show();
        // Windows can refuse to bring it forward (a key pressed with another app in front): on top is enough.
        try { WindowController.Pin(Handle); }
        catch (WindowOperationException) { TopMost = true; }
        _words = new CropOverlay.Instructions(this, _instructions);
        _words.ShowOver(_covers);
        if (!_hints) return;
        _finder = new ElementFinder(Handle);
        _finder.Found += ShowHint;
    }

    private void ShowHint(FoundElement? found)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            Rectangle hint = found?.Bounds is { } rectangle
                ? Rectangle.Intersect(new Rectangle(rectangle.Location - (Size)_covers.Location, rectangle.Size), ClientRectangle)
                : Rectangle.Empty;
            if (_pressed || hint == _hint)
                return;
            _hint = hint;
            Invalidate();
        });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Bounds = _covers;
        if (_words is { Visible: true })
            _words.ShowOver(_covers);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var brush = new SolidBrush(Accent);
        using var pen = new Pen(Color.White, 2) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
        foreach (Rectangle area in _areas)
        {
            e.Graphics.FillRectangle(brush, area);
            e.Graphics.DrawRectangle(pen, area);
        }
        if (_pressed)
        {
            // The border too: a cover's fill is as black as the overlay, so it alone shows nothing.
            e.Graphics.FillRectangle(brush, _selection);
            if (_selection.Width > 0 && _selection.Height > 0) e.Graphics.DrawRectangle(pen, _selection);
            return;
        }
        if (_hint.Width > 0 && _hint.Height > 0 && !_areas.Any(area => area.Contains(_hint)))
        {
            using var outline = new Pen(Accent, 2) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
            e.Graphics.DrawRectangle(outline, _hint);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            Finish(keep: true);
        }
        else if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _selection = Rectangle.Empty;
            _pressed = true;
            _words?.Hide();
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
        Choose(_selection, e.Location);
        Invalidate();
    }

    internal void Choose(Rectangle dragged, Point at)
    {
        if (dragged.Width >= SmallestSide && dragged.Height >= SmallestSide)
        {
            Add(dragged);
            return;
        }
        // A click on a marked area takes it away; elsewhere it takes the highlighted part.
        int marked = _areas.FindLastIndex(area => area.Contains(at));
        if (marked >= 0)
            _areas.RemoveAt(marked);
        else if (_hint.Width >= SmallestSide && _hint.Height >= SmallestSide)
            Add(_hint);
    }

    private void Add(Rectangle area)
    {
        if (_areas.Contains(area)) return;
        if (_areas.Count >= _most)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        _areas.Add(area);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Finish(keep: false);
        else if (e.KeyCode == Keys.Enter)
            Finish(keep: true);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Finish(keep: true);
    }

    private void Finish(bool keep)
    {
        if (_finished)
            return;
        _finished = true;
        _finder?.Dispose();
        _finder = null;
        _words?.Close();
        Close();
        if (keep)
            Done?.Invoke(_areas.ToList());
    }
}
