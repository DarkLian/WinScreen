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

    // A view goes on passing its state to a capture that has ended, as when the PC is locked: 1.3.0-beta.11 crashed
    // when Freeze reached a finished capture's signal (the owner, 20 Nov 2026).
    [WgcFact]
    public void A_capture_that_has_ended_takes_freeze_and_view_changes_quietly()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var destination = new Form { ClientSize = new(480, 320), Location = new(900, 150),
                StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
            destination.Show();
            using var capture = new WgcCaptureSession(WindowIdentity.Capture(source.Handle), destination.Handle,
                destination.ClientSize, PipDisplay.MonitorFromWindow(destination.Handle), new(), 60, 60, false);
            capture.SetViewport(destination.ClientRectangle, new(0, 0, 400, 300));
            Assert.True(Desktop.WaitFor(() => capture.State == PipSessionState.Live, 10000, () => pump(10)));
            capture.Dispose();
            Assert.True(Desktop.WaitFor(() => capture.Completion.IsCompleted, 10000, () => pump(10)), capture.State.ToString());
            capture.SetFrozen(true);
            capture.SetViewport(new(0, 0, 200, 150), new(0, 0, 400, 300));
            capture.SetFrozen(false);
            capture.SetOpacity(128);
        });
    }

    // Frozen, the view keeps its last picture while the window changes, still draws it when the view is resized, and
    // starts no visit; unfrozen, it shows the window as it is now.
    [WgcFact]
    public void A_frozen_view_keeps_its_picture_until_unfrozen()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ShowOnTop(); pip.Location = new(800, 120);
            Assert.True(Desktop.WaitFor(() => pip.BackendKind != PipBackendKind.DwmThumbnail, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            Rectangle Middle() => new(pip.Left + pip.Width / 2 - 10, pip.Top + pip.Height / 2 - 10, 20, 20);
            Color red = Color.FromArgb(0xC8, 0x1E, 0x1E), green = Color.FromArgb(0x1E, 0xB4, 0x46);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle()), red), 4000, () => pump(20)), pip.DiagnosticSnapshot);

            pip.SetFrozen(true);
            Assert.True(pip.Frozen);
            source.SetColors("1EB446");
            pump(800);
            Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(Middle()), red), $"The frozen view showed {Desktop.ColorOnScreen(Middle())}.");
            pip.Size = new Size(pip.Width + 80, pip.Height + 60);
            pump(500);
            Assert.True(Desktop.IsNear(Desktop.ColorOnScreen(Middle()), red), $"Resized, the frozen view showed {Desktop.ColorOnScreen(Middle())}.");

            pip.SetFrozen(false);
            Assert.False(pip.Frozen);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Middle()), green), 4000, () => pump(20)),
                $"Unfrozen, the view showed {Desktop.ColorOnScreen(Middle())}.");
        });
    }

    // Owner, 22 Nov: an original shown from its view and then minimized stopped drawing, and the view stayed on its last
    // picture. It goes behind its view instead, off the screen and not minimized, and the view goes on updating.
    // Then: it should act as a minimized window does with no view, keeping its taskbar button and Alt+Tab entry, and
    // come back when picked there (Settings, on by default). Turned off, Keep hidden goes on, off the taskbar.
    [WgcTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_original_minimized_while_shown_goes_behind_its_view_and_keeps_drawing(bool keepOnTaskbar)
    {
        using var source = new TargetProcess(new(200, 160, 400, 300), "C81E1E", "Minimized source");
        using var other = new TargetProcess(new(260, 500, 300, 200), "1EC864", "Another window");
        OnUiThread(pump =>
        {
            Desktop.GainForegroundRights();
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows);
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ApplyViewPreferences(new PipViewPreferences { KeepMinimizedOriginalsOnTaskbar = keepOnTaskbar });
            pip.ShowOnTop(); pip.Location = new(800, 120);
            try
            {
                Assert.True(Desktop.WaitFor(() => pip.BackendKind != PipBackendKind.DwmThumbnail, 15000, () => pump(20)), pip.DiagnosticSnapshot);
                pip.HideOriginalByDefault();
                pump(300);
                Desktop.Activate(other.Handle); // Used just before, so Windows gives it the focus on minimize.
                pump(200);
                pip.GoToSource();
                pump(500);
                Assert.False(windows.IsHidden(source.Handle), "Not shown.");
                User32.ShowWindow(source.Handle, 6); // Its minimize button.
                Assert.True(Desktop.WaitFor(() => windows.IsHidden(source.Handle) && !User32.IsIconic(source.Handle), 3000, () => pump(20)),
                    $"Minimized: hidden {windows.IsHidden(source.Handle)}, minimized {User32.IsIconic(source.Handle)}.");
                Rectangle at = Desktop.BoundsOf(source.Handle);
                Assert.False(Screen.AllScreens.Any(s => Rectangle.Intersect(s.Bounds, at) is { Width: > 2, Height: > 2 }), $"Hidden on the screen at {at}.");
                Assert.True(pip.KeepsUpdating != keepOnTaskbar && pip.HiddenWhileMinimized == keepOnTaskbar, pip.StateDescription);
                if (keepOnTaskbar) Assert.False(windows.IsOffTaskbar(source.Handle), "Minimized, it left the taskbar.");
                else if (Environment.OSVersion.Version.Build >= 22000) Assert.True(windows.IsOffTaskbar(source.Handle), "Keep hidden left it on the taskbar.");
                Rectangle middle = new(pip.Left + pip.Width / 2 - 10, pip.Top + pip.Height / 2 - 10, 20, 20);
                source.SetColors("1E64C8");
                Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(middle), Color.FromArgb(0x1E, 0x64, 0xC8)), 4000, () => pump(20)),
                    "The view didn't update: " + pip.DiagnosticSnapshot);
                if (!keepOnTaskbar) return;

                // Owner, 22 Nov: used directly from the view and then Watch only, it was shown again. It is minimized
                // again, back on the taskbar and in Alt+Tab, which direct use took it off.
                pip.AllowInteractive = true; // A test window is used directly only with the experimental features.
                pip.SetInteractive(true);
                pump(300);
                Assert.True(pip.IsInteractive && !pip.HiddenWhileMinimized, pip.StateDescription);
                pip.SetInteractive(false);
                pump(300);
                Assert.True(windows.IsHidden(source.Handle), "Watch only showed the minimized original.");
                Assert.True(pip.HiddenWhileMinimized && !pip.KeepsUpdating && !pip.IsInteractive, pip.StateDescription);
                Assert.False(windows.IsOffTaskbar(source.Handle), "Back to Watch only, it stayed off the taskbar.");
                Assert.Equal(0, pip.UnexpectedModes);
                Desktop.Activate(other.Handle);
                pump(200);

                // Picked with a quick Alt+Tab from the window used before it, as one would a minimized window: it is
                // itself again.
                Assert.True(Desktop.WaitFor(() => User32.GetForegroundWindow() == other.Handle, 2000, () => pump(20)),
                    $"Minimized, the focus went to {WindowInfo.ClassName(User32.GetForegroundWindow())}, not the window used before.");
                // What Alt+Tab goes by: the order of windows from the top, and the original's styles.
                var order = new List<string>();
                for (nint w = User32.GetWindow(other.Handle, 0); w != 0 && order.Count < 400; w = User32.GetWindow(w, 2)) // GW_HWNDFIRST, GW_HWNDNEXT
                    if (w == source.Handle || w == other.Handle || w == pip.Handle) order.Add(w == source.Handle ? "original" : w == other.Handle ? "other" : "view");
                string before = $"order {string.Join(">", order)}; original ex 0x{Desktop.ExStyle(source.Handle):X} visible {User32.IsWindowVisible(source.Handle)} " +
                    $"at {Desktop.BoundsOf(source.Handle)} alpha {Desktop.Alpha(source.Handle)}; view ex 0x{Desktop.ExStyle(pip.Handle):X}";
                long pressedAt = Environment.TickCount64;
                Desktop.Key(Keys.Menu, true);
                Thread.Sleep(30);
                Desktop.Key(Keys.Tab, true);
                Desktop.Key(Keys.Tab, false);
                Thread.Sleep(30);
                Desktop.Key(Keys.Menu, false);
                bool picked = Desktop.WaitFor(() => !windows.IsHidden(source.Handle) && User32.GetForegroundWindow() == source.Handle, 3000, () => pump(20));
                string listed = picked ? "" : string.Join(" | ", SwitcherListTests.Names());
                Assert.True(picked,
                    $"Picked in Alt+Tab, it wasn't shown (Alt+Tab lists '{listed}'): hidden {windows.IsHidden(source.Handle)}, front '{WindowInfo.Title(User32.GetForegroundWindow())}' {Desktop.FrontDescription()}, {pip.LastArrival}; Alt+Tab pressed at {pressedAt}; before: {before}.");
                Assert.True(!pip.HiddenWhileMinimized && !pip.KeepsUpdating, pip.StateDescription);
                Assert.Equal(PipStateProblem.None, pip.FirstStateProblem);
            }
            finally { pip.Close(); pump(200); }
            Assert.False(windows.IsHidden(source.Handle));
        });
    }

    // Owner, 22 Nov: a wide view turned a quarter is made smaller to fit the screen; turned back, it had kept that
    // smaller size. It comes back at the size it was given.
    [WgcFact]
    public void A_view_made_smaller_to_fit_when_turned_has_its_size_again_turned_back()
    {
        using var source = new TargetProcess(new(200, 160, 900, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ShowOnTop(); pip.Location = new(100, 120);
            Assert.True(Desktop.WaitFor(() => pip.CanTurn, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            pump(300);
            Rectangle area = Screen.FromControl(pip).WorkingArea;
            // Resized by hand to wider than the screen is tall.
            pip.Size = new Size(Math.Min(area.Width - 40, area.Height * 3 / 2), Math.Min(area.Width - 40, area.Height * 3 / 2) / 3);
            pump(500);
            Rectangle upright = pip.Bounds;
            Assert.True(upright.Width > area.Height, $"Not wide enough to test: {upright} in {area}");
            pip.SetTurn(new ViewTurn(1, false));
            pump(500);
            Assert.True(pip.Height <= area.Height && pip.Height < upright.Width, $"Turned, it wasn't fitted to the screen: {pip.Bounds} in {area}");
            pip.SetTurn(ViewTurn.Upright);
            pump(500);
            Assert.True(Math.Abs(pip.Width - upright.Width) <= 2 && Math.Abs(pip.Height - upright.Height) <= 2,
                $"Turned back, {pip.Bounds}; before {upright}");
        });
    }

    // A quarter turn swaps the view's sides about its middle; a preset keeps the turn; on Windows' thumbnail the view
    // shows upright and the turn comes back with capture. The left half of a red and green window turned clockwise
    // is the view's top half.
    [WgcFact]
    public void A_turned_view_swaps_its_sides_keeps_its_turn_in_a_preset_and_waits_for_capture()
    {
        using var source = new TargetProcess(new(200, 160, 400, 300));
        OnUiThread(pump =>
        {
            using var pip = new PipWindow(source.Handle, new WindowController([]));
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            pip.ShowOnTop(); pip.Location = new(800, 120);
            Assert.True(Desktop.WaitFor(() => pip.CanTurn, 15000, () => pump(20)), pip.DiagnosticSnapshot);
            pump(300);
            Rectangle upright = pip.Bounds;
            Assert.True(upright.Width > upright.Height);
            var quarter = new ViewTurn(1, false);
            pip.SetTurn(quarter);
            pump(300);
            Assert.Equal(quarter, pip.Turn);
            Rectangle turned = pip.Bounds;
            Assert.True(Math.Abs(turned.Width - upright.Height) <= 2 && Math.Abs(turned.Height - upright.Width) <= 2, $"{upright} turned is {turned}.");
            Point middle(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
            Assert.True(Math.Abs(middle(turned).X - middle(upright).X) <= 2 && Math.Abs(middle(turned).Y - middle(upright).Y) <= 2);

            source.SetColors("C81E1E,1EB446"); // Left red, right green.
            Rectangle Spot(double x, double y) => new(pip.Left + (int)(pip.Width * x) - 5, pip.Top + (int)(pip.Height * y) - 5, 10, 10);
            Color red = Color.FromArgb(0xC8, 0x1E, 0x1E), green = Color.FromArgb(0x1E, 0xB4, 0x46);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Spot(.5, .2)), red) &&
                Desktop.IsNear(Desktop.ColorOnScreen(Spot(.5, .8)), green), 4000, () => pump(20)),
                $"Turned clockwise: top {Desktop.ColorOnScreen(Spot(.5, .2))}, bottom {Desktop.ColorOnScreen(Spot(.5, .8))}.");
            pip.SetTurn(new ViewTurn(1, true)); // Mirrored too: red stays on top.
            pump(300);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Spot(.5, .2)), red), 4000, () => pump(20)));
            pip.SetTurn(new ViewTurn(3, false)); // Counter-clockwise: red at the bottom.
            pump(300);
            Assert.True(Desktop.WaitFor(() => Desktop.IsNear(Desktop.ColorOnScreen(Spot(.5, .8)), red), 4000, () => pump(20)),
                $"Turned counter-clockwise: bottom {Desktop.ColorOnScreen(Spot(.5, .8))}.");

            PipPreset preset = pip.CurrentPreset("turned");
            Assert.Equal(3, preset.Turns);
            Assert.Null(preset.Mirrored);
            pip.SetTurn(ViewTurn.Upright);
            pump(300);
            Assert.True(pip.Width > pip.Height);
            Assert.Null(pip.CurrentPreset("upright").Turns);
            pip.ApplyPreset(preset);
            pump(300);
            Assert.Equal(new ViewTurn(3, false), pip.Turn);
            Assert.True(pip.Height > pip.Width);

            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Dwm });
            Assert.True(Desktop.WaitFor(() => !pip.CanTurn && pip.Turn.IsUpright, 4000, () => pump(20)), pip.DiagnosticSnapshot);
            Assert.Equal(new ViewTurn(3, false), pip.RequestedTurn);
            Assert.True(pip.Width > pip.Height);
            pip.ApplyPipPreferences(new() { Backend = PipBackendPreference.Wgc });
            Assert.True(Desktop.WaitFor(() => pip.Turn == new ViewTurn(3, false), 15000, () => pump(20)), pip.DiagnosticSnapshot);
            pip.Close(); pump(150);
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
            // One that can't be hidden: others go behind their view when minimized (owner, 22 Nov).
            source.RefuseToBeSeeThrough();
            User32.ShowWindow(source.Handle, 6); // SW_MINIMIZE
            pump(2200);
            Assert.True(User32.IsIconic(source.Handle), "Not left minimized.");
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
