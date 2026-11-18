using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace WinScreen.Glass;

public enum BackdropFit { Fill = 0, Fit = 1, Stretch = 2, Tile = 3, Center = 4 }

// Volume is a percent, heard only from the one window ContentEffects lets play sound.
internal sealed record BackdropChoice(string File, BackdropFit Fit, int Volume = 0)
{
    public bool SameFile(BackdropChoice? other) => other is not null && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);

    public static readonly string[] Pictures = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];
    public static readonly string[] Animations = [".gif"];
    public static readonly string[] Videos = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"];

    public bool IsSet => !string.IsNullOrWhiteSpace(File);

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

// Lives on the effect's drawing thread: everything here uses the device's context from that thread only.
internal abstract class Backdrop : IDisposable
{
    protected ID3D11Texture2D? Texture;

    public ID3D11ShaderResourceView? View { get; private set; }

    public Size Size { get; private set; }

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

    public static Backdrop? Open(string file)
    {
        string kind = Path.GetExtension(file).ToLowerInvariant();
        if (BackdropChoice.Videos.Contains(kind)) return new VideoBackdrop(file);
        using var image = Image.FromFile(file);
        int frames = image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? image.GetFrameCount(FrameDimension.Time) : 1;
        return frames > 1 ? new AnimationBackdrop(image, frames) : new PictureBackdrop(image);
    }

    protected void Allocate(ID3D11Device device, Size size)
    {
        View?.Dispose();
        Texture?.Dispose();
        Size = size;
        Texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 0,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        View = device.CreateShaderResourceView(Texture);
    }

    protected void Upload(ID3D11DeviceContext context, nint pixels, int stride)
    {
        context.UpdateSubresource(Texture!, 0, null, pixels, (uint)stride, 0);
        context.GenerateMips(View!);
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
        View?.Dispose();
        Texture?.Dispose();
        View = null;
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

    public AnimationBackdrop(Image image, int frames)
    {
        _size = Limited(image.Size);
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

    public override bool Update(ID3D11Device device, ID3D11DeviceContext context)
    {
        if (Texture is null) Allocate(device, _size);
        if (_shown >= 0 && (_paused || Environment.TickCount64 < _due)) return false;
        _shown = (_shown + 1) % _frames.Count;
        _due = Environment.TickCount64 + _frames[_shown].Milliseconds;
        Upload(context, _frames[_shown].Pixels);
        return true;
    }
}

internal sealed class VideoBackdrop(string file) : Backdrop
{
    private const int Poll = 8;
    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup().Success);
    private IMFDXGIDeviceManager? _manager;
    private IMFMediaEngine? _engine;
    private ID3D11Texture2D? _frame;
    private volatile bool _failed;
    private bool _paused;
    private int _percent = -1;

    public bool Failed => _failed;

    public override int Wait => _paused || _failed ? Timeout.Infinite : Poll;

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
        };
        _engine.Volume = 0;
        _engine.Muted = true;
        _percent = 0;
        _engine.Loop = true;
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
