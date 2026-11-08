using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>A see-through outline of where a dragged window will snap when it's let go.</summary>
internal sealed class SnapPreview : Form
{
    private const int Inset = 6;

    public SnapPreview()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = SystemColors.Highlight;
        Opacity = 0.3;
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

    /// <summary>Shows the outline over this area, or hides it for null.</summary>
    public void ShowAt(Rectangle? area)
    {
        if (area is not { } r)
        {
            Hide();
            return;
        }

        r.Inflate(-Inset, -Inset);
        Bounds = r;
        if (!Visible) Show();
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, User32.SWP_NOACTIVATE);
    }
}
