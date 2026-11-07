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

    internal bool Dragging { get; private set; }

    public PipTitleBar(Form view, Func<bool> locked, Action minimize)
    {
        _view = view;
        _locked = locked;
        Owner = view;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        _close = MakeButton("×", T("pip.close").Replace("&", ""), () => view.Close());
        _minimize = MakeButton("−", T("pip.minimize"), minimize);
        _title = new Label { ForeColor = Color.White, AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _icon = new PictureBox { Dock = DockStyle.Left, SizeMode = PictureBoxSizeMode.CenterImage };
        Controls.AddRange([_title, _icon, _minimize, _close]);
        foreach (Control control in new Control[] { this, _title, _icon })
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || _locked()) return;
                _start = control.PointToScreen(e.Location);
                _original = _view.Location;
                Dragging = true;
                control.Capture = true;
            };
            control.MouseMove += (_, e) =>
            {
                if (!Dragging || _locked()) return;
                Point point = control.PointToScreen(e.Location);
                Point place = _original + new Size(point.X - _start.X, point.Y - _start.Y);
                _view.Location = PipGeometry.KeepInside(new Rectangle(place, _view.Size), Screen.FromPoint(point).WorkingArea);
                Location = new Point(_view.Left + PipGeometry.Border, _view.Top + PipGeometry.Border);
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
        _close.Width = _minimize.Width = size;
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
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(32, 32, 32) };
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
        if (disposing) _icon.Image?.Dispose();
        base.Dispose(disposing);
    }
}
