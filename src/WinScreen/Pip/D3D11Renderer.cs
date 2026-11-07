using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using WinScreen.Settings;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace WinScreen.Pip;

/// <summary>Single worker owns the immediate context, cached full-size texture and bounded swapchain resources.</summary>
internal sealed class D3D11Renderer : IDisposable
{
    internal ID3D11Device Device { get; private set; } = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _cache, _scaled;
    private ID3D11ShaderResourceView? _sourceView, _scaledView;
    private ID3D11UnorderedAccessView? _scaledOutput;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _pixel;
    private ID3D11ComputeShader? _compute;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _constants;
    private ID3D11Query? _fence;
    private bool _gpuPending;
    private long _workStarted;
    private Size _bufferSize;
    internal Size SourceSize { get; private set; }
    private readonly PipPreferences _preferences;
    private readonly PipDiagnostics _diagnostics;
    private bool _disposed;
    private long _lastResize;
    internal PipCapabilities Capabilities { get; private set; } = null!;
    internal bool HasFrame => _cache is not null;
    internal int RemovedReason => Device is null ? 0 : Device.DeviceRemovedReason.Code;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Crop, Viewport, Surface, Selection, Options;
    }

    internal D3D11Renderer(nint hwnd, nint monitor, PipPreferences preferences, PipDiagnostics diagnostics, bool software,
        bool preferCompute = true)
    {
        _preferences = preferences;
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
            }
            _sampler = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _constants = Device.CreateBuffer(new BufferDescription(80, BindFlags.ConstantBuffer, ResourceUsage.Default));
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
            _cache?.Dispose(); _cache = null;
            _cache = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)content.Width, (uint)content.Height, 1, 1, BindFlags.ShaderResource));
            _sourceView = Device.CreateShaderResourceView(_cache);
            SourceSize = content;
        }
        _context.CopySubresourceRegion(_cache!, 0, 0, 0, 0, texture, 0,
            new Box(0, 0, 0, content.Width, content.Height, 1));
        _diagnostics.Source = content;
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
        var constants = new Constants
        {
            Crop = new(crop.X, crop.Y, crop.Width, crop.Height),
            Viewport = new(destination.X, destination.Y, destination.Width, destination.Height),
            Surface = new(SourceSize.Width, SourceSize.Height, client.Width, client.Height),
            Selection = selection is { } s ? new(s.X, s.Y, s.Width, s.Height) : Vector4.Zero,
            Options = new(opacity / 255f, (int)_preferences.Scaling, 0, selection.HasValue ? 1 : 0),
        };
        bool compute = _compute is not null && _preferences.Scaling == PipScaling.Auto &&
            crop.Width >= destination.Width && crop.Height >= destination.Height;
        _diagnostics.FilterMode = _preferences.Scaling == PipScaling.Crisp ? "Nearest texel" :
            _preferences.Scaling == PipScaling.Smooth || crop.Width < destination.Width || crop.Height < destination.Height ? "Linear" :
            crop.Size == destination.Size ? "Exact texel" : compute ? "Compute area (16x16)" : "Raster area (PS4)";
        _context.UpdateSubresource(in constants, _constants!);
        if (compute)
        {
            if (_scaled is null || _scaled.Description.Width != destination.Width || _scaled.Description.Height != destination.Height)
            {
                _scaledOutput?.Dispose(); _scaledView?.Dispose(); _scaled?.Dispose();
                _scaled = Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                    (uint)destination.Width, (uint)destination.Height, 1, 1, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
                _scaledOutput = Device.CreateUnorderedAccessView(_scaled);
                _scaledView = Device.CreateShaderResourceView(_scaled);
            }
            _context.CSSetShader(_compute);
            _context.CSSetConstantBuffer(0, _constants);
            _context.CSSetShaderResource(0, _sourceView);
            _context.CSSetSampler(0, _sampler);
            _context.CSSetUnorderedAccessView(0, _scaledOutput);
            _context.Dispatch((uint)(destination.Width + 15) / 16, (uint)(destination.Height + 15) / 16, 1);
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
        _context?.ClearState();
        _sourceView?.Dispose(); _cache?.Dispose();
        _scaledOutput?.Dispose(); _scaledView?.Dispose(); _scaled?.Dispose();
        _renderTarget?.Dispose(); _swapChain?.Dispose();
        _constants?.Dispose(); _sampler?.Dispose(); _compute?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose();
        _fence?.Dispose();
        // DXGI permits only one flip-model chain per HWND. Flush deferred destruction before a recovery creates another.
        _context?.Flush();
        _context?.Dispose(); Device?.Dispose();
    }
}
