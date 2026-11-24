using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace WinScreen.Glass;

public enum BackdropFit { Fill = 0, Fit = 1, Stretch = 2, Tile = 3, Center = 4 }

public enum BackdropOrder { InOrder = 0, Shuffle = 1 }

// When a set goes on to its next: as a media player does, or after the minutes chosen.
public enum BackdropSwitch { VideoEnds = 0, Timed = 1 }

// One picture or video, a file or a link to one. Volume, Speed and Brightness are percents; sound is heard only from
// the one window ContentEffects lets play it.
internal sealed record BackdropChoice(string File, BackdropFit Fit, int Volume = 0, int Speed = 100, int Brightness = 100)
{
    public bool SameFile(BackdropChoice? other) => other is not null && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);

    public static readonly string[] Pictures = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];
    public static readonly string[] Animations = [".gif"];
    public static readonly string[] Videos = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"];

    public bool IsSet => !string.IsNullOrWhiteSpace(File);

    public bool IsLink => IsLinkText(File);

    public static bool IsLinkText(string? file) =>
        Uri.TryCreate(file?.Trim(), UriKind.Absolute, out Uri? link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp);

    // A link's kind is its path's extension; one without a picture's extension (a stream, say) is taken as a video.
    public bool IsVideo => IsVideoText(File);

    public static bool IsVideoText(string file)
    {
        string kind = Kind(file);
        return Videos.Contains(kind) || IsLinkText(file) && !Pictures.Contains(kind) && !Animations.Contains(kind);
    }

    public static string Kind(string file) =>
        (IsLinkText(file) ? Path.GetExtension(new Uri(file.Trim()).AbsolutePath) : Path.GetExtension(file)).ToLowerInvariant();

    // A file on this PC that isn't there any more: moved, renamed or deleted. Whether a link works is only known when
    // it is opened.
    public bool IsMissing => IsSet && !IsLink && !System.IO.File.Exists(File);

    // Where the picture goes in a window of the given size, in the window's pixels. A tiled picture repeats from there.
    public static RectangleF Place(BackdropFit fit, Size picture, Size window)
    {
        if (picture.Width <= 0 || picture.Height <= 0) return RectangleF.Empty;
        float scale = fit switch
        {
            BackdropFit.Fill => Math.Max(window.Width / (float)picture.Width, window.Height / (float)picture.Height),
            BackdropFit.Fit => Math.Min(window.Width / (float)picture.Width, window.Height / (float)picture.Height),
            _ => 1,
        };
        if (fit == BackdropFit.Stretch) return new RectangleF(0, 0, window.Width, window.Height);
        if (fit == BackdropFit.Tile) return new RectangleF(0, 0, picture.Width, picture.Height);
        float width = picture.Width * scale, height = picture.Height * scale;
        return new RectangleF((window.Width - width) / 2, (window.Height - height) / 2, width, height);
    }
}

// What a window shows behind Glass: one picture or video, or several taking turns. With several, a picture stays for
// Minutes; a video plays once, or with Timed plays round and round for Minutes.
internal sealed record BackdropSet(IReadOnlyList<BackdropChoice> Items, BackdropOrder Order = BackdropOrder.InOrder, int Minutes = 5,
    BackdropSwitch Switch = BackdropSwitch.VideoEnds)
{
    public bool PlaysOnce => Items.Count > 1 && Switch == BackdropSwitch.VideoEnds;

    // How long the item stays before the next, or null to wait for its end.
    public long? StayMilliseconds(BackdropChoice item) =>
        Items.Count < 2 || PlaysOnce && item.IsVideo ? null : Math.Max(1, Minutes) * 60_000L;

    public bool HasSound => Items.Any(item => item.Volume > 0 && item.IsVideo);

    // The same pictures and videos in the same order: what shows can carry on, with any new fit, sound or speed.
    public bool SameItems(BackdropSet? other) => other is not null && Order == other.Order && Switch == other.Switch && Items.Count == other.Items.Count &&
        Items.Zip(other.Items).All(pair => pair.First.SameFile(pair.Second));

    public bool Equals(BackdropSet? other) =>
        other is not null && Order == other.Order && Minutes == other.Minutes && Switch == other.Switch && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Order, Minutes, Switch, Items.Count, Items.FirstOrDefault());
}

// Which of a set shows next: in order, or shuffled so that each shows once a round and none twice running.
internal sealed class BackdropSequence(int count, BackdropOrder order, Random random)
{
    private readonly List<int> _round = [];
    private int _last = -1;
    private int _asked = -1;

