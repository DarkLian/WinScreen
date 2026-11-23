using System.Runtime.CompilerServices;
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

internal readonly record struct ContentLevels(float Background, float Text, float FadeRange = 1, bool Plain = false,
    GlassText TextColor = GlassText.AsItIs, bool Shadow = true);

// Light or Dark flips a page that is the other way, as Dark mode does, before Glass.
public enum GlassText { AsItIs, Light, Dark }

internal sealed unsafe class ContentOverlay : Form
{
    private const long NoRedirectionBitmap = 0x200000;
    internal const uint SoftwareRendererVendor = 0x1414; // Microsoft: the Basic Render Driver.
    private const uint OutOfContext = 0, LocationChange = 0x800B, Foreground = 0x0003, Reorder = 0x8004;
    private const uint MinimizeStart = 0x0016, MinimizeEnd = 0x0017, Cloaked = 0x8017, Uncloaked = 0x8018;

    private readonly nint _source;
    private readonly ContentLook _look;
    private readonly PageReader _page = new();
    private readonly AutoResetEvent _arrived = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly User32.WinEventProc _eventProc;
    private readonly List<nint> _hooks = [];
    private const uint NextBelow = 2; // GW_HWNDNEXT
    private const uint PreviousAbove = 3; // GW_HWNDPREV
    private const int RetryMilliseconds = 500;
    private volatile bool _stopping;
    private long _retryAfter;
    private System.Windows.Forms.Timer? _retry;
    private volatile StrongBox<ContentLevels> _levels;
    private volatile bool _changed;
    private volatile Settings.KeptArea[] _kept;
    private readonly bool _leaveAlreadyDark;
    private volatile BackdropSet? _backdrop;
    private volatile BackdropChoice? _showing;
    private volatile bool _hidden;
    private volatile bool _audible;
    private volatile bool _held;
    private Thread? _renderer;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _stats, _composite, _backdropShader;
    private ID3D11Buffer? _options, _place;
    private ID3D11SamplerState? _edge, _repeat;
    private IDXGISwapChain1? _chain;
    private nint _composition, _target, _visual;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _captureDevice;

    public ContentOverlay(nint source, ContentLook look, ContentLevels levels, bool leaveAlreadyDark = true, BackdropSet? backdrop = null,
        Settings.KeptArea[]? kept = null)
    {
        _source = source;
        _look = look;
        _levels = new(levels);
        _leaveAlreadyDark = leaveAlreadyDark;
        _backdrop = backdrop;
        _kept = kept ?? [];
        _eventProc = OnWindowEvent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "WinScreen effect";
    }

    public event Action<string>? Failed;

    // With the file or link that couldn't be opened or stopped playing, and whether it is a file that isn't there.
    public event Action<string, bool>? BackdropFailed;

    public event Action? CameForward;

    // Whether the app this window belongs to is the one in front, each time another window comes to the front;
    // WinScreen's own windows (its menus, Settings) change nothing.
    public event Action<bool>? FrontChanged;

    public BackdropSet? Background => _backdrop;

    // The one of the set showing now.
    public BackdropChoice? Showing => _showing;

    public nint Source => _source;

    public ContentLook Look => _look;

    internal long Frames { get; private set; }

    internal int Restacks { get; private set; }


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

    // A still window sends no new frame, so a change redraws the last one.
    public void SetLevels(ContentLevels levels)
    {
        _levels = new(levels);
        _changed = true;
        if (!_stopping) _arrived.Set();
    }

    public void SetKept(Settings.KeptArea[] kept)
    {
        _kept = kept;
        _changed = true;
        if (!_stopping) _arrived.Set();
    }

    public void SetBackdrop(BackdropSet? backdrop)
    {
        _backdrop = backdrop;
        if (!_stopping) _arrived.Set();
    }

    public void SetAudible(bool audible)
    {
        if (_audible == audible) return;
        _audible = audible;
        if (!_stopping) _arrived.Set();
    }

    // Held: videos and moving pictures stop where they are, as for a full-screen game.
    public void SetHeld(bool held)
    {
        if (_held == held) return;
        _held = held;
        if (!_stopping) _arrived.Set();
    }

