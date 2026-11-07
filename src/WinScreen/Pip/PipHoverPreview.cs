using WinScreen.Native;

namespace WinScreen.Pip;

internal static class PipPreviewGeometry
{
    internal static Rectangle Lens(Rectangle shown, Point sourcePoint, Size sample)
    {
        int width = Math.Min(shown.Width, sample.Width), height = Math.Min(shown.Height, sample.Height);
        return new Rectangle(Math.Clamp(sourcePoint.X - width / 2, shown.Left, shown.Right - width),
            Math.Clamp(sourcePoint.Y - height / 2, shown.Top, shown.Bottom - height), width, height);
    }

    internal static Rectangle Place(Rectangle avoid, Size size, Rectangle area)
    {
        int x = avoid.Right + 12;
        if (x + size.Width > area.Right) x = avoid.Left - size.Width - 12;
        return new Rectangle(PipGeometry.KeepInside(new Rectangle(new Point(x, avoid.Top), size), area), size);
    }
}

/// <summary>A compositor thumbnail only: no capture, hook, timer, or input forwarding.</summary>
internal sealed class PipHoverPreview : Form
{
    private readonly DwmThumbnail _thumbnail;

    public PipHoverPreview(Form owner, nint source)
    {
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        try
        {
            if (!User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA))
                throw new Windows.WindowOperationException(Localization.UiText.T("errors.pip.renderFailed"));
            _thumbnail = DwmThumbnail.TryCreate(Handle, source) ?? throw new Windows.WindowOperationException(
                Localization.UiText.T("errors.pip.renderFailed"));
        }
        catch { Dispose(); throw; }
    }

    internal void UpdatePreview(Rectangle source, Rectangle destination)
    {
        Bounds = destination;
        _thumbnail.Show(new Rectangle(Point.Empty, ClientSize), source);
        if (!Visible) Show();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST |
                User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED);
            return parameters;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumbnail?.Dispose();
        base.Dispose(disposing);
    }
}