    // The next is this one, wherever the order had got to; the order goes on from it.
    public void Ask(int index) => _asked = index;

    public int Next()
    {
        if (count <= 1) return _last = 0;
        if (_asked >= 0 && _asked < count)
        {
            (_last, _asked) = (_asked, -1);
            _round.Remove(_last);
            return _last;
        }
        if (order == BackdropOrder.InOrder) return _last = (_last + 1) % count;
        if (_round.Count == 0)
        {
            _round.AddRange(Enumerable.Range(0, count).OrderBy(_ => random.Next()));
            if (_round[0] == _last) (_round[0], _round[^1]) = (_round[^1], _round[0]);
        }
        _last = _round[0];
        _round.RemoveAt(0);
        return _last;
    }
}

// Lives on the effect's drawing thread: everything here uses the device's context from that thread only.
internal abstract class Backdrop : IDisposable
{
    protected ID3D11Texture2D? Texture;
    private ID3D11ShaderResourceView? _view;
    private Size _size;

    public virtual ID3D11ShaderResourceView? View => _view;

    public virtual Size Size => _size;

    // How long the drawing thread may wait before the next frame could be due.
    public virtual int Wait => Timeout.Infinite;

    // True when the picture changed and the window needs drawing again.
    public abstract bool Update(ID3D11Device device, ID3D11DeviceContext context);

    public virtual void Pause(bool paused)
    {
    }

    public virtual bool HasSound => false;

    public virtual void Sound(int percent)
    {
    }

    public virtual void Speed(int percent)
    {
    }

    // A video asked to play once, which has.
    public virtual bool Ended => false;

    // The same source for a new fit or larger displays, or null when what it has will do.
    public virtual Backdrop? Again(BackdropChoice item) => null;

    // Once is for a set, where the next one follows; alone, a video plays round and round.
    public static Backdrop Open(BackdropChoice item, bool once = false)
    {
        if (item.IsMissing) throw new FileNotFoundException(null, item.File);
        if (item.IsVideo) return new VideoBackdrop(item.File.Trim(), once, item.Speed);
        if (item.IsLink) return new LinkedPicture(item);
        return FromStream(OpenFile(item.File), item);
    }

    // It can be moved or deleted while it shows: a long animation goes on reading it.
    protected static FileStream OpenFile(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // The stream is the backdrop's from here on.
    internal static Backdrop FromStream(Stream file, BackdropChoice item)
    {
        Image? image = null;
        try
        {
            image = Image.FromStream(file, useEmbeddedColorManagement: false, validateImageData: false);
            int frames = image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? image.GetFrameCount(FrameDimension.Time) : 1;
            if (frames < 2) return new PictureBackdrop(image, item.Fit);
            var animation = new AnimationBackdrop(image, file, frames, item.Speed);
            if (animation.TakesImage) (image, file) = (null, Stream.Null);
            return animation;
        }
        finally
        {
            image?.Dispose();
            file.Dispose();
        }
    }

    protected void Allocate(ID3D11Device device, Size size)
    {
        _view?.Dispose();
        Texture?.Dispose();
        _size = size;
        Texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 0,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        _view = device.CreateShaderResourceView(Texture);
    }

    protected void Upload(ID3D11DeviceContext context, nint pixels, int stride)
    {
        context.UpdateSubresource(Texture!, 0, null, pixels, (uint)stride, 0);
        context.GenerateMips(_view!);
    }

    protected static byte[] Pixels(Image image, Size size)
    {
        using var copy = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(copy))
            graphics.DrawImage(image, 0, 0, size.Width, size.Height);
        BitmapData bits = copy.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[size.Width * size.Height * 4];
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(bits.Scan0 + y * bits.Stride, pixels, y * size.Width * 4, size.Width * 4);
            return pixels;
        }
        finally
        {
            copy.UnlockBits(bits);
        }
    }

    protected static Size Limited(Size size)
    {
        const int Largest = 8192;
        float scale = Math.Min(1, Largest / (float)Math.Max(size.Width, size.Height));
        return new Size(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
    }

    protected unsafe void Upload(ID3D11DeviceContext context, byte[] pixels)
    {
        fixed (byte* start = pixels)
            Upload(context, (nint)start, Size.Width * 4);
    }

    public virtual void Dispose()
    {
        _view?.Dispose();
        Texture?.Dispose();
        _view = null;
        Texture = null;
    }
}

