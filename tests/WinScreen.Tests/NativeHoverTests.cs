using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class NativeHoverTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Start(uint thread, nint target, long token, nint source, nint marker);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long Renew(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool Stop(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long Inspect(nint session, int field);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);

    [NativeHoverFact]
    public void Removing_the_active_marker_disarms_filtering_before_the_hook_is_uninstalled()
    {
        using var target = new TargetProcess(new Rectangle(40, 40, 180, 100));
        target.BehaveLikeBrowser();
        var identity = WindowIdentity.Capture(target.Handle);
        nint module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll")), session = 0;
        var start = Marshal.GetDelegateForFunctionPointer<Start>(NativeLibrary.GetExport(module, "Start"));
        var renew = Marshal.GetDelegateForFunctionPointer<Renew>(NativeLibrary.GetExport(module, "Renew"));
        var stop = Marshal.GetDelegateForFunctionPointer<Stop>(NativeLibrary.GetExport(module, "Stop"));
        var inspect = Marshal.GetDelegateForFunctionPointer<Inspect>(NativeLibrary.GetExport(module, "Inspect"));
        try
        {
            Assert.True(User32.SetProp(target.Handle, WindowController.EnhancedHoverActiveProperty, 12345));
            session = start(identity.ThreadId, target.Handle, identity.Token, target.Handle, 12345);
            Assert.NotEqual(0, session);
            User32.PostMessage(target.Handle, 0x2A3, 777, 0);
            Assert.True(Desktop.WaitFor(() => renew(session) > 0));
            for (int i = 0; i < 1024; i++) Assert.True(User32.PostMessage(target.Handle, 0x2A3, 777, 0));
            Assert.True(Desktop.WaitFor(() => renew(session) >= 1025));
            long samples = inspect(session, 3), ticks = inspect(session, 4), frequency = inspect(session, 5), maximum = inspect(session, 6);
            Assert.True(samples > 0);
            output.WriteLine($"Eligible-leave stress (1025 filtered): native samples={samples} mean={ticks * 1_000_000.0 / frequency / samples:F2}us max={maximum * 1_000_000.0 / frequency:F2}us, includes CallNextHookEx.");
            Assert.Equal(12345, RemoveProp(target.Handle, WindowController.EnhancedHoverActiveProperty));
            User32.PostMessage(target.Handle, 0x2A3, 778, 0);
            Assert.True(Desktop.WaitFor(() => target.NextReport(10) == "event 2A3 778", 3000));
            output.WriteLine("Active marker removed: normal leave delivered while the hook registration was still installed.");
        }
        finally { if (session != 0) Assert.True(stop(session)); NativeLibrary.Free(module); }
    }

    [NativeHoverFact]
    public void Native_filter_preserves_unrelated_leaves_expires_and_releases_handles()
    {
        using var target = new TargetProcess(new Rectangle(40, 40, 180, 100));
        using var other = new TargetProcess(new Rectangle(260, 40, 180, 100));
        target.BehaveLikeBrowser(); other.BehaveLikeBrowser();
        var identity = WindowIdentity.Capture(target.Handle);
        nint module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll"));
        var start = Marshal.GetDelegateForFunctionPointer<Start>(NativeLibrary.GetExport(module, "Start"));
        var renew = Marshal.GetDelegateForFunctionPointer<Renew>(NativeLibrary.GetExport(module, "Renew"));
        var stop = Marshal.GetDelegateForFunctionPointer<Stop>(NativeLibrary.GetExport(module, "Stop"));
        bool Report(TargetProcess app, string expected) => Desktop.WaitFor(() => app.NextReport(10) == expected, 4000);
        nint session = 0;
        try
        {
            double Throughput()
            {
                var times = new List<double>();
                for (int round = 0; round < 5; round++)
                {
                    long stamp = Stopwatch.GetTimestamp();
                    for (int n = 0; n < 2000; n++) Assert.True(User32.PostMessage(target.Handle, 0, 0, 0));
                    User32.PostMessage(target.Handle, 0x6, 790 + round, 0);
                    Assert.True(Report(target, $"event 6 {790 + round}"));
                    times.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                }
                return times.Average();
            }
            double baseline = Throughput(), hooked = 0;
            int before = Process.GetCurrentProcess().HandleCount;
            var installs = new List<double>(); var removals = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                long stamp = Stopwatch.GetTimestamp();
                session = start(identity.ThreadId, target.Handle, identity.Token, 0, 0);
                installs.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                Assert.NotEqual(0, session);
                if (i == 0) hooked = Throughput();
                User32.PostMessage(target.Handle, 0x2A3, 777, 0);
                Assert.True(Desktop.WaitFor(() => renew(session) > 0));
                User32.PostMessage(other.Handle, 0x2A3, 777, 0);
                Assert.True(Report(other, "event 2A3 777"));
                // WM_ACTIVATE must stay untouched (only mouse leave is eligible).
                User32.PostMessage(target.Handle, 0x6, 778, 0);
                Assert.True(Report(target, "event 6 778"));
                stamp = Stopwatch.GetTimestamp(); Assert.True(stop(session)); session = 0;
                removals.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                User32.PostMessage(target.Handle, 0x2A3, 779, 0);
                Assert.True(Report(target, "event 2A3 779"));
            }
            session = start(identity.ThreadId, target.Handle, identity.Token, 0, 0);
            Assert.NotEqual(0, session);
            Thread.Sleep(3200); // No heartbeat: filtering must fail open.
            User32.PostMessage(target.Handle, 0x2A3, 780, 0);
            Assert.True(Report(target, "event 2A3 780"));
            Assert.True(stop(session)); session = 0;
            int after = Process.GetCurrentProcess().HandleCount;
            output.WriteLine($"30 installs mean={installs.Average():F3}ms max={installs.Max():F3}ms; removals mean={removals.Average():F3}ms max={removals.Max():F3}ms; host handles {before}->{after}");
            output.WriteLine($"2000 queued WM_NULL + completion marker, five rounds mean: baseline={baseline:F3}ms hook={hooked:F3}ms; scheduling/IPC included, not isolated callback CPU.");
            Assert.True(after <= before + 4, $"Handle growth: {before}->{after}");
            // OS removes a dead thread's hook; teardown must still release the native session.
            session = start(identity.ThreadId, target.Handle, identity.Token, 0, 0);
            target.Dispose(); Assert.True(stop(session)); session = 0;
        }
        finally { if (session != 0) stop(session); NativeLibrary.Free(module); }
    }
}
