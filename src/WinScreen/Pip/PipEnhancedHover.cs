using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Optional Messages extension. All calls are on the input worker, never the mouse hook.</summary>
internal sealed class PipEnhancedHover(WindowIdentity sourceIdentity) : IDisposable
{
    // A native thread has one activation slot. Refuse competing views rather than replacing their target.
    private static readonly HashSet<uint> Threads = [];
    private nint _module, _hook, _target;
    private nint _attempted;
    private uint _thread;
    private WindowIdentity? _identity;
    private long _renewed;
    private long _installedAt;
    private long _activationStarted;
    private nint _marker;
    private volatile bool _disabled;
    private bool _disarmed, _leaveSent;
    private RenewHook? _renew;
    private StopHook? _stop;
    private InspectHook? _inspect;
    internal long Filtered { get; private set; }
    internal string Status { get; private set; } = "inactive";
    internal double InstallMilliseconds { get; private set; }
    internal double UninstallMilliseconds { get; private set; }

    internal static bool CanAttempt(nint window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !File.Exists(Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll"))) return false;
        User32.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string product = FileVersionInfo.GetVersionInfo(process.MainModule!.FileName).ProductName ?? "";
            return process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) &&
                IsWow64Process2(process.Handle, out ushort machine, out _) && machine == 0 &&
                (product.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || product.Contains("Chromium", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { return false; }
    }

    internal void Update(nint target)
    {
        if (_disabled) { Dispose(); return; }
        if (!_disarmed && (target == _target || target == _attempted)) return;
        Dispose();
        if (_hook != 0) return;
        _attempted = target;
        if (target == 0) return;
        // Win10 cold-source renderer visibility was inconsistent despite successful filtering.
        // Keep the experimental rollout at the runtime-validated Windows 11 environment.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Status = "unavailable: requires Windows 11"; return; }
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64) { Status = "unavailable: requires native x64 Windows"; return; }
        uint thread = User32.GetWindowThreadProcessId(target, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            // Experimental rollout is limited to the validated browser. Never inject into arbitrary apps/games.
            if (!string.Equals(process.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
            { Status = "unavailable: application not validated"; return; }
            string product = FileVersionInfo.GetVersionInfo(process.MainModule!.FileName).ProductName ?? "";
            if (!product.Contains("Chrome", StringComparison.OrdinalIgnoreCase) && !product.Contains("Chromium", StringComparison.OrdinalIgnoreCase))
            { Status = "unavailable: application not validated"; return; }
            if (!IsWow64Process2(process.Handle, out ushort machine, out _) || machine != 0)
            { Status = "unavailable: target architecture"; return; }
            string path = Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll");
            if (!File.Exists(path)) { Status = "unavailable: helper missing"; return; }
            lock (Threads)
            {
                if (!Threads.Add(thread)) { Status = "unavailable: thread already in use"; return; }
                _thread = thread;
            }
            _identity = WindowIdentity.Capture(target);
            _activationStarted = Stopwatch.GetTimestamp();
            _module = NativeLibrary.Load(path);
            if (Marshal.GetDelegateForFunctionPointer<ReadVersion>(NativeLibrary.GetExport(_module, "ApiVersion"))() != 2)
                throw new EntryPointNotFoundException("Incompatible hover helper API.");
            var install = Marshal.GetDelegateForFunctionPointer<InstallHook>(NativeLibrary.GetExport(_module, "Start"));
            _renew = Marshal.GetDelegateForFunctionPointer<RenewHook>(NativeLibrary.GetExport(_module, "Renew"));
            _stop = Marshal.GetDelegateForFunctionPointer<StopHook>(NativeLibrary.GetExport(_module, "Stop"));
            _inspect = Marshal.GetDelegateForFunctionPointer<InspectHook>(NativeLibrary.GetExport(_module, "Inspect"));
            if (_disabled) { Dispose(); return; }
            sourceIdentity.Require();
            _marker = (nint)(BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) | 1);
            if (!User32.SetProp(sourceIdentity.Window, WindowController.EnhancedHoverOwnerProperty, _marker) ||
                !User32.SetProp(sourceIdentity.Window, WindowController.EnhancedHoverActiveProperty, _marker))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (_disabled) { Dispose(); return; }
            long start = Stopwatch.GetTimestamp();
            _hook = install(thread, target, _identity.Token, sourceIdentity.Window, _marker);
            InstallMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _target = target;
            _disarmed = _leaveSent = false;
            _installedAt = Environment.TickCount64;
            Renew();
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or
            DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or WindowOperationException or IOException or UnauthorizedAccessException)
        {
            Dispose();
            _attempted = target;
            Status = e is Win32Exception native ? $"failed: Win32 {native.NativeErrorCode}" : $"failed: {e.GetType().Name}";
        }
    }

    internal void Renew()
    {
        if (_hook == 0) return;
        if (_disabled || _disarmed) return;
        if (_identity?.StillThere() != true || IsHungAppWindow(_target)) { Dispose(); return; }
        if (Environment.TickCount64 - _renewed < 500) return;
        Filtered = _renew!(_hook);
        _renewed = Environment.TickCount64;
        long point = _inspect!(_hook, 1);
        if (_inspect(_hook, 2) == 0)
        {
            if (Environment.TickCount64 - _installedAt > 1500) { Dispose(); Status = "failed: target helper did not respond"; }
            else Status = "installed; awaiting target";
            return;
        }
        long samples = _inspect(_hook, 3), ticks = _inspect(_hook, 4), frequency = _inspect(_hook, 5), maximum = _inspect(_hook, 6);
        string timing = samples == 0 ? "unsampled" : $"mean={ticks * 1_000_000.0 / frequency / samples:F2}us max={maximum * 1_000_000.0 / frequency:F2}us n={samples}";
        long readyAt = _inspect(_hook, 7);
        double activation = readyAt == 0 ? 0 : Stopwatch.GetElapsedTime(_activationStarted, readyAt).TotalMilliseconds;
        Status = $"active; filtered={Filtered} moves={_inspect(_hook, 0)} point={(short)point},{(short)(point >> 16)} native({timing}) activation={activation:F3}ms";
    }

    /// <summary>UI teardown can disarm without racing the worker's native context/module lifetime.</summary>
    internal void Deactivate()
    {
        _disabled = true;
        ClearMarker(WindowController.EnhancedHoverActiveProperty);
        var identity = _identity;
        if (identity?.StillThere() == true) User32.PostMessage(identity.Window, 0x2A3, 0, 0);
    }

    private void ClearMarker(string property)
    {
        nint marker = Interlocked.CompareExchange(ref _marker, 0, 0);
        if (marker != 0 && sourceIdentity.StillThere() && User32.GetProp(sourceIdentity.Window, property) == marker)
            RemoveProp(sourceIdentity.Window, property);
    }

    public void Dispose()
    {
        ClearMarker(WindowController.EnhancedHoverActiveProperty);
        if (_hook != 0)
        {
            long start = Stopwatch.GetTimestamp();
            bool removed = _stop!(_hook);
            UninstallMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _disarmed = true;
            if (!_leaveSent && _identity?.StillThere() == true)
            {
                if (User32.SendMessageTimeout(_identity.Window, 0x2A3, 0, 0, 2, 30, out _) == 0)
                    User32.PostMessage(_identity.Window, 0x2A3, 0, 0);
                _leaveSent = true;
            }
            if (!removed) { Status = "failed: unhook"; return; } // Disarmed; retain context/module for a later retry.
            _hook = 0;
        }
        ClearMarker(WindowController.EnhancedHoverOwnerProperty);
        _marker = 0;
        if (_module != 0) { NativeLibrary.Free(_module); _module = 0; }
        if (_thread != 0) { lock (Threads) Threads.Remove(_thread); _thread = 0; }
        _identity = null;
        _target = 0;
        _attempted = 0;
        _renewed = 0;
        _disarmed = _leaveSent = false;
        Status = "inactive";
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate nint InstallHook(uint thread, nint target, long token, nint source, nint marker);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long RenewHook(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate bool StopHook(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long InspectHook(nint session, int point);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadVersion();
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(nint process, out ushort machine, out ushort nativeMachine);
}