internal sealed class PictureBackdrop : Backdrop
{
    private readonly Size _size, _source, _room;
    private Bitmap? _picture;

    public PictureBackdrop(Image image, BackdropFit fit)
    {
        _source = image.Size;
        _room = Room(fit);
        _size = Kept(_source, fit, _room);
        _picture = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(_picture);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Without this the edge pixels blend with nothing and turn see-through.
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(image, new Rectangle(Point.Empty, _size), 0, 0, _source.Width, _source.Height, GraphicsUnit.Pixel, attributes);
    }

    // Every display together: a window can be as large as that, and no larger.
    private static Size Room(BackdropFit fit) => fit is BackdropFit.Center or BackdropFit.Tile ? Size.Empty : SystemInformation.VirtualScreen.Size;

    // As large as it can ever show: centred and tiled pictures show their own pixels, so they stay whole.
    internal static Size Kept(Size picture, BackdropFit fit, Size room)
    {
        if (fit is BackdropFit.Center or BackdropFit.Tile) return Limited(picture);
        float wide = room.Width / (float)picture.Width, tall = room.Height / (float)picture.Height;
        (float x, float y) = fit switch
        {
            BackdropFit.Fill => (Math.Max(wide, tall), Math.Max(wide, tall)),
            BackdropFit.Fit => (Math.Min(wide, tall), Math.Min(wide, tall)),
            _ => (wide, tall),
        };
        return Limited(new Size(Math.Max(1, (int)Math.Ceiling(picture.Width * Math.Min(1, x))), Math.Max(1, (int)Math.Ceiling(picture.Height * Math.Min(1, y)))));
    }

    internal static bool Outgrown(Size source, Size kept, BackdropFit fit) => Kept(source, fit, SystemInformation.VirtualScreen.Size) != kept;

    public override Backdrop? Again(BackdropChoice item) =>
        Outgrown(_source, _size, item.Fit) ? FromStream(OpenFile(item.File), item) : null;

    internal Size Source => _source;

    internal Size KeptSize => _size;

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_picture is not { } picture) return false;
        Allocate(device, _size);
        BitmapData bits = picture.LockBits(new Rectangle(Point.Empty, _size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Upload(context, bits.Scan0, bits.Stride); }
        finally { picture.UnlockBits(bits); }
        picture.Dispose();
        _picture = null;
        return true;
    }

    public override void Dispose()
    {
        _picture?.Dispose();
        _picture = null;
        base.Dispose();
    }
}

// Small ones are kept whole; others are decoded as they play, a few frames ahead, on a thread of their own.
internal sealed class AnimationBackdrop : Backdrop
{
    internal const long MostKept = 64L * 1024 * 1024;
    private const int Ahead = 3;
    private readonly int[] _delays;
    private readonly Size _size;
    private readonly byte[][]? _all;
    private readonly BlockingCollection<(int Frame, byte[] Pixels)>? _ready;
    private readonly ConcurrentBag<byte[]> _spare = [];
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _failed;
    private int _shown = -1;
    private long _due;
    private bool _paused;
    private int _speed;

    public AnimationBackdrop(Image image, Stream file, int frames, int speed = 100)
    {
        _size = Limited(image.Size);
        _speed = Math.Max(1, speed);
        PropertyItem? delays = image.PropertyIdList.Contains(0x5100) ? image.GetPropertyItem(0x5100) : null;
        _delays = [.. Enumerable.Range(0, frames).Select(i =>
        {
            int hundredths = delays?.Value is { } value && value.Length >= (i + 1) * 4 ? BitConverter.ToInt32(value, i * 4) : 10;
            // Like browsers: a delay this short is played as a tenth of a second.
            return hundredths <= 1 ? 100 : hundredths * 10;
        })];
        if ((long)_size.Width * _size.Height * 4 * frames <= MostKept)
        {
            _all = new byte[frames][];
            for (int i = 0; i < frames; i++)
            {
                image.SelectActiveFrame(FrameDimension.Time, i);
                _all[i] = Pixels(image, _size);
            }
            return;
        }
        _ready = new BlockingCollection<(int, byte[])>(Ahead);
        TakesImage = true;
        CancellationToken stop = _stop.Token;
        new Thread(() => Decode(image, file, stop)) { IsBackground = true, Name = "WinScreen background frames", Priority = ThreadPriority.BelowNormal }.Start();
    }

