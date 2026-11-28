using System.Drawing.Drawing2D;
using WinScreen.Glass;
using WinScreen.Tray;

namespace WinScreen.Settings;

internal sealed class BackgroundEntry(string? program, string name, Func<BackgroundChoice> choice, Func<string> detail)
{
    public string? Program => program;

    public string Name => name;

    public BackgroundChoice Choice => choice();

    public string Detail => detail();

    public override string ToString() => name + ", " + detail();
}

// Every app's background first, then each app with its own: whose it is, its file, and a small picture of it.
internal sealed class BackgroundList : ThemedList
{
    private readonly BackgroundPictures _pictures;

    public BackgroundList()
    {
        _pictures = new BackgroundPictures(256, () => BackgroundPictures.Redraw(this));
        ItemHeight = Unit(58);
    }

    private int Unit(int value) => SettingsStyle.Unit(this, value);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Unit(58);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemHeight = Unit(58);
    }

    protected override void DrawRow(Graphics g, Rectangle row, int index)
    {
        if (Items[index] is not BackgroundEntry entry) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int left = row.Left + Unit(12), icon = Unit(24);
        Image? picture = entry.Program is null ? ColorIcons.Of(MenuParts.Picture, icon) : ProgramIcons.Of(entry.Program, icon);
        if (picture is not null) g.DrawImage(picture, new Rectangle(left, row.Top + (row.Height - icon) / 2, icon, icon));
        left += icon + Unit(12);

        int tall = row.Height - Unit(14), wide = tall * 16 / 9;
        var thumbnail = new Rectangle(row.Right - Unit(12) - wide, row.Top + (row.Height - tall) / 2, wide, tall);
        DrawThumbnail(g, thumbnail, entry.Choice.File);

        int right = thumbnail.Left - Unit(12), half = row.Top + row.Height / 2;
        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        Native.DirectText.DrawText(g, entry.Name, Font, new Rectangle(left, row.Top, right - left, half - row.Top + Unit(1)),
            Enabled ? SettingsStyle.Text : SettingsStyle.Disabled, Line | TextFormatFlags.Bottom);
        Native.DirectText.DrawText(g, entry.Detail, SettingsStyle.CaptionFont, new Rectangle(left, half + Unit(2), right - left, row.Bottom - half - Unit(2)),
            Enabled ? SettingsStyle.Secondary : SettingsStyle.Disabled, Line | TextFormatFlags.Top);
    }

    private void DrawThumbnail(Graphics g, Rectangle box, string file)
    {
        using GraphicsPath outline = SettingsStyle.Rounded(box, Unit(4));
        using (var page = new SolidBrush(SettingsStyle.Page)) g.FillPath(page, outline);
        if (file.Length > 0 && _pictures.Of(file) is { } picture)
        {
            // Filled as Fill would: the middle of the picture, cut to the box's shape.
            float scale = Math.Max(box.Width / (float)picture.Width, box.Height / (float)picture.Height);
            float width = picture.Width * scale, height = picture.Height * scale;
            GraphicsState state = g.Save();
            g.SetClip(outline, CombineMode.Intersect);
            g.DrawImage(picture, box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height);
            g.Restore(state);
        }
        using var pen = new Pen(SettingsStyle.CardStroke);
        g.DrawPath(pen, outline);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pictures.Dispose();
        base.Dispose(disposing);
    }
}

// Pictures of chosen files, read once each off the window's thread. A video's is the one File Explorer shows.
internal sealed class BackgroundPictures(int largest, Action arrived) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Bitmap? Picture, Size Real)> _made = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _wanted = new();
    private bool _working, _disposed;

    // From the pictures thread: Settings can close between the check and the post, which there would end WinScreen.
    internal static void Redraw(Control control)
    {
        try
        {
            if (control.IsHandleCreated && !control.IsDisposed) control.BeginInvoke(control.Invalidate);
        }
        catch (Exception closed) when (closed is InvalidOperationException or ObjectDisposedException)
        {
            Diagnostics.Log.Swallowed(closed);
        }
    }

    public Bitmap? Of(string file) => Read(file)?.Picture;

    // Null until it has been read; then the picture, or none if it couldn't be.
    public (Bitmap? Picture, Size Real)? Read(string file)
    {
        lock (_gate)
        {
            if (_made.TryGetValue(file, out var made)) return made;
            if (_disposed) return null;
            if (!_wanted.Contains(file, StringComparer.OrdinalIgnoreCase)) _wanted.Enqueue(file);
            if (_working) return null;
            _working = true;
        }
        var worker = new Thread(Work) { IsBackground = true, Name = "WinScreen pictures" };
        worker.SetApartmentState(ApartmentState.STA); // A video's picture comes from Windows' shell.
        worker.Start();
        return null;
    }

    private void Work()
    {
        while (true)
        {
            string file;
            lock (_gate)
            {
                if (_disposed || _wanted.Count == 0)
                {
                    _working = false;
                    return;
                }
                file = _wanted.Dequeue();
            }
            (Bitmap? picture, Size real) = Load(file, largest);
            lock (_gate)
            {
                if (_disposed)
                {
                    picture?.Dispose();
                    _working = false;
                    return;
                }
                _made[file] = (picture, real);
            }
            arrived();
        }
    }

    // Call on a single-threaded apartment thread.
    internal static (Bitmap?, Size) Load(string file, int largest)
    {
        // A link isn't fetched for a thumbnail, and a file that was moved or deleted has none.
        if (BackdropChoice.IsLinkText(file) || !File.Exists(file)) return (null, Size.Empty);
        // This runs on a thread of its own, where anything uncaught ends WinScreen; GDI+ and the shell report a file
        // they can't read with ExternalException as well as the others.
        try
        {
            if (!BackdropChoice.Videos.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                using Image image = Image.FromFile(file);
                return (Smaller(image, largest), image.Size);
            }
            Bitmap? frame = Native.ShellThumbnail.Of(file, Math.Min(largest, 640));
            // A video's own size isn't known here; a common one in its shape stands in.
            return (frame, frame is null ? Size.Empty : new Size(1920, Math.Max(1, 1920 * frame.Height / frame.Width)));
        }
        catch (Exception failure) when (failure is IOException or OutOfMemoryException or ArgumentException or UnauthorizedAccessException
                                            or System.Runtime.InteropServices.ExternalException)
        {
            Diagnostics.Log.Swallowed(failure);
            return (null, Size.Empty);
        }
    }

    private static Bitmap Smaller(Image image, int largest)
    {
        float scale = Math.Min(1, largest / (float)Math.Max(image.Width, image.Height));
        var copy = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        try
        {
            using Graphics g = Graphics.FromImage(copy);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, copy.Width, copy.Height);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach ((Bitmap? picture, _) in _made.Values) picture?.Dispose();
            _made.Clear();
        }
    }
}
