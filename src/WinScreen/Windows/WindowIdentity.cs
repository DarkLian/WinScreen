using static WinScreen.Localization.UiText;
using System.Diagnostics;
using System.Security.Cryptography;
using WinScreen.Native;

namespace WinScreen.Windows;

// A per-window native property disappears when the window is destroyed, even if its process and thread ids are reused.
internal sealed record WindowIdentity(nint Window, uint ProcessId, uint ThreadId, long ProcessStartTicks, long Token)
{
    internal const string PropertyName = "WinScreen.WindowIdentity.v1";

    // Without touching the window: anything about to change a window uses Capture, so windows WinScreen never acts on
    // stay untouched.
    internal static WindowIdentity Observe(nint window)
    {
        uint thread = User32.GetWindowThreadProcessId(window, out uint pid);
        if (thread == 0 || !User32.IsWindow(window))
            throw new WindowOperationException(T("errors.identity.windowGone"));
        return new WindowIdentity(window, pid, thread, StartTicks(pid), 0);
    }

    internal static WindowIdentity Capture(nint window)
    {
        WindowIdentity observed = Observe(window);
        long token = User32.GetProp(window, PropertyName);
        if (token == 0)
        {
            token = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) | 1;
            if (!User32.SetProp(window, PropertyName, (nint)token))
                throw new WindowOperationException(
                    T("errors.identity.accessDenied"));
        }

        var identity = observed with { Token = token };
        if (!identity.Matches())
            throw new WindowOperationException(T("errors.identity.windowChanged"));
        return identity;
    }

    private static long StartTicks(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            throw new WindowOperationException(T("errors.identity.processUnverifiable"));
        }
    }

    internal bool IsSameWindow()
    {
        uint thread = User32.GetWindowThreadProcessId(Window, out uint pid);
        if (pid != ProcessId || thread != ThreadId || !User32.IsWindow(Window))
            return false;
        try
        {
            return StartTicks(pid) == ProcessStartTicks;
        }
        catch (WindowOperationException)
        {
            return false;
        }
    }

    internal bool Matches()
    {
        uint thread = User32.GetWindowThreadProcessId(Window, out uint pid);
        if (Token == 0 || pid != ProcessId || (ThreadId != 0 && thread != ThreadId) || !User32.IsWindow(Window) ||
            (long)User32.GetProp(Window, PropertyName) != Token)
            return false;
        try
        {
            return StartTicks(pid) == ProcessStartTicks;
        }
        catch (WindowOperationException)
        {
            return false;
        }
    }

    // For a mouse hook, which Windows drops if it answers slowly: the marker tells a reused handle apart without
    // opening the process.
    internal bool StillThere()
    {
        uint thread = User32.GetWindowThreadProcessId(Window, out uint pid);
        return Token != 0 && pid == ProcessId && (ThreadId == 0 || thread == ThreadId) && User32.IsWindow(Window) &&
               (long)User32.GetProp(Window, PropertyName) == Token;
    }

    internal void Require()
    {
        if (!Matches())
            throw new WindowOperationException(T("errors.identity.originalGone"));
    }
}
