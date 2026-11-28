using static WinScreen.Localization.UiText;
using WinScreen.Native;
using WinScreen.Pip;

namespace WinScreen.Windows;

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
    private FoundElement? _hintElement;
    private Instructions? _words;

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

    internal FoundElement? ChosenElement { get; private set; }

    public void ShowOnTop()
    {
        Show();
        // Windows can refuse to bring it forward (a key pressed with another app in front): on top is enough.
        try { WindowController.Pin(Handle); }
        catch (WindowOperationException) { TopMost = true; }
        _words = new Instructions(this, T("crop.instructions"));
        _words.ShowOver(_covers);
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
            if (_pressed)
                return;
            _hintElement = hint.IsEmpty ? null : found;
            if (hint == _hint)
                return;
            _hint = hint;
            Invalidate();
        });
    }

    // Moving onto a monitor with other scaling must not change what's covered.
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
        Rectangle part = _selection;
        // A press and release with no real drag takes the highlighted control.
        if (part.Width < SmallestSide && part.Height < SmallestSide && _hint.Width > 0 && _hint.Height > 0)
        {
            part = _hint;
            ChosenElement = _hintElement;
        }
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
        _words?.Close();
        Close();
    }

    // A window of its own over the dimmed one, so app text under the dimming can't show through. Clicks pass through to
    // the overlay.
    internal sealed class Instructions : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int HTTRANSPARENT = -1;
        private const int MA_NOACTIVATE = 3;
        private const int Widest = 520;
        private const int Margin_ = 16;
        private const int Padding_ = 14;

        public Instructions(Form overlay, string words)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Owner = overlay;
            Text = words;
            Font = new Font("Segoe UI", 11f);
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.White;
        }

        public void ShowOver(Rectangle area)
        {
            Place(area);
            if (!Visible)
            {
                Show();
                Place(area); // Measured again at the scale of the display it's now on.
            }
        }

        private void Place(Rectangle area)
        {
            int padding = Dpi.Scale(Padding_, DeviceDpi);
            int widest = Math.Max(Dpi.Scale(120, DeviceDpi),
                Math.Min(Dpi.Scale(Widest, DeviceDpi), area.Width - 2 * Dpi.Scale(Margin_, DeviceDpi)));
            Size text = TextRenderer.MeasureText(Text, Font, new Size(widest - 2 * padding, int.MaxValue),
                TextFormatFlags.WordBreak);
            Size size = new(text.Width + 2 * padding, text.Height + 2 * padding);
            Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2,
                size.Width, size.Height);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= (int)(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST);
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int padding = Dpi.Scale(Padding_, DeviceDpi);
            Rectangle words = Rectangle.Inflate(ClientRectangle, -padding, -padding);
            TextRenderer.DrawText(e.Graphics, Text, Font, words, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = HTTRANSPARENT;
                return;
            }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Font.Dispose();
            base.Dispose(disposing);
        }
    }
}
