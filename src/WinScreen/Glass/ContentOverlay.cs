using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Windows;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace WinScreen.Glass;

internal enum ContentLook { Glass, Dark }

/// <summary>How opaque each kind of content stays under Glass, from 0 to 1.</summary>
internal readonly record struct ContentLevels(float Background, float Text, float Picture);

/// <summary>
/// Draws one window again, processed: Glass fades the page and keeps text and pictures, Dark swaps light
/// and dark but leaves pictures alone. The window itself stays where it is, invisible and clickable,
/// under this see-through, click-through overlay. Work happens only when the window draws a new frame.
/// </summary>
internal sealed unsafe class ContentOverlay : Form
{
    private const long NoRedirectionBitmap = 0x200000;
    internal const uint SoftwareRendererVendor = 0x1414; // Microsoft: the Basic Render Driver.
    private const uint OutOfContext = 0, LocationChange = 0x800B, Foreground = 0x0003, Reorder = 0x8004;
    private const uint MinimizeStart = 0x0016, MinimizeEnd = 0x0017;

    private readonly nint _source;
    private readonly ContentLook _look;
    private readonly PictureFinder _finder = new();
    private readonly AutoResetEvent _arrived = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly User32.WinEventProc _eventProc;
    private readonly List<nint> _hooks = [];
    private volatile bool _stopping;
    private ContentLevels _levels;
    private Thread? _renderer;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _stats, _composite;
    private ID3D11Buffer? _options;
    private IDXGISwapChain1? _chain;
    private nint _composition, _target, _visual;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _captureDevice;

    public ContentOverlay(nint source, ContentLook look, ContentLevels levels)
    {
        _source = source;
        _look = look;
        _levels = levels;
        _eventProc = OnWindowEvent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "WinScreen effect";
    }

    /// <summary>Raised on the UI thread when the effect can't go on, with a message for the person.</summary>
    public event Action<string>? Failed;

    public nint Source => _source;

    public ContentLook Look => _look;

