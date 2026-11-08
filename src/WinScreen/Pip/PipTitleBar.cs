using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

/// <summary>A separate surface, because DWM thumbnails cover controls painted in the view itself.</summary>
internal sealed class PipTitleBar : Form
{
    private readonly Form _view;
    private readonly Func<bool> _locked;
    private readonly Label _title;
    private readonly PictureBox _icon;
    private readonly Button _minimize, _close;
    private Point _start, _original;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private long _leftAt, _fadeAt;
    private double _from, _target;
    internal event Action<Point>? MenuRequested;

    internal void HoldVisible()
    {
        _fade.Stop();
        _leftAt = 0;
        _target = 1;
        Opacity = 1;
        if (!Visible) { Show(); TopLayer.Raise(Handle); }
    }

    internal void Dismiss()
    {
        _fade.Stop();
        _leftAt = 0;
        Opacity = 0;
        Hide();
    }

    internal void FollowHover(bool over, long now)
    {
        if (over || Dragging)
        {
            _leftAt = 0;
            FadeTo(1);
        }
        else if (_leftAt == 0) _leftAt = now;
        else if (now - _leftAt >= 1000) FadeTo(0);
    }

    private void FadeTo(double target)
    {
        if (_target == target && (_fade.Enabled || Opacity == target)) return;
        _from = Visible ? Opacity : 0;
        _target = target;
        _fadeAt = Environment.TickCount64;
        if (target > 0 && !Visible)
        {
            Opacity = 0;
            Show();
            TopLayer.Raise(Handle);
        }
        _fade.Start();
    }

    private void FadeTick()
    {
        double fraction = Math.Clamp((Environment.TickCount64 - _fadeAt) / (_target > _from ? 150.0 : 300.0), 0, 1);
        Opacity = _from + (_target - _from) * fraction;
        if (fraction < 1) return;
        _fade.Stop();
        if (_target == 0) Hide();
    }

    [DllImport("user32.dll")] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern nint DeferWindowPos(nint batch, nint window, nint after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(nint batch);

    internal bool Dragging { get; private set; }

    public PipTitleBar(Form view, Func<bool> locked, Action minimize)
    {
        _fade.Tick += (_, _) => FadeTick();
        Opacity = 0;
        _view = view;
        _locked = locked;
        Owner = view;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(32, 32, 32);
        _close = MakeButton("×", T("pip.close").Replace("&", ""), () => view.Close());
        _minimize = MakeButton("−", T("pip.minimize"), minimize);
        _close.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
        _minimize.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 64, 64);
        _title = new Label { ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.White, AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _icon = new PictureBox { Dock = DockStyle.Left, SizeMode = PictureBoxSizeMode.CenterImage };
        Controls.AddRange([_title, _icon, _minimize, _close]);
        foreach (Control button in new Control[] { _minimize, _close })
            button.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(button.PointToScreen(e.Location));
            };
        foreach (Control control in new Control[] { this, _title, _icon })
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) { MenuRequested?.Invoke(control.PointToScreen(e.Location)); return; }
                if (e.Button != MouseButtons.Left || e.Clicks > 1 || _locked()) return;
                _start = control.PointToScreen(e.Location);
                _original = _view.Location;
                Dragging = true;
                HoldVisible();
                control.Capture = true;
            };
            control.MouseMove += (_, e) =>
            {
                if (!Dragging || _locked()) return;
                Point point = control.PointToScreen(e.Location);
                Point place = _original + new Size(point.X - _start.X, point.Y - _start.Y);
                Rectangle area = Screen.FromPoint(point).WorkingArea;
                place = PipGeometry.KeepInside(new Rectangle(place, _view.Size), area);
                int top = place.Y - Height >= area.Top ? place.Y - Height : place.Y + _view.Height;
                const uint flags = User32.SWP_NOACTIVATE | User32.SWP_NOZORDER | User32.SWP_NOSIZE;
                nint batch = BeginDeferWindowPos(2);
                if (batch != 0) batch = DeferWindowPos(batch, _view.Handle, 0, place.X, place.Y, 0, 0, flags);
                if (batch != 0) batch = DeferWindowPos(batch, Handle, 0, place.X, top, 0, 0, flags);
                if (batch == 0 || !EndDeferWindowPos(batch)) _view.Location = place;

            };
            control.MouseUp += (_, _) => { Dragging = false; control.Capture = false; };
            control.MouseCaptureChanged += (_, _) => { if (!control.Capture) Dragging = false; };
        }
    }

    internal void UpdateTitle(string title, Icon? icon)
    {
        if (_title.Text != title) _title.Text = title;
        int size = Dpi.Scale(28, _view.DeviceDpi);
        Height = size;
        _close.Width = _minimize.Width = Dpi.Scale(46, _view.DeviceDpi);
        _icon.Width = size;
        var iconSize = new Size(Dpi.Scale(16, _view.DeviceDpi), Dpi.Scale(16, _view.DeviceDpi));
        if (icon is not null && _icon.Image?.Size != iconSize)
        {
            using var original = icon.ToBitmap();
            Image? previous = _icon.Image;
            _icon.Image = new Bitmap(original, iconSize);
            previous?.Dispose();
        }
    }

    private static Button MakeButton(string glyph, string name, Action action)
    {
        var button = new Button { Text = glyph, AccessibleName = name, Dock = DockStyle.Right, TabStop = false,
            FlatStyle = FlatStyle.Flat, ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.White,
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(32, 32, 32) };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        return button;
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

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = 3; return; }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fade.Dispose(); _icon.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
