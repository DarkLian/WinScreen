using static WinScreen.Localization.UiText;
using System.Diagnostics;
using System.Security.Cryptography;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>A per-window native property disappears when HWND is destroyed, even if its PID/thread reuse the handle.</summary>
internal sealed record WindowIdentity(nint Window, uint ProcessId, uint ThreadId, long ProcessStartTicks, long Token)
{
    internal const string PropertyName = "WinScreen.WindowIdentity.v1";

    /// <summary>
    /// Notes which window this is without touching it. Enough to follow the window in use; anything that
    /// is about to change a window uses <see cref="Capture"/>, so windows WinScreen never acts on stay untouched.
    /// </summary>
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

    /// <summary>True while the window and its process are the ones noted by <see cref="Observe"/>.</summary>
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

    internal void Require()
    {
        if (!Matches())
            throw new WindowOperationException(T("errors.identity.originalGone"));
    }
}
