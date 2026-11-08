using System.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Free-threaded capture publishes only the newest owned frame. One worker owns all GPU operations.</summary>
internal sealed class WgcCaptureSession : IPipCaptureBackend
{
    private sealed class Frame(Direct3D11CaptureFrame value, int generation) : IDisposable
    {
        private Direct3D11CaptureFrame? _value = value;
        internal Direct3D11CaptureFrame Value => _value!;
        internal int Generation { get; } = generation;
        internal long Arrival { get; } = Stopwatch.GetTimestamp();
        public void Dispose() => Interlocked.Exchange(ref _value, null)?.Dispose();
    }
    private sealed record View(Size Client, Rectangle Destination, Rectangle Crop, byte Opacity,
        Rectangle? Selection, bool Visible, int SourceHz, int TargetHz, int FrameRate, PipScaling Scaling);
    private readonly object _captureGate = new(), _viewGate = new();
    private readonly LatestFrame<Frame> _frames = new();
    private readonly PipWorkerSignal _wake = new();
    private readonly PipPreferences _preferences;
    private readonly PipSource _source;
    private readonly nint _destination, _monitor;
    private readonly bool _software;
    private readonly Thread _worker;
    private View _view;
    private bool _dirty = true;
    private volatile bool _stopping;
    private volatile PipSessionState _state = PipSessionState.Starting;
    private int _generation;
    private Size _sourceSize;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;
    public PipDiagnostics Diagnostics { get; } = new();
    internal Exception? Failure { get; private set; }
    internal long Started { get; } = Stopwatch.GetTimestamp();
    private long _progress = Stopwatch.GetTimestamp();
    public long LastProgress => Interlocked.Read(ref _progress);

    internal WgcCaptureSession(WindowIdentity identity, nint destination, Size client, nint monitor,
        PipPreferences preferences, int sourceHz, int targetHz, bool software)
        : this(new WindowPipSource(identity), destination, client, monitor, preferences, sourceHz, targetHz, software)
    {
    }

