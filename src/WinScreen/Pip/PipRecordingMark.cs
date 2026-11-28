using System.Drawing.Drawing2D;
using WinScreen.Native;

namespace WinScreen.Pip;

// The red dot on a recording view: a window of its own, like the state chip, because nothing can be drawn over the
// picture. Clicks go through it to the view.
internal sealed class PipRecordingMark : Form
{
    private const int Diameter = 12, Inset = 8;
    private static readonly Color Red = Color.FromArgb(232, 17, 35);

    public PipRecordingMark(Form view)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Owner = view;
        BackColor = Red;
        Text = "";
    }

    // Inside the view's top right corner, clear of the border.
    internal void Follow(Rectangle view)
    {
        int size = Dpi.Scale(Diameter, DeviceDpi), inset = Dpi.Scale(Inset, DeviceDpi) + PipGeometry.Border;
        var bounds = new Rectangle(view.Right - inset - size, view.Top + inset, size, size);
        if (ClientSize != bounds.Size)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, size, size);
            Region = new Region(path);
        }
        Bounds = bounds;
        if (Visible) return;
        Show();
        if (Owner is { } owner) Windows.TopLayer.RaiseOver(Handle, owner.Handle);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST |
                User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT);
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);
    }
}
