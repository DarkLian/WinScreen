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

    public int Next()
    {
        if (count <= 1) return _last = 0;
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

    // Once is for a set, where the next one follows; alone, a video plays round and round.
    public static Backdrop Open(BackdropChoice item, bool once = false)
    {
        if (item.IsMissing) throw new FileNotFoundException(null, item.File);
        if (item.IsVideo) return new VideoBackdrop(item.File.Trim(), once, item.Speed);
        if (item.IsLink) return new LinkedPicture(item.File.Trim(), item.Speed);
        using var image = Image.FromFile(item.File);
        return FromImage(image, item.Speed);
    }

    internal static Backdrop FromImage(Image image, int speed)
    {
        int frames = image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? image.GetFrameCount(FrameDimension.Time) : 1;
        return frames > 1 ? new AnimationBackdrop(image, frames, speed) : new PictureBackdrop(image);
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

internal sealed class PictureBackdrop(Image image) : Backdrop
{
    private byte[]? _pixels = Pixels(image, Limited(image.Size));
    private readonly Size _size = Limited(image.Size);

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_pixels is null) return false;
        Allocate(device, _size);
        Upload(context, _pixels);
        _pixels = null;
        return true;
    }
}

internal sealed class AnimationBackdrop : Backdrop
{
    private const int MostFrames = 600;
    private readonly List<(byte[] Pixels, int Milliseconds)> _frames = [];
    private readonly Size _size;
    private int _shown = -1;
    private long _due;
    private bool _paused;
    private int _speed;

    public AnimationBackdrop(Image image, int frames, int speed = 100)
    {
        _size = Limited(image.Size);
        _speed = Math.Max(1, speed);
        PropertyItem? delays = image.PropertyIdList.Contains(0x5100) ? image.GetPropertyItem(0x5100) : null;
        for (int i = 0; i < Math.Min(frames, MostFrames); i++)
        {
            image.SelectActiveFrame(FrameDimension.Time, i);
            int hundredths = delays?.Value is { } value && value.Length >= (i + 1) * 4 ? BitConverter.ToInt32(value, i * 4) : 10;
            // Like browsers: a delay this short is played as a tenth of a second.
            _frames.Add((Pixels(image, _size), hundredths <= 1 ? 100 : hundredths * 10));
        }
    }

    public override int Wait => _paused ? Timeout.Infinite : (int)Math.Clamp(_due - Environment.TickCount64, 1, 1000);

    public override void Pause(bool paused) => _paused = paused;

    public override void Speed(int percent) => _speed = Math.Max(1, percent);

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (Texture is null) Allocate(device, _size);
        if (_shown >= 0 && (_paused || Environment.TickCount64 < _due)) return false;
        _shown = (_shown + 1) % _frames.Count;
        _due = Environment.TickCount64 + Math.Max(10, _frames[_shown].Milliseconds * 100 / _speed);
        Upload(context, _frames[_shown].Pixels);
        return true;
    }
}

// A picture from a link, fetched away from the drawing thread (BackdropLink); until it arrives, nothing shows.
internal sealed class LinkedPicture : Backdrop
{
    private readonly Task<Backdrop> _fetching;
    private readonly CancellationTokenSource _cancel = new();
    private Backdrop? _picture;

    public LinkedPicture(string link, int speed)
    {
        CancellationToken cancel = _cancel.Token;
        _fetching = Task.Run(async () =>
        {
            using var bytes = new MemoryStream(await BackdropLink.Fetch(link, cancel));
            using Image image = Image.FromStream(bytes);
            return FromImage(image, speed);
        }, cancel);
    }

    public override ID3D11ShaderResourceView? View => _picture?.View;

    public override Size Size => _picture?.Size ?? Size.Empty;

    public override int Wait => _picture?.Wait ?? (_fetching.IsCompleted ? 1 : 100);

    public override void Pause(bool paused) => _picture?.Pause(paused);

    public override void Speed(int percent) => _picture?.Speed(percent);

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (_picture is null)
        {
            if (!_fetching.IsCompleted) return false;
            if (!_fetching.IsCompletedSuccessfully)
                throw new NotSupportedException("The picture couldn't be fetched.", _fetching.Exception?.GetBaseException());
            _picture = _fetching.Result;
        }
        return _picture.Update(device, context);
    }

    public override void Dispose()
    {
        _cancel.Cancel();
        if (_picture is not null) _picture.Dispose();
        else _fetching.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully) done.Result.Dispose();
            else if (done.Exception is { } failure) Diagnostics.Log.Swallowed(failure);
        }, TaskScheduler.Default);
        _cancel.Dispose();
        base.Dispose();
    }
}

internal sealed class VideoBackdrop(string file, bool once = false, int speed = 100) : Backdrop
{
    private const int Poll = 8;
    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup().Success);
    private IMFDXGIDeviceManager? _manager;
    private IMFMediaEngine? _engine;
    private ID3D11Texture2D? _frame;
    private volatile bool _failed;
    private volatile bool _ended;
    private bool _paused;
    private int _percent = -1;
    private int _speed = speed;

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
