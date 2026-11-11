using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinScreen.Settings;

/// <summary>
/// A thin scroll bar drawn the Windows 11 way: a slim line at rest that widens, with arrows, under the pointer. Windows
/// draws a list's own scroll bar thick and dark, so that one is hidden past the list's edge and this one sits over it,
/// reading where the list is from Windows and moving it the list's own way.
/// </summary>
internal sealed class ThinScrollBar : Control
{
    private const int SB_VERT = 1, SIF_RANGE = 0x1, SIF_PAGE = 0x2, SIF_POS = 0x4;
    private readonly Control _target;
    private readonly Action<int> _scrollTo;
    private readonly Func<Color> _back;
    private readonly System.Windows.Forms.Timer _repeat = new() { Interval = 60 };
    private int _min, _max, _page, _pos;
    private bool _hot, _dragging;
    private int _dragFrom, _dragStart, _repeatStep;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size, Mask;
        public int Min, Max;
        public uint Page;
        public int Pos, TrackPos;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(nint window, int bar, ref ScrollInfo info);

    /// <param name="target">What scrolls.</param>
    /// <param name="scrollTo">Moves it so that the given position, in its own scroll units, is at the top.</param>
    /// <param name="back">The colour behind the bar.</param>
    internal ThinScrollBar(Control target, Action<int> scrollTo, Func<Color> back)
    {
        _target = target;
        _scrollTo = scrollTo;
        _back = back;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Visible = false;
        _repeat.Tick += (_, _) => { _repeat.Interval = 50; Step(_repeatStep); };
        target.HandleCreated += (_, _) => Sync();
    }

    internal const int BarWidth = 12;
    private int Unit(int value) => SettingsStyle.Unit(this, value);
    private int Range => _max - _min + 1;
    private bool Needed => _page > 0 && Range > _page;
    private int Arrow => _hot ? Unit(14) : Unit(4);

    /// <summary>Rereads where the target is; called whenever it may have moved.</summary>
    internal void Sync()
    {
        if (!_target.IsHandleCreated || IsDisposed) return;
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = SIF_RANGE | SIF_PAGE | SIF_POS };
        if (!GetScrollInfo(_target.Handle, SB_VERT, ref info)) info = default;
        bool changed = info.Min != _min || info.Max != _max || (int)info.Page != _page || info.Pos != _pos;
        (_min, _max, _page, _pos) = (info.Min, info.Max, (int)info.Page, info.Pos);
        if (Visible != Needed) Visible = Needed;
        if (changed) Invalidate();
    }

    private Rectangle Track => new(0, Arrow, Width, Math.Max(1, Height - Arrow * 2));

    private Rectangle Thumb
    {
        get
        {
            Rectangle track = Track;
            int length = Math.Max(Unit(24), (int)((long)track.Height * _page / Math.Max(1, Range)));
            length = Math.Min(length, track.Height);
            int travel = Math.Max(1, Range - _page);
            int top = track.Top + (int)((long)(track.Height - length) * Math.Clamp(_pos - _min, 0, travel) / travel);
            return new Rectangle(0, top, Width, length);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color back = _back();
        g.Clear(back);
        if (!Needed) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = SettingsStyle.Blend(SettingsStyle.Text, back, _hot || _dragging ? 0.62 : 0.5);
        int width = _hot || _dragging ? Unit(6) : Unit(2);
        Rectangle thumb = Thumb;
        var bar = new Rectangle((Width - width) / 2, thumb.Top, width, thumb.Height);
        using (GraphicsPath path = SettingsStyle.Rounded(bar, Math.Max(1, width / 2)))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);
        if (!_hot && !_dragging) return;
        // Small filled arrows at both ends, as Windows 11 shows them under the pointer.
        float x = Width / 2f, half = Unit(4), tall = Unit(3);
        using var arrow = new SolidBrush(color);
        float top = Arrow / 2f, bottom = Height - Arrow / 2f;
        g.FillPolygon(arrow, [new PointF(x - half, top + tall / 2), new PointF(x + half, top + tall / 2), new PointF(x, top - tall / 2)]);
        g.FillPolygon(arrow, [new PointF(x - half, bottom - tall / 2), new PointF(x + half, bottom - tall / 2), new PointF(x, bottom + tall / 2)]);
    }

    private void Step(int by)
    {
        int to = Math.Clamp(_pos + by, _min, Math.Max(_min, _max - _page + 1));
        if (to != _pos) _scrollTo(to);
        Sync();
    }

    private int SmallStep => Math.Max(1, _page / 12);

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Needed) return;
        Rectangle thumb = Thumb;
        if (thumb.Contains(e.Location))
        {
            _dragging = Capture = true;
            (_dragFrom, _dragStart) = (e.Y, _pos);
            return;
        }
        _repeatStep = e.Y < Arrow ? -SmallStep : e.Y >= Height - Arrow ? SmallStep : e.Y < thumb.Top ? -_page : _page;
        Step(_repeatStep);
        _repeat.Interval = 300;
        _repeat.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Rectangle track = Track, thumb = Thumb;
        int room = Math.Max(1, track.Height - thumb.Height);
        int to = _dragStart + (int)((long)(e.Y - _dragFrom) * (Range - _page) / room);
        to = Math.Clamp(to, _min, Math.Max(_min, _max - _page + 1));
        if (to != _pos) { _scrollTo(to); Sync(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _repeat.Stop();
        if (_dragging) { _dragging = Capture = false; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Step(-Math.Sign(e.Delta) * SmallStep * 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A page of cards that scrolls, and says so whenever what it shows may have moved.</summary>
internal sealed class ScrollPage : FlowLayoutPanel
{
    private const int WM_PAINT = 0x000F, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A;
    internal event Action? Moved;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WM_PAINT or WM_VSCROLL or WM_MOUSEWHEEL) Moved?.Invoke();
    }

    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Moved?.Invoke(); }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.LocationChanged += (_, _) => Moved?.Invoke(); // Scrolling to a setting found by a search moves them.
    }
}

