using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// Stands in for a minimized view on the taskbar, because a view is a tool window with no button of its
/// own. Nothing runs while it waits: Windows asks for a picture only when someone hovers the button.
/// </summary>
internal sealed class PipTaskbarProxy : Form
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_RESTORE = 0xF120;
    private const int WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    private const int WM_DWMSENDICONICLIVEPREVIEWBITMAP = 0x0326;
    private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    private const int DWMWA_HAS_ICONIC_BITMAP = 10;
    private const int SW_SHOWMINNOACTIVE = 7;

    private readonly Func<Size, Bitmap?> _preview;
    private readonly Action _restore;
    private readonly Action _close;
    private readonly Icon _icon;
    private bool _dismissed;

    public PipTaskbarProxy(string title, Icon icon, Rectangle place, Func<Size, Bitmap?> preview, Action restore, Action close)
    {
        _preview = preview;
        _restore = restore;
        _close = close;
        _icon = icon;
        Text = title;
        Icon = icon;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        Bounds = place; // Hovering the button previews the view where it was.
        _ = Handle;
        int on = 1;
        Dwmapi.DwmSetWindowAttribute(Handle, DWMWA_FORCE_ICONIC_REPRESENTATION, in on, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(Handle, DWMWA_HAS_ICONIC_BITMAP, in on, sizeof(int));
        User32.ShowWindow(Handle, SW_SHOWMINNOACTIVE); // Minimizing a view must not take the focus from the app in use.
    }

    /// <summary>The outcome of the last picture given to Windows, for tests and diagnostics. Zero is success.</summary>
    internal int LastPreviewResult { get; private set; } = -1;

    protected override bool ShowWithoutActivation => true;

    /// <summary>Removes the button after the view has come back, without closing the view.</summary>
    public void Dismiss()
    {
        _dismissed = true;
        Dispose();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_SYSCOMMAND when ((int)m.WParam & 0xFFF0) == SC_RESTORE:
                _restore();
                m.Result = 0;
                return;
            case WM_DWMSENDICONICTHUMBNAIL:
            {
                long size = m.LParam.ToInt64();
                LastPreviewResult = Give(new Size((int)((size >> 16) & 0xFFFF), (int)(size & 0xFFFF)),
                    picture => Dwmapi.DwmSetIconicThumbnail(Handle, picture, 0));
                m.Result = 0;
                return;
            }
            case WM_DWMSENDICONICLIVEPREVIEWBITMAP:
                LastPreviewResult = Give(ClientSize,
                    picture => Dwmapi.DwmSetIconicLivePreviewBitmap(Handle, picture, 0, 0));
                m.Result = 0;
                return;
        }

        base.WndProc(ref m);
    }

    private int Give(Size limit, Func<nint, int> give)
    {
        using Bitmap picture = _preview(limit) ?? IconPicture(limit);
        nint handle = ToDesktopBitmap(picture);
        try { return give(handle); }
        finally { User32.DeleteObject(handle); }
    }

    /// <summary>The desktop compositor takes only a top-down, 32-bit, premultiplied bitmap.</summary>
    private static unsafe nint ToDesktopBitmap(Bitmap picture)
    {
        var header = new User32.BitmapInfoHeader
        {
            Size = (uint)sizeof(User32.BitmapInfoHeader), Width = picture.Width, Height = -picture.Height,
            Planes = 1, BitCount = 32,
        };
        nint handle = User32.CreateDIBSection(0, in header, 0, out nint bits, 0, 0);
        if (handle == 0)
            throw new OutOfMemoryException();
        var whole = new Rectangle(Point.Empty, picture.Size);
        System.Drawing.Imaging.BitmapData data = picture.LockBits(whole, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (int row = 0; row < picture.Height; row++)
                Buffer.MemoryCopy((byte*)data.Scan0 + row * data.Stride, (byte*)bits + row * picture.Width * 4,
                    picture.Width * 4L, picture.Width * 4L);
        }
        finally { picture.UnlockBits(data); }

        return handle;
    }

    private Bitmap IconPicture(Size limit)
    {
        var picture = new Bitmap(Math.Max(1, limit.Width), Math.Max(1, limit.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(picture);
        graphics.Clear(Color.FromArgb(32, 32, 32));
        int side = Math.Min(Math.Min(picture.Width, picture.Height), 64);
        graphics.DrawIcon(_icon, new Rectangle((picture.Width - side) / 2, (picture.Height - side) / 2, side, side));
        return picture;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!_dismissed)
        {
            _dismissed = true;
            _close(); // The button's Close means the view. The original is restored as with any close.
        }
    }
}
