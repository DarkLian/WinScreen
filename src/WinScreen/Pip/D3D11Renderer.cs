using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using SharpGen.Runtime;
using Vortice;
using WinScreen.Diagnostics;
using WinScreen.Settings;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace WinScreen.Pip;

// One worker owns the immediate context, the cached full-size texture and the bounded swapchain resources.
internal sealed class D3D11Renderer : IDisposable
{
    internal ID3D11Device Device { get; private set; } = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _cache, _scaled;
    // With covers, the frame as it came is kept here and the cache is it with the covers blacked out: everything drawn
    // or copied from the cache has them, and they can change without waiting for the window to change.
    private ID3D11Texture2D? _whole, _staging;
    private ID3D11RenderTargetView? _cacheTarget;
    private ID3D11DeviceContext1? _clearing;
    private Rectangle[] _covers = [];
    private ID3D11ShaderResourceView? _sourceView, _scaledView;
    private ID3D11UnorderedAccessView? _scaledOutput;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _pixel;
    private ID3D11ComputeShader? _compute, _computeCubic;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _constants;
    private ID3D11Query? _fence;
    private bool _gpuPending;
    private long _workStarted;
    private Size _bufferSize;
    internal Size SourceSize { get; private set; }
    internal PipScaling ViewScaling { get; set; }
    internal ViewTurn Turn { get; set; }
    private readonly PipPreferences _preferences;
    private readonly PipDiagnostics _diagnostics;
    private bool _disposed;
    private long _lastResize;
    // Windows' software renderer: Auto enlarges with the linear blend, which costs the processor less.
    private readonly bool _software;
    internal PipCapabilities Capabilities { get; private set; } = null!;
    internal bool HasFrame => _cache is not null;
    internal int RemovedReason => Device is null ? 0 : Device.DeviceRemovedReason.Code;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Crop, Viewport, Surface, Selection, Options, Orient;
    }

    internal D3D11Renderer(nint hwnd, nint monitor, PipPreferences preferences, PipDiagnostics diagnostics, bool software,
        bool preferCompute = true)
    {
        _preferences = preferences;
        ViewScaling = preferences.Scaling;
        _software = software;
        _diagnostics = diagnostics;
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory2>();
            using var adapter = software ? null : FindAdapter(factory, monitor);
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
            var driver = software ? DriverType.Warp : adapter is null ? DriverType.Hardware : DriverType.Unknown;
            var result = D3D11CreateDevice(adapter, driver, DeviceCreationFlags.BgraSupport, levels,
                out var device, out FeatureLevel level, out var context);
            if (result.Code == unchecked((int)0x80070057))
                result = D3D11CreateDevice(adapter, driver, DeviceCreationFlags.BgraSupport, levels[1..],
                    out device, out level, out context);
            result.CheckError();
            Device = device;
            _context = context;
            var format = Device.CheckFormatSupport(Format.B8G8R8A8_UNorm);
            const FormatSupport captureFormat = FormatSupport.Texture2D | FormatSupport.ShaderLoad |
                FormatSupport.ShaderSample | FormatSupport.RenderTarget;
            bool supported = (format & captureFormat) == captureFormat;
            if (!supported) throw new NotSupportedException("BGRA texture/render support unavailable.");
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            using var actual = dxgi.GetAdapter();
            var description = actual.Description;
            const FormatSupport computeFormat = FormatSupport.Texture2D | FormatSupport.ShaderLoad | FormatSupport.TypedUnorderedAccessView;
            bool compute = level >= FeatureLevel.Level_11_0 &&
                (Device.CheckFormatSupport(Format.R8G8B8A8_UNorm) & computeFormat) == computeFormat;
            Capabilities = new(true, !software, level.ToString(), compute, supported, null,
                global::Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"),
                description.Description, description.Luid);
            _diagnostics.Capabilities = Capabilities;

            // Windows 11 ships the system compiler. Resolve from System32 only; a missing compiler falls back to DWM.
            EnsureCompiler();
            _vertex = Device.CreateVertexShader(Compiler.Compile(PipShaders.Source, "VS", "PipShaders", "vs_4_0").Span);
            _pixel = Device.CreatePixelShader(Compiler.Compile(PipShaders.Source, "PS", "PipShaders", "ps_4_0").Span);
            if (compute && preferCompute)
            {
                try
                {
                    _compute = Device.CreateComputeShader(Compiler.Compile("#define COMPUTE\n" + PipShaders.Source,
                        "CS", "PipShaders", "cs_5_0").Span);
                }
                catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or ExternalException)
                {
                    Capabilities = Capabilities with { ComputeAreaSupported = false };
                    _diagnostics.Capabilities = Capabilities;
                }
                // Without it, shrinking a little averages as it does further down.
                if (_compute is not null && !software)
                    try
                    {
                        _computeCubic = Device.CreateComputeShader(Compiler.Compile("#define COMPUTE\n" + PipShaders.Source,
                            "CSCubic", "PipShaders", "cs_5_0").Span);
                    }
                    catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or ExternalException) { Log.Swallowed(e); }
            }
            _sampler = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _constants = Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
            _fence = Device.CreateQuery(new QueryDescription(QueryType.Event));
            var swap = new SwapChainDescription1
            {
                Width = 16, Height = 16, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2, SwapEffect = SwapEffect.FlipSequential, Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForHwnd(Device, hwnd, swap);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter).CheckError();
        }
        catch { Dispose(); throw; }
    }

    private static readonly object CompilerGate = new();
    private static bool _compilerResolved;
    private static void EnsureCompiler()
    {
        lock (CompilerGate)
        {
            if (_compilerResolved) return;
            NativeLibrary.SetDllImportResolver(typeof(Compiler).Assembly, (name, assembly, _) =>
                name.Contains("d3dcompiler", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load("d3dcompiler_47.dll", assembly, DllImportSearchPath.System32) : 0);
            _compilerResolved = true;
        }
    }

    private static IDXGIAdapter1? FindAdapter(IDXGIFactory2 factory, nint monitor)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            bool found = false;
            try
            {
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    using (output) if (output.Description.Monitor == monitor) { found = true; break; }
                }
                if (found) return adapter;
            }
            finally { if (!found) adapter.Dispose(); }
        }
        return null;
    }

    internal void Cache(ID3D11Texture2D texture, Size content)
    {
        var desc = texture.Description;
        int max = Device.FeatureLevel >= FeatureLevel.Level_11_0 ? 16384 : 8192;
        if (content.Width <= 0 || content.Height <= 0 || content.Width > max || content.Height > max ||
            (long)content.Width * content.Height > 67_108_864 || content.Width > desc.Width || content.Height > desc.Height)
            throw new InvalidOperationException("Capture dimensions exceed supported bounds.");
        if (SourceSize != content)
        {
            _sourceView?.Dispose(); _sourceView = null;
            _cacheTarget?.Dispose(); _cacheTarget = null;
            _whole?.Dispose(); _whole = null;
            _staging?.Dispose(); _staging = null;
            _cache?.Dispose(); _cache = null;
            _cache = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)content.Width, (uint)content.Height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
            _sourceView = Device.CreateShaderResourceView(_cache);
            SourceSize = content;
        }
        var box = new Box(0, 0, 0, content.Width, content.Height, 1);
        if (_covers.Length == 0)
        {
            _context.CopySubresourceRegion(_cache!, 0, 0, 0, 0, texture, 0, box);
        }
        else
        {
            _whole ??= Device.CreateTexture2D(_cache!.Description with { BindFlags = BindFlags.ShaderResource });
            _context.CopySubresourceRegion(_whole, 0, 0, 0, 0, texture, 0, box);
            Cover();
        }
        _diagnostics.Source = content;
    }

    // In the cache's pixels. True when it queued work on the graphics card.
    internal bool SetCovers(Rectangle[] covers)
    {
        if (covers.AsSpan().SequenceEqual(_covers)) return false;
        _covers = covers;
        if (_cache is null) return false;
        if (covers.Length > 0 && _whole is null)
        {
            // Until now the cache was the whole frame.
            _whole = Device.CreateTexture2D(_cache.Description with { BindFlags = BindFlags.ShaderResource });
            _context.CopyResource(_whole, _cache);
        }
        if (_whole is null) return false;
        if (covers.Length > 0)
        {
            Cover();
            return true;
        }
        _context.CopyResource(_cache, _whole);
        _whole.Dispose();
        _whole = null;
        return true;
    }

    private void Cover()
    {
        _context.CopyResource(_cache!, _whole!);
        var whole = new Rectangle(Point.Empty, SourceSize);
        RawRect[] rects = _covers.Select(cover => Rectangle.Intersect(cover, whole)).Where(cover => cover.Width > 0 && cover.Height > 0)
            .Select(cover => new RawRect(cover.Left, cover.Top, cover.Right, cover.Bottom)).ToArray();
        if (rects.Length == 0) return;
        _cacheTarget ??= Device.CreateRenderTargetView(_cache);
        _clearing ??= _context.QueryInterface<ID3D11DeviceContext1>();
        _clearing.ClearView(_cacheTarget, new Color4(0, 0, 0, 1), rects);
    }

    // The picture as the view has it, covers included, read once the graphics card has copied it: the worker asks
    // again on its next turns and never waits.
    internal void BeginReadBack()
    {
        if (_cache is null) return;
        _staging ??= Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)SourceSize.Width,
            (uint)SourceSize.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _context.CopyResource(_staging, _cache);
    }

    internal unsafe Bitmap? TryReadBack()
    {
        if (_staging is null) return null;
        Result mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource map);
        if (mapped.Code == unchecked((int)0x887A000A)) return null; // Still copying.
        mapped.CheckError();
        try
        {
            var picture = new Bitmap(SourceSize.Width, SourceSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bits = picture.LockBits(new Rectangle(Point.Empty, picture.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < picture.Height; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, (byte*)bits.Scan0 + (long)y * bits.Stride,
                        bits.Stride, picture.Width * 4L);
            }
            finally { picture.UnlockBits(bits); }
            return picture;
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    internal bool ReadyForWork()
    {
        if (!_gpuPending) return true;
        var result = _context.GetData(_fence!, 0, 0, AsyncGetDataFlags.DoNotFlush);
        result.CheckError();
        if (result.Code == 0) { _gpuPending = false; return true; }
        if (Stopwatch.GetElapsedTime(_workStarted).TotalSeconds > 5)
            throw new TimeoutException("GPU work did not complete within five seconds.");
        return false;
    }

    internal void EndWork()
    {
        _context.End(_fence!);
        _context.Flush(); // Submit cached-copy-only work too; never wait for the GPU.
        _gpuPending = true;
        _workStarted = Stopwatch.GetTimestamp();
    }

    internal bool Render(Size client, Rectangle destination, Rectangle requestedCrop, byte opacity,
        Rectangle? selection, long timestamp, bool newFrame, bool present = true, long arrivalTimestamp = 0)
    {
        if (_cache is null || client.Width <= 0 || client.Height <= 0) return false;
        int max = Device.FeatureLevel >= FeatureLevel.Level_11_0 ? 16384 : 8192;
        Size target = PipRenderGeometry.Target(client, SourceSize, max, _preferences.PixelBudget);
        if (target != client) throw new NotSupportedException("Destination exceeds the configured physical-pixel budget.");
        if (_bufferSize != client)
        {
            if (_renderTarget is not null && Stopwatch.GetElapsedTime(_lastResize).TotalMilliseconds < 100) return false;
            _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
            _renderTarget?.Dispose(); _renderTarget = null;
            _scaledOutput?.Dispose(); _scaledOutput = null;
            _scaledView?.Dispose(); _scaledView = null;
            _scaled?.Dispose(); _scaled = null;
            _swapChain!.ResizeBuffers(2, (uint)client.Width, (uint)client.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderTarget = Device.CreateRenderTargetView(back);
            _bufferSize = client;
            _lastResize = Stopwatch.GetTimestamp();
            _diagnostics.Resizes++;
        }
        Rectangle crop = PipRenderGeometry.Crop(requestedCrop, SourceSize);
        // Scaling is worked out on the upright picture; the shader turns it as it draws.
        Size upright = Turn.Oriented(destination.Size);
        var constants = new Constants
        {
            Crop = new(crop.X, crop.Y, crop.Width, crop.Height),
            Viewport = new(destination.X, destination.Y, destination.Width, destination.Height),
            Surface = new(SourceSize.Width, SourceSize.Height, client.Width, client.Height),
            Selection = selection is { } s ? new(s.X, s.Y, s.Width, s.Height) : Vector4.Zero,
            Options = new(opacity / 255f, ViewScaling == PipScaling.Auto && _software ? 3 : (int)ViewScaling, 0, selection.HasValue ? 1 : 0),
            Orient = new(Turn.Turns, Turn.Mirrored ? 1 : 0, upright.Width, upright.Height),
        };
        bool compute = _compute is not null && ViewScaling == PipScaling.Auto &&
            crop.Width >= upright.Width && crop.Height >= upright.Height;
        bool enlarging = crop.Width < upright.Width || crop.Height < upright.Height;
        bool cubic = compute && _computeCubic is not null && crop.Size != upright &&
            crop.Width * 2 < upright.Width * 3 && crop.Height * 2 < upright.Height * 3;
        _diagnostics.FilterMode = crop.Size == upright ? "Exact texel" :
            enlarging ? (ViewScaling == PipScaling.Crisp ? "Nearest texel" :
                ViewScaling == PipScaling.Auto && !_software ? "Edge-aware enlarge" : "Linear") :
            ViewScaling == PipScaling.Smooth && crop.Width < 2 * upright.Width && crop.Height < 2 * upright.Height ? "Linear" :
            cubic ? "Cubic shrink (compute 16x16)" : compute ? "Area (compute 16x16)" : "Area (raster PS4)";
        _context.UpdateSubresource(in constants, _constants!);
        if (compute)
        {
            if (_scaled is null || _scaled.Description.Width != upright.Width || _scaled.Description.Height != upright.Height)
            {
                _scaledOutput?.Dispose(); _scaledView?.Dispose(); _scaled?.Dispose();
                _scaled = Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                    (uint)upright.Width, (uint)upright.Height, 1, 1, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
                _scaledOutput = Device.CreateUnorderedAccessView(_scaled);
                _scaledView = Device.CreateShaderResourceView(_scaled);
            }
            _context.CSSetShader(cubic ? _computeCubic : _compute);
            _context.CSSetConstantBuffer(0, _constants);
            _context.CSSetShaderResource(0, _sourceView);
            _context.CSSetSampler(0, _sampler);
            _context.CSSetUnorderedAccessView(0, _scaledOutput);
            _context.Dispatch((uint)(upright.Width + 15) / 16, (uint)(upright.Height + 15) / 16, 1);
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetShaderResource(0, null);
            constants.Options.Z = 1;
            _context.UpdateSubresource(in constants, _constants!);
        }
        _context.OMSetRenderTargets(_renderTarget!);
        _context.RSSetViewport(new Viewport(0, 0, client.Width, client.Height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetShader(_pixel);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetShaderResource(0, compute ? _scaledView! : _sourceView!);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);
        // A diagnostic oracle can inspect the completed GPU target before flip-model Present retires it.
        if (!present) return true;
        long before = Stopwatch.GetTimestamp();
        var result = _swapChain!.Present(0, PresentFlags.DoNotWait);
        if (result.Code == unchecked((int)0x887A000A)) return false; // Busy: keep latest, never block capture/UI.
        result.CheckError();
        if (result.Code != 0) return false; // DXGI_STATUS_OCCLUDED is not a successful display probe.
        _diagnostics.Target = destination.Size;
        if (newFrame) _diagnostics.PresentedFrame(timestamp, arrivalTimestamp, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The window keeps showing the last frame after the capture ends, under Windows' thumbnail when that takes
        // over. It may be from another layout (turned, zoomed), so where the thumbnail leaves a cover's gap an old,
        // uncovered part could show: black first.
        try
        {
            if (_renderTarget is not null && _swapChain is not null)
            {
                _context.ClearRenderTargetView(_renderTarget, new Color4(0, 0, 0, 1));
                _swapChain.Present(0, PresentFlags.None);
            }
        }
        catch (Exception e) when (e is SharpGenException or ExternalException) { Log.Swallowed(e); }
        _context?.ClearState();
        _sourceView?.Dispose(); _cacheTarget?.Dispose(); _cache?.Dispose(); _whole?.Dispose(); _staging?.Dispose(); _clearing?.Dispose();
        _scaledOutput?.Dispose(); _scaledView?.Dispose(); _scaled?.Dispose();
        _renderTarget?.Dispose(); _swapChain?.Dispose();
        _constants?.Dispose(); _sampler?.Dispose(); _compute?.Dispose(); _computeCubic?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose();
        _fence?.Dispose();
        // DXGI permits only one flip-model chain per HWND. Flush deferred destruction before a recovery creates
        // another.
        _context?.Flush();
        _context?.Dispose(); Device?.Dispose();
    }
}