    internal WgcCaptureSession(PipSource source, nint destination, Size client, nint monitor,
        PipPreferences preferences, int sourceHz, int targetHz, bool software)
    {
        _source = source;
        _destination = destination;
        _monitor = monitor;
        _preferences = preferences;
        _software = software;
        _view = new(client, Rectangle.Empty, Rectangle.Empty, preferences.Alpha, null, true, sourceHz, targetHz,
            preferences.FrameRate, preferences.Scaling);
        _worker = new Thread(Run) { IsBackground = true, Name = "WinScreen PiP capture" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public PipBackendKind Kind => _software ? PipBackendKind.WgcSoftware : PipBackendKind.WgcHardware;
    public PipSessionState State => _state;
    public Size SourceSize { get { lock (_viewGate) return _sourceSize; } }
    private void Change(Func<View, View> update)
    {
        lock (_viewGate)
        {
            View changed = update(_view);
            if (changed == _view || _stopping) return;
            _view = changed; _dirty = true;
            _wake.Set();
        }
    }
    public void SetClient(Size size) => Change(v => v with { Client = size });
    public void SetRefresh(int source, int target) => Change(v => v with { SourceHz = source, TargetHz = target });
    public void SetPacing(int frameRate, PipScaling scaling) => Change(v => v with { FrameRate = frameRate, Scaling = scaling });
    public void SetViewport(Rectangle destination, Rectangle source) => Change(v => v with { Destination = destination, Crop = source });
    public void SetOpacity(byte opacity) => Change(v => v with { Opacity = opacity });
    public void SetVisible(bool visible) => Change(v => v with { Visible = visible });
    public void SetSelection(Rectangle? selection) => Change(v => v with { Selection = selection });

    private void Run()
    {
        GraphicsCaptureItem? item = null;
        IDirect3DDevice? winrtDevice = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        D3D11Renderer? renderer = null;
        int closed = 0;
        void Closed(GraphicsCaptureItem _, object __)
        {
            lock (_viewGate)
            {
                if (_stopping) return;
                Interlocked.Exchange(ref closed, 1); _wake.Set();
            }
        }
        void Arrived(Direct3D11CaptureFramePool sender, object _)
        {
            lock (_captureGate)
            {
                if (_stopping) return;
                try
                {
                    Direct3D11CaptureFrame? newest = null;
                    try
                    {
                        // Pool has three slots; never turn a busy producer into an unbounded drain loop.
                        for (int i = 0; i < 3; i++)
                        {
                            var next = sender.TryGetNextFrame();
                            if (next is null) break;
                            Diagnostics.Received++;
                            newest?.Dispose();
                            if (newest is not null) Interlocked.Increment(ref Diagnostics.Dropped);
                            newest = next;
                        }
                        if (newest is null) return;
                        Diagnostics.LastArrival = Stopwatch.GetTimestamp();
                        _frames.Publish(new Frame(newest, _generation));
                        newest = null;
                        _wake.Set();
                    }
                    finally { newest?.Dispose(); }
                }
                catch (Exception e)
                {
                    Failure = e;
                    Diagnostics.Exception = e;
                    Diagnostics.Failure = $"Capture callback: {e.GetType().Name} 0x{e.HResult:X8}";
                    _state = PipSessionState.Failed;
                    _wake.Set();
                }
            }
        }
        try
        {
            _source.Require();
            if (!ApiInformation.IsMethodPresent("Windows.Graphics.Capture.Direct3D11CaptureFramePool", "CreateFreeThreaded") ||
                !GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture unavailable.");
            renderer = new D3D11Renderer(_destination, _monitor, _preferences, Diagnostics, _software);
            winrtDevice = WgcInterop.Device(renderer.Device);
            _source.Require();
            item = _source.CreateItem();
            SizeInt32 size = item.Size;
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 67_108_864)
                throw new InvalidOperationException("Invalid capture item dimensions.");
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
            session = pool.CreateCaptureSession(item);
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                session.IsCursorCaptureEnabled = _source.ShowsCursor;
            // Windows' yellow capture border goes only if the person wants that and Windows allows it.
            Diagnostics.CaptureBorderHidden = CaptureBorder.TryHide(session);
            if (_preferences.RequireHiddenBorder && !Diagnostics.CaptureBorderHidden)
                throw new InvalidOperationException("A concealed source requires borderless capture.");
            item.Closed += Closed;
            pool.FrameArrived += Arrived;
            var pacer = new PipFramePacer(Stopwatch.Frequency, 60);
            int previousRate = -1, previousSourceHz = -1;
            long cachedTimestamp = 0, cachedArrival = 0;
            long? mismatchedSince = null;
            bool newCachedFrame = false, needsPresent = false, everPresented = false;
            session.StartCapture();
            while (!_stopping && Volatile.Read(ref closed) == 0 && _state != PipSessionState.Failed)
            {
                _wake.Wait(needsPresent || _frames.HasPending ? Math.Max(1, 1000 / Math.Max(15, previousRate)) : 100);
                if (_stopping || Volatile.Read(ref closed) != 0) break;
                View view;
                bool dirty;
                lock (_viewGate) { view = _view; dirty = _dirty; _dirty = false; }
                int rate = view.FrameRate == 0 ? view.TargetHz : view.FrameRate;
                renderer.ViewScaling = view.Scaling;
                if (_software) rate = Math.Min(rate, 30);
                if (rate != previousRate || view.SourceHz != previousSourceHz)
                {
                    pacer.SetRate(rate);
                    if (renderer.Capabilities.MinUpdateIntervalSupported)
                        session.MinUpdateInterval = TimeSpan.FromSeconds(Math.Clamp(0.5 / Math.Max(rate, view.SourceHz), .001, .016));
                    previousRate = rate; previousSourceHz = view.SourceHz;
                }
                bool sourcePaused = _source.IsMinimized;
                bool ready = renderer.ReadyForWork(), submitted = false;
                // Retain the capture buffer until EndWork has flushed its copy commands.
                using Frame? owned = ready ? _frames.Take() : null;
                if (owned is not null && owned.Generation == _generation)
                {
                    var frame = owned.Value;
                    SizeInt32 content = frame.ContentSize;
                    if (sourcePaused || content.Width <= 0 || content.Height <= 0)
                    {
                        Interlocked.Increment(ref Diagnostics.Dropped);
                    }
                    else if (content.Width != size.Width || content.Height != size.Height)
                    {
                        // Discard this frame: its surface may still have the old allocation size.
                        owned.Dispose();
                        lock (_captureGate)
                        {
                            _generation++;
                            _frames.Take()?.Dispose();
                            if (content.Width <= 0 || content.Height <= 0 ||
                                content.Width > 16384 || content.Height > 16384 || (long)content.Width * content.Height > 67_108_864)
                                throw new InvalidOperationException("Invalid resized capture dimensions.");
                            size = content;
                            pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                        }
                    }
                    else
                    {
                        using var texture = WgcInterop.Texture(frame.Surface);
                        var description = texture.Description;
                        if (content.Width > description.Width || content.Height > description.Height)
                        {
                            // Recreate can race the compositor's resize. ContentSize alone does not
                            // prove that this surface belongs to the new allocation generation.
                            Interlocked.Increment(ref Diagnostics.Dropped);
                            mismatchedSince ??= Stopwatch.GetTimestamp();
                            if (Stopwatch.GetElapsedTime(mismatchedSince.Value).TotalSeconds > 5)
                                throw new TimeoutException("Capture surfaces never reached their reported content size.");
                        }
                        else
                        {
                            mismatchedSince = null;
                            if (newCachedFrame) Interlocked.Increment(ref Diagnostics.Dropped);
                            renderer.Cache(texture, new Size(content.Width, content.Height));
                            submitted = true;
                            lock (_viewGate) _sourceSize = renderer.SourceSize;
                            cachedTimestamp = frame.SystemRelativeTime.Ticks;
                            cachedArrival = owned.Arrival;
                            newCachedFrame = true;
                            needsPresent = true;
                        }
                    }
                }
                needsPresent |= dirty;
                if (ready && renderer.HasFrame && view.Visible && needsPresent && view.Destination.Width > 0 &&
                    view.Destination.Height > 0 && (dirty || pacer.Accept(Stopwatch.GetTimestamp())))
                {
                    bool presented = renderer.Render(view.Client, view.Destination, view.Crop, view.Opacity,
                        view.Selection, cachedTimestamp, newCachedFrame, true, cachedArrival);
                    submitted = true;
                    if (presented)
                    {
                        if (newCachedFrame) Diagnostics.Accepted++;
                        everPresented = true;
                        needsPresent = false;
                        newCachedFrame = false;
                        _state = PipSessionState.Live;
                        if (_software) Diagnostics.Capabilities = renderer.Capabilities with { WarpUsable = true };
                    }
                }
                if (submitted) renderer.EndWork();
                if (!everPresented && Stopwatch.GetElapsedTime(Started).TotalSeconds > 5)
                    throw new TimeoutException("No presentable WGC frame within five seconds.");
                if (everPresented && (sourcePaused || Stopwatch.GetElapsedTime(Diagnostics.LastArrival).TotalSeconds > 2))
                    _state = PipSessionState.SourcePaused;
                Diagnostics.QueueDrops = _frames.Dropped;
                Interlocked.Exchange(ref _progress, Stopwatch.GetTimestamp());
            }
            if (Volatile.Read(ref closed) != 0 && !_stopping) throw new InvalidOperationException("Capture item closed.");
        }
        catch (Exception e)
        {
            Failure = e;
            Diagnostics.Exception = e;
            int reason = 0;
            try { if (renderer is not null) reason = renderer.RemovedReason; } catch (Exception) { }
            Diagnostics.Failure = $"{e.GetType().Name} 0x{e.HResult:X8}; device 0x{reason:X8}";
            _state = PipSessionState.Failed;
        }
        finally
        {
            lock (_captureGate) _stopping = true;
            void Release(Action dispose)
            {
                try { dispose(); }
                catch (Exception e)
                {
                    Diagnostics.Failure ??= $"Teardown: {e.GetType().Name} 0x{e.HResult:X8}";
                }
            }
            if (pool is not null) Release(() => pool.FrameArrived -= Arrived);
            if (item is not null) Release(() => item.Closed -= Closed);
            Release(() => session?.Dispose());
            Release(_frames.Dispose);
            Release(() => pool?.Dispose());
            Release(() => winrtDevice?.Dispose());
            Release(() => renderer?.Dispose());
            if (_state != PipSessionState.Failed) _state = PipSessionState.Stopped;
            lock (_viewGate) _wake.Dispose();
            _completion.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_viewGate)
        {
            if (_stopping) return;
            _stopping = true;
            _wake.Set();
        }
        // The HWND owner never waits on capture callbacks, GPU submission, or a driver.
    }
}
