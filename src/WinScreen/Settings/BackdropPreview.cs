using System.Drawing.Drawing2D;
using WinScreen.Glass;
using WinScreen.Tray;

namespace WinScreen.Settings;

// A window drawn the way Glass would show it: lines of text over the chosen picture, placed by the chosen fit. With no
// picture it asks for one; a click chooses one and a file dropped on it is taken.
internal sealed class BackdropPreview : Control
{
    private const int Radius = 8;
    private const int Window = 1280; // The window the preview stands for, for fits that keep the picture's own size.
    private readonly BackgroundPictures _pictures;
    private string _file = "";
    private BackdropFit _fit;
    private string _empty = "";

    public BackdropPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _pictures = new BackgroundPictures(1280, () =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate);
        });
        BackColor = SettingsStyle.Card;
        Margin = new Padding(3, 6, 3, 10);
        Size = new Size(SwitchBox.RowWidth, SwitchBox.RowWidth * 9 / 16);
        AccessibleRole = AccessibleRole.Graphic;
        TabStop = false;
        Cursor = Cursors.Hand;
        AllowDrop = true;
    }

    public event Action? ChooseAsked;

    public event Action<string>? Dropped;

    public void Show(string file, BackdropFit fit, string empty)
    {
        _file = file;
        _fit = fit;
        _empty = empty;
        AccessibleDescription = file.Length > 0 ? Path.GetFileName(file) : empty;
        Invalidate();
    }

    public static bool Takes(string file)
    {
        string kind = Path.GetExtension(file).ToLowerInvariant();
        return BackdropChoice.Pictures.Contains(kind) || BackdropChoice.Animations.Contains(kind) || BackdropChoice.Videos.Contains(kind);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ChooseAsked?.Invoke();
    }

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && Takes(files[0]) ? files[0] : null;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = DroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (DroppedFile(e) is { } file) Dropped?.Invoke(file);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // As tall as it was made; a wider page centres it rather than pushing the settings under it out of sight.
        int wide = Math.Min(Width - 1, (Height - 1) * 16 / 9);
        var box = new Rectangle((Width - 1 - wide) / 2, 0, wide, Height - 1);
        using GraphicsPath outline = SettingsStyle.Rounded(box, Unit(Radius));
        Color page = SettingsStyle.Page;
        using (var fill = new SolidBrush(page)) g.FillPath(fill, outline);
        if (_file.Length == 0)
        {
            DrawEmpty(g, box, outline);
            return;
        }
        g.SetClip(outline);
        if (_pictures.Read(_file) is { Picture: { } picture, Real: var real }) DrawPicture(g, picture, real, box);
        DrawWindow(g, box, page);
        g.ResetClip();
        using var pen = new Pen(SettingsStyle.CardStroke);
        g.DrawPath(pen, outline);
    }

    private void DrawEmpty(Graphics g, Rectangle box, GraphicsPath outline)
    {
        using (var pen = new Pen(SettingsStyle.StrokeOn(SettingsStyle.Page)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, outline);
        int icon = Unit(32);
        if (ColorIcons.Of(MenuParts.Picture, icon) is { } picture)
            g.DrawImage(picture, new Rectangle(box.X + (box.Width - icon) / 2, box.Y + box.Height / 2 - icon - Unit(6), icon, icon));
        Native.DirectText.DrawText(g, _empty, SettingsStyle.BodyFont,
            new Rectangle(box.X + Unit(24), box.Y + box.Height / 2 + Unit(4), box.Width - Unit(48), Unit(48)), SettingsStyle.Secondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    private void DrawPicture(Graphics g, Bitmap picture, Size real, Rectangle box)
    {
        float scale = box.Width / (float)Window;
        var window = new Size(Window, (int)(box.Height / scale));
        RectangleF place = BackdropChoice.Place(_fit, real, window);
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
        using (var bar = new SolidBrush(SettingsStyle.Card)) g.FillRectangle(bar, box.X, box.Y, box.Width, Unit(26));
        using (var dot = new SolidBrush(SettingsStyle.Tertiary))
            for (int i = 0; i < 3; i++)
                g.FillEllipse(dot, box.Right - Unit(18 + i * 16), box.Y + Unit(10), Unit(7), Unit(7));
        float[] lines = [0.42f, 0.74f, 0.66f, 0.7f, 0.52f];
        using var halo = new SolidBrush(Color.FromArgb(220, page));
        using var ink = new SolidBrush(SettingsStyle.Text);
        for (int i = 0; i < lines.Length; i++)
        {
            int height = Unit(i == 0 ? 11 : 7);
            var line = new Rectangle(box.X + Unit(22), box.Y + Unit(48 + i * 22), (int)(box.Width * lines[i]), height);
            using (GraphicsPath edge = SettingsStyle.Rounded(Rectangle.Inflate(line, Unit(3), Unit(3)), Unit(5))) g.FillPath(halo, edge);
            using (GraphicsPath text = SettingsStyle.Rounded(line, height / 2)) g.FillPath(ink, text);
        }
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pictures.Dispose();
        base.Dispose(disposing);
    }
}