/// <summary>Holds a <see cref="ScrollPage"/> with Windows' scroll bar cut off at the edge and a thin one over it.</summary>
internal sealed class ScrollHost : Panel
{
    private readonly ScrollPage _page;
    private readonly ThinScrollBar _bar;

    internal ScrollHost(ScrollPage page)
    {
        _page = page;
        Dock = DockStyle.Fill;
        BackColor = page.BackColor;
        page.Dock = DockStyle.None;
        _bar = new ThinScrollBar(page, position => page.AutoScrollPosition = new Point(0, position), () => BackColor);
        page.Moved += _bar.Sync;
        Controls.Add(page);
        Controls.Add(_bar);
        _bar.BringToFront();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_page is null || _bar is null) return;
        int native = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        _page.SetBounds(0, 0, Width + native, Height);
        int width = SettingsStyle.Unit(this, ThinScrollBar.BarWidth);
        _bar.SetBounds(Width - width, 0, width, Height);
        _bar.Sync();
    }
}

/// <summary>The table of actions, saying whenever it may have scrolled.</summary>
internal sealed class ThemedTable : ListView
{
    private const int WM_PAINT = 0x000F, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A, LVM_SCROLL = 0x1014;
    internal event Action? Moved;

    // Windows redraws a row as the pointer passes over it; drawn straight onto the screen, its words blinked.
    public ThemedTable() => DoubleBuffered = true;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT) Moved?.Invoke();
        // Scrolling moves what is already on screen, including a row only partly drawn; all of it is drawn again
        // instead, in one go, so nothing is left behind.
        else if (m.Msg is WM_VSCROLL or WM_MOUSEWHEEL or LVM_SCROLL) Invalidate();
    }

    /// <summary>Puts the row at <paramref name="row"/> at the top, the way the table's own scroll bar would.</summary>
    internal void ScrollToRow(int row)
    {
        if (Items.Count == 0 || !IsHandleCreated) return;
        int top = TopItem?.Index ?? 0;
        int height = Items[0].Bounds.Height;
        Native.User32.SendMessage(Handle, LVM_SCROLL, 0, (row - top) * height);
    }
}
