using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class WgcPipTests(ITestOutputHelper output)
{
    private static void OnUiThread(Action<Action<int>> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test(ms =>
                {
                    long end = Environment.TickCount64 + ms;
                    while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
                });
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [WgcTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_Wgc_presents_changes_crop_static_content_and_restores_hidden_source(bool hidden)
    {
        using var source = new TargetProcess(new(200, 160, 400, 300), "C81E1E,1E64C8");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(source.Handle);
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows);
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc, ShowDiagnostics = true });
            pip.ShowOnTop(); pip.Location = new(800, 120);
            try
            {
                Assert.True(Desktop.WaitFor(() => pip.BackendKind != PipBackendKind.DwmThumbnail, 15000, () => pump(20)), pip.DiagnosticSnapshot);
                Assert.Equal(PipBackendKind.WgcHardware, pip.BackendKind);
                Assert.Equal(source.Handle, User32.GetForegroundWindow());
                pip.SetCrop(new Rectangle(0, 0, 180, 300));
                pump(400);
                Assert.Equal(source.Handle, User32.GetForegroundWindow());
                Rectangle middle = new(pip.Left + pip.Width / 2 - 10, pip.Top + pip.Height / 2 - 10, 20, 20);
                Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0xC8, 0x1E, 0x1E)), pip.DiagnosticSnapshot);
                source.SetColors("1EB446");
                Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0x1E, 0xB4, 0x46)), 4000, () => pump(20)), pip.DiagnosticSnapshot);
                pip.SetOriginalHidden(hidden); pump(300);
                Assert.Equal(hidden, windows.IsHidden(source.Handle));
                source.SetColors("1E64C8");
                Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0x1E, 0x64, 0xC8)), 4000, () => pump(20)), pip.DiagnosticSnapshot);
                output.WriteLine(pip.DiagnosticSnapshot);
            }
            finally { pip.Close(); pump(200); }
            Assert.False(windows.IsHidden(source.Handle));
        });
    }

    private sealed class FaultCapture(bool software) : IPipCaptureBackend
    {
        private readonly TaskCompletionSource _completion = new();
        public Task Completion => _completion.Task;
        public PipDiagnostics Diagnostics { get; } = new();
        public long LastProgress => Stopwatch.GetTimestamp();
        public PipBackendKind Kind => software ? PipBackendKind.WgcSoftware : PipBackendKind.WgcHardware;
        public PipSessionState State { get; private set; } = PipSessionState.Starting;
        public Size SourceSize => new(400, 300);
        internal int DisposeCount;
        internal void Live() => State = PipSessionState.Live;
        internal void Fail(int hr)
        {
            Diagnostics.Failure = $"Injected device failure 0x{hr:X8}";
            State = PipSessionState.Failed;
            _completion.TrySetResult();
        }
        public void SetViewport(Rectangle destination, Rectangle source) { }
        public void SetOpacity(byte opacity) { }
        public void SetVisible(bool visible) { }
        public void SetSelection(Rectangle? selection) { }
        public void SetClient(Size client) { }
        public void SetRefresh(int source, int target) { }
        public void Dispose() { DisposeCount++; State = PipSessionState.Stopped; _completion.TrySetResult(); }
    }

    [Fact]
    public void Changing_the_frame_limit_or_scaling_keeps_capture_running()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var destination = new PipWindow(source.Handle, new WindowController([])) { Bounds = new(800, 120, 404, 304) };
            destination.Show();
            var created = new List<FaultCapture>();
            using var selector = new PipBackendSelector(WindowIdentity.Capture(source.Handle), destination.Handle,
                new() { Backend = PipBackendPreference.Wgc }, start =>
                {
                    var capture = new FaultCapture(start.Software); created.Add(capture); return capture;
                });
            selector.SetViewport(new(2, 2, 400, 300), new(0, 0, 400, 300));
            selector.Poll(destination.ClientSize);
            created[0].Live(); selector.Poll(destination.ClientSize);
            selector.Configure(new() { Backend = PipBackendPreference.Wgc, FrameRate = 30, Scaling = PipScaling.Crisp });
            selector.Poll(destination.ClientSize);
            Assert.Single(created);
            Assert.Equal(0, created[0].DisposeCount);
            Assert.Equal(PipBackendKind.WgcHardware, selector.Kind);
        });
    }

    [Theory]
    [InlineData(PipBackendPreference.Auto, 2)]
    [InlineData(PipBackendPreference.Wgc, 3)]
    public void Device_loss_returns_to_real_Dwm_once_and_pins_after_bounded_attempts(PipBackendPreference preference, int attempts)
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var destination = new PipWindow(source.Handle, new WindowController([])) { Bounds = new(800, 120, 404, 304) };
            destination.Show();
            var created = new List<FaultCapture>();
            using var selector = new PipBackendSelector(WindowIdentity.Capture(source.Handle), destination.Handle,
                new() { Backend = preference }, start =>
                {
                    var capture = new FaultCapture(start.Software); created.Add(capture); return capture;
                });
            selector.SetViewport(new(2, 2, 400, 300), new(0, 0, 400, 300));
            int warnings = 0;
            selector.Problem += _ => warnings++;
            selector.Poll(destination.ClientSize);
            Assert.Equal(PipBackendKind.DwmThumbnail, selector.Kind); // First frame is still unproven.
            created[0].Live(); selector.Poll(destination.ClientSize);
            Assert.Equal(PipBackendKind.WgcHardware, selector.Kind);
            for (int i = 0; i < attempts; i++)
            {
                created[^1].Fail(unchecked((int)0x887A0005));
                selector.Poll(destination.ClientSize);
            }
            for (int i = 0; i < 100; i++) selector.Poll(destination.ClientSize);
            Assert.Equal(attempts, created.Count);
            Assert.Equal(1, warnings);
            Assert.Equal(PipSessionState.Fallback, selector.State);
            Assert.False(selector.Retry());
            // Saving unrelated preferences or changing diagnostic detail must not reset the circuit.
            selector.Configure(new() { Backend = preference, ShowDiagnostics = true, OpacityPercent = 90 });
            for (int i = 0; i < 10; i++) selector.Poll(destination.ClientSize);
            Assert.Equal(attempts, created.Count);
            Assert.Equal(PipSessionState.Fallback, selector.State);
            var middle = new Rectangle(destination.Left + 150, destination.Top + 100, 20, 20);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0xC8, 0x1E, 0x1E)),
                3000, () => pump(20)), $"Fallback showed {Desktop.ColorOnScreen(middle)}.\n{selector.Snapshot()}");
        });
    }

    [Fact]
    public void Forced_Dwm_allocates_no_capture_session_and_dispose_restores_a_hidden_original()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var destination = new Form { Bounds = new(800, 120, 404, 304) };
            using var selector = new PipBackendSelector(WindowIdentity.Capture(source.Handle), destination.Handle,
                new() { Backend = PipBackendPreference.Dwm }, _ => throw new Xunit.Sdk.XunitException("DWM allocated capture resources"));
            for (int i = 0; i < 100; i++) selector.Poll(destination.ClientSize);
            var windows = new WindowController([]);
            var pip = new PipWindow(source.Handle, windows);
            pip.ShowOnTop(); pip.SetOriginalHidden(true); pump(100);
            Assert.True(windows.IsHidden(source.Handle));
            pip.Dispose(); pip.Dispose(); pump(100);
            Assert.False(windows.IsHidden(source.Handle));
        });
    }

    [WgcFact]
    public void Wgc_software_is_only_proven_after_real_capture_to_present()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var destination = new Form { Bounds = new(800, 120, 404, 304), FormBorderStyle = FormBorderStyle.None, TopMost = true };
            destination.Show();
            using var capture = new WgcCaptureSession(WindowIdentity.Capture(source.Handle), destination.Handle,
                destination.ClientSize, PipDisplay.MonitorFromWindow(destination.Handle), new(), 144, 144, true);
            capture.SetViewport(new(2, 2, 400, 300), new(0, 0, 400, 300));
            Assert.True(Desktop.WaitFor(() => capture.State is PipSessionState.Live or PipSessionState.Failed, 10000, () => pump(20)));
            Assert.Equal(PipSessionState.Live, capture.State);
            Assert.True(capture.Diagnostics.Capabilities?.WarpUsable);
            output.WriteLine(capture.Diagnostics.Snapshot(capture.Kind, capture.State, 60, 144, 144, true));
            capture.Dispose();
            Assert.True(Desktop.WaitFor(() => capture.Completion.IsCompleted, 5000, () => pump(10)));
        });
    }

    [WgcFact]
    public void Resizing_capture_keeps_arriving_and_completes_shutdown_without_callback_deadlock()
    {
        using var source = new TargetProcess(new(160, 160, 400, 300));
        source.Animate(144);
        OnUiThread(pump =>
        {
            using var destination = new Form { ClientSize = new(480, 320), Location = new(900, 150),
                StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
            destination.Show();
            using var capture = new WgcCaptureSession(WindowIdentity.Capture(source.Handle), destination.Handle,
                destination.ClientSize, PipDisplay.MonitorFromWindow(destination.Handle), new(), 60, 60, false);
            try
            {
                capture.SetViewport(destination.ClientRectangle, new(0, 0, 400, 300));
                Assert.True(Desktop.WaitFor(() => capture.State == PipSessionState.Live, 10000, () => pump(10)));
                for (int i = 0; i < 120; i++)
                {
                    source.Resize(new(400 + i % 31, 300 + i % 23));
                    pump(10);
                }
                source.Resize(new(503, 307));
                Assert.True(Desktop.WaitFor(() => capture.SourceSize == new Size(503, 307), 8000, () => pump(10)),
                    capture.Diagnostics.Snapshot(capture.Kind, capture.State, 60, 60, 60, true));
                long before = capture.Diagnostics.Received;
                Assert.True(Desktop.WaitFor(() => capture.Diagnostics.Received > before + 3, 3000, () => pump(10)));
            }
            finally { capture.Dispose(); }
            Assert.True(Desktop.WaitFor(() => capture.Completion.IsCompleted, 5000, () => pump(10)),
                "Capture did not finish after resizing; a callback may be blocking Recreate.");
        });
    }

    [WgcFact]
    public void Independent_live_sessions_survive_one_hundred_resizes_and_source_exit()
    {
        using var source = new TargetProcess(new(160, 160, 400, 300));
        using var other = new TargetProcess(new(100, 520, 400, 300), "1EB446");
        source.Animate(144);
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            using var second = new PipWindow(other.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc, ShowDiagnostics = true });
            second.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ShowOnTop(); second.ShowOnTop(); pip.Location = new(800, 120); second.Location = new(1400, 120);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind == PipBackendKind.WgcHardware && second.BackendKind == PipBackendKind.WgcHardware,
                15000, () => pump(20)), pip.DiagnosticSnapshot + second.DiagnosticSnapshot);
            using var process = Process.GetCurrentProcess();
            long before = process.PrivateMemorySize64;
            for (int i = 0; i < 100; i++)
            {
                source.Resize(new(400 + i % 17, 300 + i % 13));
                pip.SetCrop(new(i % 9, i % 7, 180 + i % 30, 200));
                int x = pip.Left + 50, y = pip.Top + 50;
                User32.PostMessage(pip.Handle, 0x020A, (nint)((i % 2 == 0 ? 120 : -120) << 16), (y << 16) | x);
                pump(5);
            }
            source.Resize(new(503, 307)); source.SetColors("1E64C8"); pip.SetCrop(Rectangle.Empty);
            pump(1000);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind == PipBackendKind.WgcHardware, 8000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.Contains("Width=503, Height=307", pip.DiagnosticSnapshot);
            process.Refresh();
            Assert.InRange(process.PrivateMemorySize64 - before, -128_000_000, 128_000_000);
            source.Dispose();
            Assert.True(Desktop.WaitFor(() => pip.IsDisposed, 4000, () => pump(20)));
            Assert.Equal(PipBackendKind.WgcHardware, second.BackendKind);
            second.Close(); pump(150);
        });
    }

    [WgcFact]
    public void Minimized_source_keeps_cached_pixels_and_resumes_without_changing_backend()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ShowOnTop(); pip.Location = new(800, 120);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind == PipBackendKind.WgcHardware, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            User32.ShowWindow(source.Handle, 6); // SW_MINIMIZE
            pump(2200);
            Assert.Equal(PipSessionState.SourcePaused, pip.BackendState);
            pip.SetCrop(new(0, 0, 180, 300)); pump(200);
            var middle = new Rectangle(pip.Left + pip.Width / 2 - 10, pip.Top + pip.Height / 2 - 10, 20, 20);
            Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0xC8, 0x1E, 0x1E)), pip.DiagnosticSnapshot);
            User32.ShowWindow(source.Handle, User32.SW_RESTORE);
            source.SetColors("1EB446");
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0x1E, 0xB4, 0x46)), 4000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.Equal(PipBackendKind.WgcHardware, pip.BackendKind);
            pip.Close(); pump(150);
        });
    }
}