    public void SetOpacity(byte alpha)
    {
        if (IsHandleCreated) User32.SetLayeredWindowAttributes(Handle, 0, alpha, User32.LWA_ALPHA);
    }

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
            Diagnostics.Log.Error("effect.deviceFailed", failure);
            Release();
            throw new WindowOperationException(Localization.UiText.T("errors.glass.unavailable"));
        }

        Show();
        Follow();
        User32.GetWindowThreadProcessId(_source, out uint process);
        nint callback = Marshal.GetFunctionPointerForDelegate(_eventProc);
        _hooks.Add(User32.SetWinEventHook(LocationChange, LocationChange, 0, callback, process, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(MinimizeStart, MinimizeEnd, 0, callback, process, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(Cloaked, Uncloaked, 0, callback, process, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(Foreground, Foreground, 0, callback, 0, 0, OutOfContext));
        _hooks.Add(User32.SetWinEventHook(Reorder, Reorder, 0, callback, 0, 0, OutOfContext));
        _renderer = new Thread(Render) { IsBackground = true, Name = "WinScreen effect" };
        _renderer.Start();
    }

    private void OnWindowEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (kind == Reorder || kind == Foreground || window == _source)
            Follow();
        if (kind == Foreground && window == _source && !_stopping)
            CameForward?.Invoke();
        if (kind == Foreground && !_stopping && AppInFront(_source, window) is { } front)
            FrontChanged?.Invoke(front);
    }

    // Null when that can't be said: nothing in front, or a window of WinScreen's own.
    internal static bool? AppInFront(nint source) => AppInFront(source, User32.GetForegroundWindow());

    private static bool? AppInFront(nint source, nint front)
    {
        if (front == 0 || User32.GetWindowThreadProcessId(front, out uint shown) == 0 || shown == Environment.ProcessId ||
            User32.GetWindowThreadProcessId(source, out uint mine) == 0)
            return null;
        return shown == mine;
    }

    private void Follow()
    {
        if (_stopping || IsDisposed)
            return;
        if (!User32.IsWindow(_source) || User32.IsIconic(_source) || !User32.IsWindowVisible(_source))
        {
            Hide();
            Hidden(true);
            return;
        }
        // On another virtual desktop the window is still visible to Windows, only cloaked; its video and sound stop.
        Hidden(Dwmapi.DwmGetWindowAttribute(_source, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0);

        if (WindowInfo.TryGetFrameBounds(_source, out Rectangle frame, out _) && frame.Location != Location)
            Location = frame.Location;
        if (!Visible)
            Show();
        bool topmost = (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        bool raised = (User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        // Already just above the window, in the same band: leave the order alone. Every change of order is reported
        // to the whole desktop, this method's own included, so changing it anyway made it run again without end.
        // Hidden windows in between don't count: Windows keeps an input method's window right under ours.
        if (topmost == raised && NextShown(Handle) == _source)
            return;
        // Each try is reported back as a change of order, so a try that can't succeed came straight back, hundreds of
        // times a second, and Windows dropped the gesture hook for the stalled thread.
        if (Environment.TickCount64 < _retryAfter)
        {
            if (_retry is null)
            {
                _retry = new System.Windows.Forms.Timer { Interval = RetryMilliseconds };
                _retry.Tick += RetryFollow;
            }
            _retry.Start();
            return;
        }
        Restacks++;
        // The overlay goes to the window, never the window to the overlay: moving the window under the overlay sent it
        // down the stack, below other windows.
        if (topmost != raised)
            User32.SetWindowPos(Handle, topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        nint above = PreviousShown(_source);
        if (above == Handle) return;
        // Nothing above in its own band (the window is the band's first): the top of that band. Placing it after a
        // window of the other band would move it into that band.
        bool sameBand = above != 0 && ((User32.GetWindowLongPtr(above, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0) == topmost;
        User32.SetWindowPos(Handle, sameBand ? above : topmost ? User32.HWND_TOPMOST : User32.HWND_TOP, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        if (sameBand || NextShown(Handle) == _source) return;
        // Windows doesn't let a program that isn't in front put a window at the top, as after a toolbar button, which
        // never takes the focus: the overlay stayed where it was made, under other windows. Going lower is always
        // allowed, so the overlay goes just under the window, and the window just under the overlay: the window moves
        // one place, past its own overlay, and nothing else changes.
        User32.SetWindowPos(Handle, _source, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        User32.SetWindowPos(_source, Handle, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        nint between = NextShown(Handle);
        if (between == _source)
        {
            Diagnostics.Log.Event("effect.raisedPastWindow", ("ok", true));
            return;
        }

        _retryAfter = Environment.TickCount64 + RetryMilliseconds;
        Diagnostics.Log.Event("effect.raisedPastWindow", ("ok", false), ("between", Diagnostics.LogValue.ClassOf(between)),
            ("program", Diagnostics.LogValue.ProgramOf(between)));
    }

    // A video behind a minimised window, or one on another desktop, stops playing.
    private void Hidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        if (!_stopping) _arrived.Set();
    }

    private void RetryFollow(object? sender, EventArgs e)
    {
        _retry?.Stop();
        Follow();
    }

    private nint PreviousShown(nint window)
    {
        nint above = User32.GetWindow(window, PreviousAbove);
        while (above != 0 && above != Handle && !User32.IsWindowVisible(above)) above = User32.GetWindow(above, PreviousAbove);
        return above;
    }

    private static nint NextShown(nint window)
    {
        nint below = User32.GetWindow(window, NextBelow);
        while (below != 0 && !User32.IsWindowVisible(below)) below = User32.GetWindow(below, NextBelow);
        return below;
    }

    internal static bool GraphicsCardPresent => GraphicsCard.Value;

    private static readonly Lazy<bool> GraphicsCard = new(() =>
    {
        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1(0, out IDXGIAdapter1? adapter).Failure || adapter is null) return false;
            using (adapter) return adapter.Description1.VendorId != SoftwareRendererVendor;
        }
        catch (SharpGen.Runtime.SharpGenException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            return false;
        }
    });

    private void CreateDevice(Size size)
    {
        // Video support lets a video play behind Glass; without it Glass still works.
        if (D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_0], out _device, out _, out _context).Failure)
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
        _backdropShader = _device.CreateComputeShader(Compiler.Compile(ContentShaders.Backdrop, "Backdrop", "backdrop", "cs_5_0").Span);
        _options = _device.CreateBuffer(new BufferDescription((uint)sizeof(Options), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _place = _device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _edge = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _repeat = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap));

        using var factory = CreateDXGIFactory1<IDXGIFactory2>();
        _chain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = (uint)size.Width, Height = (uint)size.Height, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Premultiplied,
        });

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

    private void Render()
    {
        var size = Size.Empty;
        ID3D11Texture2D? cache = null, tiles = null, output = null, staging = null, final = null;
        ID3D11ShaderResourceView? cacheView = null, outputRead = null;
        ID3D11UnorderedAccessView? tilesView = null, outputView = null, finalView = null;
        Backdrop? backdrop = null, leaving = null;
        BackdropChoice? left = null;
        BackdropSet? shown = null;
        var turns = new Turns();
        bool drawn = false;
        try
        {
            while (true)
            {
                int wait = backdrop?.Wait ?? Timeout.Infinite;
                // Nothing could be opened: a drive or the internet may come back, so it is tried again now and then.
                if (backdrop is null && !_hidden && !_held && turns.RetryAt != long.MaxValue)
                    wait = (int)Math.Clamp(turns.RetryAt - Environment.TickCount64, 1, int.MaxValue);
                // Hidden or held, nothing changes until it shows again.
                if (backdrop is not null && !_hidden && !_held && turns.ChangeAt != long.MaxValue)
                {
                    int due = (int)Math.Clamp(turns.ChangeAt - Environment.TickCount64, 1, int.MaxValue);
                    wait = wait == Timeout.Infinite ? due : Math.Min(wait, due);
                }
                if (WaitHandle.WaitAny([_arrived, _stop], wait) == 1)
                    break;

                bool redraw = false;
                bool changed = _changed;
                _changed = false;
                BackdropSet? chosen = _backdrop;
                // A new fit, volume or speed is taken as it is: opening the file again would start a video over.
                if (!Equals(chosen, shown))
                {
                    if (!(chosen?.SameItems(shown) ?? shown is null))
                    {
                        backdrop?.Dispose();
                        leaving?.Dispose();
                        leaving = null;
                        turns = new Turns(chosen is { Items.Count: > 0 } && _look == ContentLook.Glass
                            ? new BackdropSequence(chosen.Items.Count, chosen.Order, Random.Shared) : null);
                        backdrop = chosen is null ? null : OpenNext(chosen, turns);
                    }
                    else if (backdrop is not null && chosen is not null)
                    {
                        BackdropChoice now = chosen.Items[turns.Showing];
                        _showing = now;
                        turns.ChangeAt = chosen.StayMilliseconds(now) is { } stay ? turns.Since + stay : long.MaxValue;
                    }
                    shown = chosen;
                    redraw = true;
                }
                if (backdrop is null && shown is not null && !_hidden && !_held && Environment.TickCount64 >= turns.RetryAt)
                {
                    turns.Failed = 0;
                    backdrop = OpenNext(shown, turns);
                }
                // The next of a set: the video played through, or its time is up. The last one's picture stays until the
                // next has one, so no plain Glass shows in between.
                if (backdrop is not null && shown is { Items.Count: > 1 } && !_hidden && !_held &&
                    (backdrop.Ended || Environment.TickCount64 >= turns.ChangeAt))
                {
                    if (backdrop.View is not null)
                    {
                        leaving?.Dispose();
                        (leaving, left) = (backdrop, _showing);
                        leaving.Sound(0);
                        leaving.Pause(true);
                    }
                    else backdrop.Dispose();
                    backdrop = OpenNext(shown, turns);
                    redraw = true;
                }
                if (backdrop is not null)
                {
                    try
                    {
                        backdrop.Pause(_hidden || _held);
                        backdrop.Sound(_audible && !_hidden && !_held ? _showing?.Volume ?? 0 : 0);
                        backdrop.Speed(_showing?.Speed ?? 100);
                        if (backdrop.Update(_device!, _context!))
                        {
                            redraw = true;
                            turns.Failed = 0;
                            turns.RetryWait = Turns.FirstRetry;
                            // Its time counts from when it shows, not from when its link was asked for.
                            if (!turns.Seen)
                            {
                                turns.Seen = true;
                                turns.Since = Environment.TickCount64;
                                if (shown is not null && _showing is { } item)
                                    turns.ChangeAt = shown.StayMilliseconds(item) is { } stay ? turns.Since + stay : long.MaxValue;
                            }
                        }
                        if (backdrop is VideoBackdrop { Failed: true }) throw new NotSupportedException();
                        if (leaving is not null && backdrop.View is not null)
                        {
                            leaving.Dispose();
                            leaving = null;
                            redraw = true;
                        }
                    }
                    catch (Exception failure) when (failure is SharpGen.Runtime.SharpGenException or COMException or NotSupportedException
                                                        or InvalidOperationException or ArgumentException)
                    {
                        Diagnostics.Log.Error("effect.backdropFailed", failure);
                        backdrop.Dispose();
                        Report(_showing, failure);
                        turns.Failed++;
                        // With others in the set, the next one shows instead.
                        backdrop = shown is null ? null : OpenNext(shown, turns);
                        redraw = true;
                    }
                }
                if (backdrop is null && leaving is not null)
                {
                    leaving.Dispose();
                    leaving = null;
                    redraw = true;
                }

                Direct3D11CaptureFrame? next = null;
                while (_pool!.TryGetNextFrame() is { } newer)
                {
                    next?.Dispose();
                    next = newer;
                }

                if (next is not null)
                    using (next)
                    {
                        var content = new Size(next.ContentSize.Width, next.ContentSize.Height);
                        if (content.Width >= 16 && content.Height >= 16)
                        {
                            if (content != size)
                            {
                                size = content;
                                DisposeAll(cache, tiles, output, staging, final, cacheView, outputRead, tilesView, outputView, finalView);
                                int columns = size.Width / PageReader.Tile, rows = size.Height / PageReader.Tile;
                                cache = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.ShaderResource));
                                cacheView = _device.CreateShaderResourceView(cache);
                                tiles = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)columns, (uint)rows, 1, 1, BindFlags.UnorderedAccess));
                                tilesView = _device.CreateUnorderedAccessView(tiles);
                                staging = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)columns, (uint)rows, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                                output = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.UnorderedAccess | BindFlags.ShaderResource));
                                outputView = _device.CreateUnorderedAccessView(output);
                                outputRead = _device.CreateShaderResourceView(output);
                                final = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.UnorderedAccess));
                                finalView = _device.CreateUnorderedAccessView(final);
                                _chain!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
                                _pool.Recreate(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, next.ContentSize);
                                _page.Reset();
                                Size newSize = size;
                                BeginInvoke(() => { if (!IsDisposed) ClientSize = newSize; });
                            }

                            using (var texture = WgcInterop.Texture(next))
                                _context!.CopySubresourceRegion(cache, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, size.Width, size.Height, 1));
                            Process(size, cacheView!, tilesView!, tiles!, staging!, outputView!);
                            Frames++;
                            drawn = true;
                            redraw = true;
                            changed = false;
                        }
                    }

                if (changed && drawn)
                {
                    Process(size, cacheView!, tilesView!, tiles!, staging!, outputView!);
                    redraw = true;
                }

                if (drawn && redraw)
                    Present(size, output!, outputRead!, final!, finalView!, leaving ?? backdrop, leaving is not null ? left : _showing);
            }
        }
        catch (Exception failure) when (failure is SharpGen.Runtime.SharpGenException or COMException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_stopping)
                BeginInvoke(() => Failed?.Invoke(Localization.UiText.T("errors.glass.stopped")));
        }
        finally
        {
            backdrop?.Dispose();
            leaving?.Dispose();
            DisposeAll(cache, tiles, output, staging, final, cacheView, outputRead, tilesView, outputView, finalView);
        }
    }

    // Where the drawing thread is in a set: which shows, when the next is due, how many failed in a row, and when to try
    // again once none opened, each time after twice as long.
    private sealed class Turns(BackdropSequence? sequence = null)
    {
        public BackdropSequence? Sequence => sequence;
        public int Showing = -1;
        public bool Seen;
        public long Since;
        public const int FirstRetry = 5_000, LongestRetry = 300_000;
        public int RetryWait = FirstRetry;
        public long RetryAt = long.MaxValue;
        public long ChangeAt = long.MaxValue;
        public int Failed;
    }

    private Backdrop? OpenNext(BackdropSet set, Turns turns)
    {
        turns.Seen = false;
        turns.RetryAt = long.MaxValue;
        while (turns.Sequence is { } sequence && turns.Failed < set.Items.Count)
        {
            turns.Showing = sequence.Next();
            BackdropChoice item = set.Items[turns.Showing];
            _showing = item;
            turns.Since = Environment.TickCount64;
            turns.ChangeAt = set.StayMilliseconds(item) is { } stay ? turns.Since + stay : long.MaxValue;
            try
            {
                return Backdrop.Open(item, once: set.PlaysOnce);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException
                                                or NotSupportedException or ExternalException)
            {
                Diagnostics.Log.Error("effect.backdropFailed", failure);
                Report(item, failure);
                turns.Failed++;
            }
        }
        _showing = null;
        turns.ChangeAt = long.MaxValue;
        if (turns.Sequence is not null)
        {
            turns.RetryAt = Environment.TickCount64 + turns.RetryWait;
            turns.RetryWait = Math.Min(turns.RetryWait * 2, Turns.LongestRetry);
        }
        return null;
    }

    private void Report(BackdropChoice? item, Exception failure)
    {
        if (item is null || _stopping) return;
        // Looked at here, not on the thread that answers clicks: a share that is gone takes seconds to say so.
        bool missing = failure is FileNotFoundException or DirectoryNotFoundException || item.IsMissing;
        try { BeginInvoke(() => BackdropFailed?.Invoke(item.File, missing)); }
        catch (InvalidOperationException closed) { Diagnostics.Log.Swallowed(closed); }
    }

    private void Present(Size size, ID3D11Texture2D output, ID3D11ShaderResourceView outputRead, ID3D11Texture2D final,
        ID3D11UnorderedAccessView finalView, Backdrop? backdrop, BackdropChoice? item)
    {
        var context = _context!;
        ID3D11Texture2D shown = output;
        if (backdrop?.View is { } picture && item is { } choice)
        {
            RectangleF place = BackdropChoice.Place(choice.Fit, backdrop.Size, size);
            float detail = choice.Fit == BackdropFit.Tile ? 0
                : MathF.Max(0, MathF.Log2(MathF.Max(backdrop.Size.Width / place.Width, backdrop.Size.Height / place.Height)));
            (float pr, float pg, float pb) = _page.Page;
            var where = new Place
            {
                X = place.X, Y = place.Y, Width = place.Width, Height = place.Height,
                Tile = choice.Fit == BackdropFit.Tile ? 1 : 0, Detail = detail, Brightness = Math.Clamp(choice.Brightness, 0, 100) / 100f,
                PageR = pr, PageG = pg, PageB = pb,
                Flip = Flips(_levels.Value) ? 1 : 0,
            };
            context.UpdateSubresource(in where, _place!);
            context.CSSetShader(_backdropShader);
            context.CSSetConstantBuffer(0, _place);
            context.CSSetShaderResource(0, outputRead);
            context.CSSetShaderResource(1, picture);
            context.CSSetSampler(0, _edge);
            context.CSSetSampler(1, _repeat);
            context.CSSetUnorderedAccessView(0, finalView);
            context.Dispatch((uint)(size.Width + 15) / 16, (uint)(size.Height + 15) / 16, 1);
            context.CSSetUnorderedAccessView(0, null);
            context.CSSetShaderResource(0, null);
            context.CSSetShaderResource(1, null);
            shown = final;
        }
        using (var back = _chain!.GetBuffer<ID3D11Texture2D>(0))
            context.CopyResource(back, shown);
        _chain.Present(0, PresentFlags.None);
    }

    private void Process(Size size, ID3D11ShaderResourceView cacheView, ID3D11UnorderedAccessView tilesView, ID3D11Texture2D tiles,
        ID3D11Texture2D staging, ID3D11UnorderedAccessView outputView)
    {
        var context = _context!;
        int columns = size.Width / PageReader.Tile, rows = size.Height / PageReader.Tile;
        context.CSSetShader(_stats);
        context.CSSetShaderResource(0, cacheView);
        context.CSSetUnorderedAccessView(0, tilesView);
        context.Dispatch((uint)(columns + 7) / 8, (uint)(rows + 7) / 8, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        // This frame's own page colour. A still window sends a single frame, so it can't lag behind. The statistics
        // are small and take well under a millisecond, so waiting for them is cheap.
        context.CopyResource(staging, tiles);
        ReadPage(staging, columns, rows);

        (float pr, float pg, float pb) = _page.Page;
        ContentLevels levels = _levels.Value;
        var options = new Options
        {
            Background = levels.Background, Text = levels.Text, FadeRange = levels.FadeRange, Shadow = levels.Shadow ? 1 : 0,
            Mode = _look == ContentLook.Dark ? 2 : levels.Plain ? 1 : 0, AlreadyDark = _leaveAlreadyDark && _page.AlreadyDark ? 1 : 0,
            PageR = pr, PageG = pg, PageB = pb, Flip = Flips(levels) ? 1 : 0,
        };
        Settings.KeptArea[] kept = _kept;
        float scale = User32.GetDpiForWindow(_source) is var dpi and > 0 ? dpi / 96f : 1;
        int count = 0;
        foreach (Settings.KeptArea area in kept)
        {
            if (count == Options.MostAreas) break;
            Rectangle place = area.In(size, scale);
            if (place.IsEmpty) continue;
            options.Areas[count * 4] = place.Left;
            options.Areas[count * 4 + 1] = place.Top;
            options.Areas[count * 4 + 2] = place.Right;
            options.Areas[count * 4 + 3] = place.Bottom;
            count++;
        }
        options.KeptCount = count;
        context.UpdateSubresource(in options, _options!);
        context.CSSetShader(_composite);
        context.CSSetConstantBuffer(0, _options);
        context.CSSetShaderResource(0, cacheView);
        context.CSSetUnorderedAccessView(0, outputView);
        context.Dispatch((uint)(size.Width + 15) / 16, (uint)(size.Height + 15) / 16, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
    }

    private void ReadPage(ID3D11Texture2D staging, int columns, int rows)
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

        _page.Read(stats);
    }

    private bool Flips(ContentLevels levels) => _look == ContentLook.Glass && !levels.Plain && levels.TextColor switch
    {
        GlassText.Light => _page.Light,
        GlassText.Dark => !_page.Light,
        _ => false,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Place
    {
        public float X, Y, Width, Height;
        public float Tile, Detail, Brightness, Unused2;
        public float PageR, PageG, PageB, Flip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Options
    {
        public float Background, Text, FadeRange, Shadow;
        public const int MostAreas = 8;
        public float Mode, AlreadyDark, KeptCount, Unused4;
        public float PageR, PageG, PageB, Flip;
        public fixed float Areas[MostAreas * 4];
    }

    private static void DisposeAll(params IDisposable?[] items)
    {
        foreach (IDisposable? item in items)
            item?.Dispose();
    }

    // The caller reveals the window.
    public void Stop()
    {
        if (_stopping)
            return;
        _stopping = true;
        _retry?.Dispose();
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
        try { _session?.Dispose(); } catch (COMException caught) { Diagnostics.Log.Swallowed(caught); }
        try { _pool?.Dispose(); } catch (COMException caught) { Diagnostics.Log.Swallowed(caught); }
        _session = null;
        _pool = null;
        // It holds the graphics device and the driver's threads, and a garbage collection can be a long way off.
        _captureDevice?.Dispose();
        _captureDevice = null;
        if (_visual != 0) Marshal.Release(_visual);
        if (_target != 0) Marshal.Release(_target);
        if (_composition != 0) Marshal.Release(_composition);
        _visual = _target = _composition = 0;
        _chain?.Dispose();
        _options?.Dispose();
        _place?.Dispose();
        _edge?.Dispose();
        _repeat?.Dispose();
        _backdropShader?.Dispose();
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
