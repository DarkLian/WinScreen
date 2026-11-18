using System.Drawing.Drawing2D;
using WinScreen.Glass;

namespace WinScreen.Settings;

// A small window drawn the way Glass would show it: lines of text over the chosen picture, placed by the chosen fit.
internal sealed class BackdropPreview : Control
{
    private const int Wide = 320, Tall = 200, Radius = 6;
    private const int Window = 1280; // The window the preview stands for, for fits that keep the picture's own size.
    private const int Largest = 1280;
    private readonly object _gate = new();
    private string _file = "";
    private BackdropFit _fit;
    private int _ticket;
    private (int Ticket, Bitmap? Picture, Size Real)? _arrived;
    private Bitmap? _picture;
    private Size _real;

    public BackdropPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SettingsStyle.Card;
        Margin = new Padding(7, 6, 3, 6);
        Size = new Size(Wide, Tall);
        Visible = false;
        AccessibleRole = AccessibleRole.Graphic;
        TabStop = false;
    }

    public void Show(string file, BackdropFit fit)
    {
        _fit = fit;
        if (file != _file)
        {
            _file = file;
            _picture?.Dispose();
            _picture = null;
            Visible = file.Length > 0;
            if (file.Length > 0) Load(file);
        }
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Size = new Size(SettingsStyle.Unit(this, Wide), SettingsStyle.Unit(this, Tall));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = new Size(SettingsStyle.Unit(this, Wide), SettingsStyle.Unit(this, Tall));
    }

    private void Load(string file)
    {
        int ticket = ++_ticket;
        var worker = new Thread(() =>
        {
            (Bitmap? picture, Size real) = Read(file);
            lock (_gate)
            {
                if (_arrived?.Picture is { } unused) unused.Dispose();
                _arrived = (ticket, picture, real);
            }
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate);
            }
            catch (InvalidOperationException closed)
            {
                Diagnostics.Log.Swallowed(closed); // Closed meanwhile; what arrived is let go with the control.
            }
        }) { IsBackground = true, Name = "WinScreen preview" };
        worker.SetApartmentState(ApartmentState.STA); // The file's thumbnail comes from Windows' shell.
        worker.Start();
    }

    private static (Bitmap?, Size) Read(string file)
    {
        if (!BackdropChoice.Videos.Contains(Path.GetExtension(file).ToLowerInvariant()))
        {
            try
            {
                using Image image = Image.FromFile(file);
                return (Smaller(image), image.Size);
            }
            catch (Exception failure) when (failure is IOException or OutOfMemoryException or ArgumentException or UnauthorizedAccessException)
            {
                Diagnostics.Log.Swallowed(failure);
                return (null, Size.Empty);
            }
        }
        Bitmap? frame = Native.ShellThumbnail.Of(file, 640);
        // A video's own size isn't known here; a common one in its shape stands in.
        return (frame, frame is null ? Size.Empty : new Size(1920, Math.Max(1, 1920 * frame.Height / frame.Width)));
    }

    private static Bitmap Smaller(Image image)
    {
        float scale = Math.Min(1, Largest / (float)Math.Max(image.Width, image.Height));
        var copy = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using Graphics g = Graphics.FromImage(copy);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, copy.Width, copy.Height);
        return copy;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        lock (_gate)
        {
            if (_arrived is { } arrived)
            {
                _arrived = null;
                if (arrived.Ticket == _ticket)
                {
                    _picture?.Dispose();
                    _picture = arrived.Picture;
                    _real = arrived.Real;
                }
                else arrived.Picture?.Dispose();
            }
        }

        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath outline = Rounded(box, Unit(Radius));
        Color page = SettingsStyle.Page;
        using (var fill = new SolidBrush(page)) g.FillPath(fill, outline);
        g.SetClip(outline);
        if (_picture is { } picture) DrawPicture(g, picture, box);
        DrawWindow(g, box, page);
        g.ResetClip();
        using var pen = new Pen(SettingsStyle.CardStroke);
        g.DrawPath(pen, outline);
    }

    private void DrawPicture(Graphics g, Bitmap picture, Rectangle box)
    {
        float scale = box.Width / (float)Window;
        var window = new Size(Window, (int)(box.Height / scale));
        RectangleF place = BackdropChoice.Place(_fit, _real, window);
        var shown = new RectangleF(box.X + place.X * scale, box.Y + place.Y * scale, place.Width * scale, place.Height * scale);
        if (_fit != BackdropFit.Tile)
        {
            g.DrawImage(picture, shown);
            return;
        }
        using var tiles = new TextureBrush(picture, WrapMode.Tile);
        tiles.ScaleTransform(shown.Width / picture.Width, shown.Height / picture.Height);
        tiles.TranslateTransform(box.X, box.Y, MatrixOrder.Append);
        g.FillRectangle(tiles, box);
    }

    // A title bar, which Glass keeps solid, and a few lines of text, each with the thin edge of page colour Glass gives
    // text so it can be read on a busy picture.
    private void DrawWindow(Graphics g, Rectangle box, Color page)
    {
        using (var bar = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(bar, box.X, box.Y, box.Width, Unit(22));
        using (var dot = new SolidBrush(SettingsStyle.Tertiary))
            for (int i = 0; i < 3; i++)
                g.FillEllipse(dot, box.Right - Unit(16 + i * 14), box.Y + Unit(8), Unit(6), Unit(6));
        float[] lines = [0.45f, 0.78f, 0.7f, 0.56f];
        using var halo = new SolidBrush(Color.FromArgb(220, page));
        using var ink = new SolidBrush(SettingsStyle.Text);
        for (int i = 0; i < lines.Length; i++)
        {
            int height = Unit(i == 0 ? 9 : 6);
            var line = new Rectangle(box.X + Unit(16), box.Y + Unit(38 + i * 18), (int)(box.Width * lines[i]), height);
            using (GraphicsPath edge = Rounded(Rectangle.Inflate(line, Unit(3), Unit(3)), Unit(5))) g.FillPath(halo, edge);
            using (GraphicsPath text = Rounded(line, height / 2)) g.FillPath(ink, text);
        }
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    private static GraphicsPath Rounded(Rectangle box, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture?.Dispose();
            lock (_gate) _arrived?.Picture?.Dispose();
        }
        base.Dispose(disposing);
    }
}