    // The image and its file are the decoding thread's from here on, and it disposes them.
    internal bool TakesImage { get; }

    internal int BuffersMade { get; private set; }

    private void Decode(Image image, Stream file, CancellationToken stop)
    {
        try
        {
            using (file)
            using (image)
            using (var copy = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(copy))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                for (int i = 0; ; i = (i + 1) % _delays.Length)
                {
                    image.SelectActiveFrame(FrameDimension.Time, i);
                    graphics.DrawImage(image, 0, 0, _size.Width, _size.Height);
                    if (!_spare.TryTake(out byte[]? pixels))
                    {
                        pixels = new byte[_size.Width * _size.Height * 4];
                        BuffersMade++;
                    }
                    BitmapData bits = copy.LockBits(new Rectangle(Point.Empty, _size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try { Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length); }
                    finally { copy.UnlockBits(bits); }
                    _ready!.Add((i, pixels), stop);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        catch (Exception failure) when (failure is ExternalException or ArgumentException or OutOfMemoryException or InvalidOperationException)
        {
            Diagnostics.Log.Error("effect.backdropFailed", failure);
            _failed = true;
        }
    }

    // Due but not decoded yet: looked for again soon.
    public override int Wait => _paused ? Timeout.Infinite : (int)Math.Clamp(_due - Environment.TickCount64, 2, 1000);

    public override void Pause(bool paused) => _paused = paused;

    public override void Speed(int percent) => _speed = Math.Max(1, percent);

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_failed) throw new NotSupportedException("The picture couldn't be decoded.");
        if (_shown >= 0 && (_paused || Environment.TickCount64 < _due)) return false;
        int next;
        byte[] pixels;
        if (_all is not null) pixels = _all[next = (_shown + 1) % _all.Length];
        else if (_ready!.TryTake(out (int Frame, byte[] Pixels) taken)) (next, pixels) = taken;
        else return false;
        if (Texture is null) Allocate(device, _size);
        Upload(context, pixels);
        if (_ready is not null) _spare.Add(pixels);
        _shown = next;
        // Late by little keeps time; late by much (paused, hidden) starts from now.
        long now = Environment.TickCount64, stay = Math.Max(10, _delays[next] * 100 / _speed);
        _due = now - _due < stay && _due > 0 ? _due + stay : now + stay;
        return true;
    }

    public override void Dispose()
    {
        _stop.Cancel();
        base.Dispose();
    }
}

// A picture from a link, fetched away from the drawing thread (BackdropLink); until it arrives, nothing shows.
internal sealed class LinkedPicture : Backdrop
{
    private readonly Task<(byte[] File, Backdrop Picture)> _fetching;
    private readonly CancellationTokenSource _cancel = new();
    private Backdrop? _picture;

    public LinkedPicture(BackdropChoice item) : this(item, null)
    {
    }

    private LinkedPicture(BackdropChoice item, byte[]? file)
    {
        CancellationToken cancel = _cancel.Token;
        _fetching = Task.Run(async () =>
        {
            byte[] bytes = file ?? await BackdropLink.Fetch(item.File.Trim(), cancel);
            return (bytes, FromStream(new MemoryStream(bytes), item));
        }, cancel);
    }

    public override ID3D11ShaderResourceView? View => _picture?.View;

    public override Size Size => _picture?.Size ?? Size.Empty;

    public override int Wait => _picture?.Wait ?? (_fetching.IsCompleted ? 1 : 100);

    public override void Pause(bool paused) => _picture?.Pause(paused);

    public override void Speed(int percent) => _picture?.Speed(percent);

    // From what was fetched, never asked for again.
    public override Backdrop? Again(BackdropChoice item) =>
        _picture is PictureBackdrop picture && PictureBackdrop.Outgrown(picture.Source, picture.KeptSize, item.Fit)
            ? new LinkedPicture(item, _fetching.Result.File) : null;

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_picture is null)
        {
            if (!_fetching.IsCompleted) return false;
            if (!_fetching.IsCompletedSuccessfully)
                throw new NotSupportedException("The picture couldn't be fetched.", _fetching.Exception?.GetBaseException());
            _picture = _fetching.Result.Picture;
        }
        return _picture.Update(device, context);
    }

    public override void Dispose()
    {
        _cancel.Cancel();
        if (_picture is not null) _picture.Dispose();
        else _fetching.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully) done.Result.Picture.Dispose();
            else if (done.Exception is { } failure) Diagnostics.Log.Swallowed(failure);
        }, TaskScheduler.Default);
        _cancel.Dispose();
        base.Dispose();
    }
}

