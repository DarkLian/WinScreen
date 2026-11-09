using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Tests.Support;

/// <summary>Explicit test-only Stage A helper. Never loaded by the shipped app.</summary>
internal sealed class NativeHoverProbe : IDisposable
{
    private readonly nint _module, _hook, _target;
    private readonly uint _thread;
    private static readonly uint Control = RegisterWindowMessage("WinScreen.HoverProbe.Activation.v1");
    internal double InstallMilliseconds { get; }
    internal double UninstallMilliseconds { get; private set; }

    internal NativeHoverProbe(nint target)
    {
        _target = target;
        _thread = User32.GetWindowThreadProcessId(target, out _);
        string path = Environment.GetEnvironmentVariable("WINSCREEN_HOVER_PROBE_DLL")
            ?? throw new InvalidOperationException("Set WINSCREEN_HOVER_PROBE_DLL to the x64 Stage A DLL.");
        _module = NativeLibrary.Load(Path.GetFullPath(path));
        var install = Marshal.GetDelegateForFunctionPointer<InstallHook>(NativeLibrary.GetExport(_module, "Install"));
        var clock = Stopwatch.StartNew();
        _hook = install(_thread);
        InstallMilliseconds = clock.Elapsed.TotalMilliseconds;
        if (_hook == 0) { int error = Marshal.GetLastWin32Error(); NativeLibrary.Free(_module); throw new Win32Exception(error); }
        Renew();
    }

    internal void Renew()
    {
        if (!PostThreadMessage(_thread, Control, _target, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        var clock = Stopwatch.StartNew();
        bool removed = UnhookWindowsHookEx(_hook);
        UninstallMilliseconds = clock.Elapsed.TotalMilliseconds;
        // Exact tested target only; after removal there is no filter to swallow this leave.
        User32.PostMessage(_target, 0x2A3, 0, 0);
        NativeLibrary.Free(_module);
        if (!removed) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate nint InstallHook(uint thread);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
}