    /// <summary>Frames processed, for tests and diagnostics.</summary>
    internal long Frames { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(NoRedirectionBitmap | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE |
                                        User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED);
            return parameters;
        }
    }

    public void SetLevels(ContentLevels levels) => _levels = levels;

    /// <summary>Sets everything up on this thread, then processes frames on its own. Throws if the window can't be captured.</summary>
    public void Start()
    {
        if (!WindowInfo.TryGetFrameBounds(_source, out Rectangle frame, out _))
            throw new WindowOperationException(Localization.UiText.T("errors.glass.unavailable"));
        Bounds = frame;
        _ = Handle;
        User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);
        try
        {
            CreateDevice(frame.Size);
        }
        catch (Exception failure) when (failure is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Release();
            throw new WindowOperationException(Localization.UiText.T("errors.glass.unavailable"));
        }

        Show();
        Follow();
        User32.GetWindowThreadProcessId(_source, out uint process);
        nint callback = Marshal.GetFunctionPointerForDelegate(_eventProc);
        _hooks.Add(User32.SetWinEventHook(LocationChange, LocationChange, 0, callback, process, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(MinimizeStart, MinimizeEnd, 0, callback, process, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(Foreground, Foreground, 0, callback, 0, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(Reorder, Reorder, 0, callback, 0, 0, OutOfContext));
        _renderer = new Thread(Render) { IsBackground = true, Name = "WinScreen effect" };
        _renderer.Start();
    }

    private void OnWindowEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (kind == Reorder || kind == Foreground || window == _source)
            Follow();
    }

    /// <summary>Stays exactly over the window and just above it, and out of sight while it's minimized.</summary>
    private void Follow()
    {
        if (_stopping || IsDisposed)
            return;
        if (!User32.IsWindow(_source) || User32.IsIconic(_source) || !User32.IsWindowVisible(_source))
        {
            Hide();
            return;
        }

        if (WindowInfo.TryGetFrameBounds(_source, out Rectangle frame, out _) && frame.Location != Location)
            Location = frame.Location;
        if (!Visible)
            Show();
        bool topmost = (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        User32.SetWindowPos(Handle, topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        User32.SetWindowPos(_source, Handle, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        if (topmost) WinScreen.Windows.TopLayer.KeepAbove();
    }

    private void CreateDevice(Size size)
    {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0],
            out _device, out _, out _context).CheckError();
        // Without a graphics card, Windows offers its software renderer as hardware. The effect starts on it but
        // shows nothing, so refuse instead.
        using (IDXGIDevice graphics = _device!.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = graphics.GetAdapter())
            if (adapter.Description.VendorId == SoftwareRendererVendor)
                throw new NotSupportedException();
        _stats = _device!.CreateComputeShader(Compiler.Compile(ContentShaders.Stats, "Stats", "stats", "cs_5_0").Span);
        _composite = _device.CreateComputeShader(Compiler.Compile(ContentShaders.Composite, "Composite", "composite", "cs_5_0").Span);
        _options = _device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer, ResourceUsage.Default));

        using var factory = CreateDXGIFactory1<IDXGIFactory2>();
        _chain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = (uint)size.Width, Height = (uint)size.Height, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Premultiplied,
        });

        // DirectComposition through its COM interface: no package needed.
        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        Guid iid = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        Marshal.ThrowExceptionForHR(DCompositionCreateDevice(dxgi.NativePointer, in iid, out _composition));
        nint target = 0, visual = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(_composition, 6))(_composition, Handle, 1, &target));
        _target = target;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_composition, 7))(_composition, &visual));
        _visual = visual;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_visual, 15))(_visual, _chain.NativePointer));
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_target, 3))(_target, _visual));
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_composition, 3))(_composition));

        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException();
        _captureDevice = WgcInterop.Device(_device);
        GraphicsCaptureItem item = WgcInterop.Item(_source);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += (_, _) => _arrived.Set();
        _session = _pool.CreateCaptureSession(item);
        if (global::Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = false;
        Pip.CaptureBorder.TryHide(_session, wait: 0); // The effect sits on the window, so a border would frame it.
        _session.StartCapture();
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);

    /// <summary>The frame loop. Nothing runs between frames: it waits for the window to draw.</summary>
    private void Render()
    {
        var size = Size.Empty;
        ID3D11Texture2D? cache = null, tiles = null, mask = null, output = null;
        ID3D11ShaderResourceView? cacheView = null, maskView = null;
        ID3D11UnorderedAccessView? tilesView = null, outputView = null;
        var staging = new ID3D11Texture2D?[2];
        long frame = 0;
        try
        {
            while (WaitHandle.WaitAny([_arrived, _stop]) == 0)
            {
                Direct3D11CaptureFrame? next = null;
                while (_pool!.TryGetNextFrame() is { } newer)
                {
                    next?.Dispose();
                    next = newer;
                }

                if (next is null)
                    continue;
                using (next)
                {
                    var content = new Size(next.ContentSize.Width, next.ContentSize.Height);
                    if (content.Width < 16 || content.Height < 16)
                        continue;
                    if (content != size)
                    {
                        size = content;
                        DisposeAll(cache, tiles, mask, output, cacheView, maskView, tilesView, outputView, staging[0], staging[1]);
                        int columns = size.Width / PictureFinder.Tile, rows = size.Height / PictureFinder.Tile;
                        cache = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.ShaderResource));
                        cacheView = _device.CreateShaderResourceView(cache);
                        tiles = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)columns, (uint)rows, 1, 1, BindFlags.UnorderedAccess));
                        tilesView = _device.CreateUnorderedAccessView(tiles);
                        for (int i = 0; i < 2; i++)
                            staging[i] = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)columns, (uint)rows, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                        mask = _device.CreateTexture2D(new Texture2DDescription(Format.R32_Float, (uint)columns, (uint)rows, 1, 1, BindFlags.ShaderResource));
                        maskView = _device.CreateShaderResourceView(mask);
                        output = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.UnorderedAccess));
                        outputView = _device.CreateUnorderedAccessView(output);
                        _chain!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
                        _pool.Recreate(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, next.ContentSize);
                        _finder.Reset();
                        frame = 0;
                        Size newSize = size;
                        BeginInvoke(() => { if (!IsDisposed) ClientSize = newSize; });
                    }

                    using (var texture = WgcInterop.Texture(next.Surface))
                        _context!.CopySubresourceRegion(cache, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, size.Width, size.Height, 1));
                    Process(size, cacheView!, tilesView!, tiles!, staging, mask!, maskView!, outputView!, output!, frame++);
                    Frames++;
                }
            }
        }
        catch (Exception failure) when (failure is SharpGen.Runtime.SharpGenException or COMException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_stopping)
                BeginInvoke(() => Failed?.Invoke(Localization.UiText.T("errors.glass.stopped")));
        }
        finally
        {
            DisposeAll(cache, tiles, mask, output, cacheView, maskView, tilesView, outputView, staging[0], staging[1]);
        }
    }

    private void Process(Size size, ID3D11ShaderResourceView cacheView, ID3D11UnorderedAccessView tilesView, ID3D11Texture2D tiles,
        ID3D11Texture2D?[] staging, ID3D11Texture2D mask, ID3D11ShaderResourceView maskView, ID3D11UnorderedAccessView outputView,
        ID3D11Texture2D output, long frame)
    {
        var context = _context!;
        int columns = size.Width / PictureFinder.Tile, rows = size.Height / PictureFinder.Tile;
        context.CSSetShader(_stats);
        context.CSSetShaderResource(0, cacheView);
        context.CSSetUnorderedAccessView(0, tilesView);
        context.Dispatch((uint)(columns + 7) / 8, (uint)(rows + 7) / 8, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        // This frame's own statistics. A still window sends a single frame, so the mask can't lag behind it.
        // The statistics are small and take well under a millisecond, so waiting for them is cheap.
        context.CopyResource(staging[0], tiles);
        float[] found = Classify(staging[0]!, columns, rows);
        fixed (float* data = found)
            context.UpdateSubresource(mask, 0, null, (nint)data, (uint)(columns * sizeof(float)), 0);

        (float pr, float pg, float pb) = _finder.Page;
        ContentLevels levels = _levels;
        var options = new Options
        {
            Background = levels.Background, Text = levels.Text, Picture = levels.Picture,
            Mode = _look == ContentLook.Dark ? 2 : 0, PageR = pr, PageG = pg, PageB = pb,
        };
        context.UpdateSubresource(in options, _options!);
        context.CSSetShader(_composite);
        context.CSSetConstantBuffer(0, _options);
        context.CSSetShaderResource(0, cacheView);
        context.CSSetShaderResource(1, maskView);
        context.CSSetUnorderedAccessView(0, outputView);
        context.Dispatch((uint)(size.Width + 15) / 16, (uint)(size.Height + 15) / 16, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        context.CSSetShaderResource(1, null);
        using (var back = _chain!.GetBuffer<ID3D11Texture2D>(0))
            context.CopyResource(back, output);
        _chain.Present(0, PresentFlags.None);
    }

    private float[] Classify(ID3D11Texture2D staging, int columns, int rows)
    {
        MappedSubresource map = _context!.Map(staging, 0, MapMode.Read);
        var stats = new TileStats[columns * rows];
        try
        {
            for (int y = 0; y < rows; y++)
            {
                var row = (Half*)((byte*)map.DataPointer + y * map.RowPitch);
                for (int x = 0; x < columns; x++)
                {
                    float a = (float)row[x * 4 + 3];
                    bool busy = a >= 1.5f;
                    stats[y * columns + x] = new TileStats((float)row[x * 4], (float)row[x * 4 + 1], (float)row[x * 4 + 2], busy ? a - 2 : a, busy);
                }
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        return _finder.Find(stats, columns, rows);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Options
    {
        public float Background, Text, Picture, Unused1;
        public float Mode, Unused2, Unused3, Unused4;
        public float PageR, PageG, PageB, Unused5;
    }

    private static void DisposeAll(params IDisposable?[] items)
    {
        foreach (IDisposable? item in items)
            item?.Dispose();
    }

    /// <summary>Stops processing and takes the overlay away. The caller reveals the window.</summary>
    public void Stop()
    {
        if (_stopping)
            return;
        _stopping = true;
        foreach (nint hook in _hooks)
            if (hook != 0)
                User32.UnhookWinEvent(hook);
        _hooks.Clear();
        _stop.Set();
        _renderer?.Join(2000);
        Release();
        Close();
    }

    private void Release()
    {
        try { _session?.Dispose(); } catch (COMException) { }
        try { _pool?.Dispose(); } catch (COMException) { }
        _session = null;
        _pool = null;
        if (_visual != 0) Marshal.Release(_visual);
        if (_target != 0) Marshal.Release(_target);
        if (_composition != 0) Marshal.Release(_composition);
        _visual = _target = _composition = 0;
        _chain?.Dispose();
        _options?.Dispose();
        _stats?.Dispose();
        _composite?.Dispose();
        _context?.ClearState();
        _context?.Dispose();
        _device?.Dispose();
        _chain = null;
        _context = null;
        _device = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _arrived.Dispose();
            _stop.Dispose();
            GC.KeepAlive(_eventProc);
        }

        base.Dispose(disposing);
    }
}
