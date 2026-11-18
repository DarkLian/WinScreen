using WinScreen.Native;

namespace WinScreen.Tray;

internal sealed class ActionFlash : Form
{
    private const int VisibleMilliseconds = 1000;
    private const int Gap = 12;

    private readonly Label _label = new()
    {
        AutoSize = true, ForeColor = Color.White, BackColor = Color.FromArgb(32, 32, 32),
        Padding = new Padding(10, 6, 10, 6),
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = VisibleMilliseconds };

    public ActionFlash()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = _label.BackColor;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(_label);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW |
                                        User32.WS_EX_TRANSPARENT);
            return parameters;
        }
    }

    public void ShowOver(Rectangle area, string text)
    {
        _label.Text = text;
        Rectangle screen = Screen.FromRectangle(area).WorkingArea;
        Size size = GetPreferredSize(Size.Empty);
        Location = Pip.PipGeometry.KeepInside(
            new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + Gap, size.Width, size.Height), screen);
        Show();
        WinScreen.Windows.TopLayer.Raise(Handle);
        _timer.Stop();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
