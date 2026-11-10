using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Optional Messages extension. After visit startup, the input worker owns the native session.</summary>
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

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "WinScreen.Hover.dll");

    /// <summary>True where the helper runs at all: Windows 11 on native x64, with the helper installed.</summary>
    internal static bool Supported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && RuntimeInformation.OSArchitecture == Architecture.X64 &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && File.Exists(HelperPath);

    /// <summary>The kind of program a window belongs to, and whether that program is a native 64-bit one.</summary>
    internal static (PipHoverApp App, bool Native) Describe(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            PipHoverApp app = PipHoverApps.Classify(process.ProcessName, WindowInfo.ClassName(User32.GetAncestor(window, User32.GA_ROOT)),
                HasElectronLayout(Path.GetDirectoryName(process.MainModule!.FileName) ?? ""), PipHoverApps.Preferences.Programs);
            return (app, IsWow64Process2(process.Handle, out ushort machine, out _) && machine == 0);
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (PipHoverApp.None, false);
        }
    }

    /// <summary>Electron keeps an app's own files in a "resources" folder, as "app" or "app.asar": beside the
    /// program, or one folder down when the program keeps each version in a folder of its own.</summary>
    internal static bool HasElectronLayout(string folder)
    {
        static bool Has(string place) =>
            File.Exists(Path.Combine(place, "resources", "app.asar")) || Directory.Exists(Path.Combine(place, "resources", "app"));
        if (folder.Length == 0 || !Directory.Exists(folder)) return false;
        return Has(folder) || Directory.EnumerateDirectories(folder).Take(64).Any(Has);
    }

    /// <summary>True when the helper may be used with this window's program. Never true for a program that is
    /// neither a known kind nor listed in Settings, so games and arbitrary apps are left alone.</summary>
    internal static bool CanAttempt(nint window) => Supported && Describe(window) is (not PipHoverApp.None, true);

    /// <summary>True when a new view of this window starts with Enhanced hover on, as chosen in Settings.</summary>
    internal static bool StartsOn(nint window)
    {
        if (window == 0 || !Supported) return false;
        var (app, native) = Describe(window);
        return native && PipHoverApps.StartsOn(app, PipHoverApps.Preferences);
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
        uint thread = User32.GetWindowThreadProcessId(target, out _);
        try
        {
            // Only the kinds of program chosen for this, or ones listed in Settings. Never arbitrary apps or games.
            var (app, native) = Describe(target);
            if (app == PipHoverApp.None) { Status = "unavailable: application not enabled"; return; }
            if (!native) { Status = "unavailable: target architecture"; return; }
            string path = HelperPath;
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