internal sealed class VideoBackdrop(string file, bool once = false, int speed = 100) : Backdrop
{
    private const int Poll = 8;
    private const int StallMilliseconds = 20_000;
    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup().Success);
    private IMFDXGIDeviceManager? _manager;
    private IMFMediaEngine? _engine;
    private ID3D11Texture2D? _frame;
    private volatile bool _failed;
    private volatile bool _ended;
    private bool _paused;
    private int _percent = -1;
    private int _speed = speed;
    private double _at = -1;
    private long _movedAt, _lookedAt;

    public bool Failed => _failed;

    public override bool Ended => _ended;

    public override int Wait => _paused && !_failed ? Timeout.Infinite : Poll;

    public override void Pause(bool paused)
    {
        if (_paused == paused || _engine is null) return;
        _paused = paused;
        if (paused) _engine.Pause();
        else _engine.Play();
    }

    public override bool HasSound => true;

    public override void Sound(int percent)
    {
        if (_engine is null || percent == _percent) return;
        _percent = percent;
        _engine.Volume = percent / 100.0;
        _engine.Muted = percent <= 0;
    }

    public override void Speed(int percent)
    {
        if (percent == _speed) return;
        _speed = percent;
        if (_engine is null) return;
        _engine.DefaultPlaybackRate = percent / 100.0;
        _engine.PlaybackRate = percent / 100.0;
    }

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_failed) return false;
        if (_engine is null)
        {
            Start(device);
            return false;
        }
        if (Stalled()) throw new NotSupportedException("The video stopped coming.");
        // Until the file's first details are read there is no size, and asking fails. A new frame is S_OK; S_FALSE, also a
        // success, means none.
        if (_engine.ReadyState < MediaEngineReady.HaveMetadata || _engine.OnVideoStreamTick_(out _).Code != 0) return false;
        _engine.GetNativeVideoSize(out int width, out int height);
        if (width <= 0 || height <= 0) return false;
        if (_frame is null || Size != new Size(width, height))
        {
            _frame?.Dispose();
            Allocate(device, new Size(width, height));
            _frame = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
        }
        _engine.TransferVideoFrame(_frame, null, new Vortice.RawRect(0, 0, width, height), null);
        context.CopySubresourceRegion(Texture!, 0, 0, 0, 0, _frame, 0, null);
        context.GenerateMips(View!);
        return true;
    }

    // A link that stops sending reports no error: the player just waits. Time paused, or asleep with the PC, doesn't count.
    private bool Stalled()
    {
        long now = Environment.TickCount64;
        double at = _engine!.CurrentTime;
        if (_paused || _ended || at != _at || now - _lookedAt > 2000) _movedAt = now;
        (_at, _lookedAt) = (at, now);
        return now - _movedAt > StallMilliseconds;
    }

    private void Start(ID3D11Device device)
    {
        if (!Started.Value) throw new NotSupportedException();
        using (ID3D11Multithread guard = device.QueryInterface<ID3D11Multithread>())
            guard.SetMultithreadProtected(true);
        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(device);
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(3);
        attributes.Set(MediaEngineAttributeKeys.DxgiManager.Guid, _manager);
        attributes.Set(MediaEngineAttributeKeys.VideoOutputFormat.Guid, (uint)Format.B8G8R8A8_UNorm);
        using var factory = new IMFMediaEngineClassFactory(Native.MediaEngineFactory.Create());
        _engine = factory.CreateInstance(MediaEngineCreateFlags.None, attributes);
        _engine!.PlaybackEvent += (kind, _, _) =>
        {
            if (kind == MediaEngineEvent.Error) _failed = true;
            else if (kind == MediaEngineEvent.Ended) _ended = true;
        };
        _engine.Volume = 0;
        _engine.Muted = true;
        _percent = 0;
        _engine.Loop = !once;
        if (_speed != 100)
        {
            _engine.DefaultPlaybackRate = _speed / 100.0;
            _engine.PlaybackRate = _speed / 100.0;
        }
        _engine.SetSource(file);
        _engine.Play();
    }

    public override void Dispose()
    {
        _engine?.Shutdown();
        _engine?.Dispose();
        _manager?.Dispose();
        _frame?.Dispose();
        _engine = null;
        _manager = null;
        _frame = null;
        base.Dispose();
    }
}
