using WinScreen.Localization;
using WinScreen.Native;

namespace WinScreen.Hotkeys;

internal static class RunningInstance
{
    public const string MutexName = @"Local\WinScreen.SingleInstance";

    private const int CloseTimeoutMilliseconds = 8000;

    // The installer calls this before an update or uninstall: killing the copy would skip putting windows back.
    public static void Close()
    {
        nint window = User32.FindWindow(null, HotkeyHost.WindowTitle);
        if (window == 0)
        {
            WaitForHelper();
            return;
        }
        string targetPath = Windows.WindowInfo.ExecutablePath(window);
        if (!string.Equals(targetPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                UiText.T(
                    "errors.app.otherFolder"));
        var identity = Windows.WindowIdentity.Capture(window);
        using var process = System.Diagnostics.Process.GetProcessById((int)identity.ProcessId);
        identity.Require();
        if (!User32.PostMessage(window, User32.WM_CLOSE, 0, 0))
            throw new InvalidOperationException(UiText.T("errors.app.shutdownRefused"));
        DateTime end = DateTime.UtcNow.AddMilliseconds(CloseTimeoutMilliseconds);
        while (DateTime.UtcNow < end && !process.HasExited)
            Thread.Sleep(50);
        if (!process.HasExited)
            throw new TimeoutException(
                UiText.T(
                    "errors.app.shutdownUnconfirmed"));
        WaitForHelper();
    }

    // An update must wait for the elevated helper too, or it still holds the files. Nothing can be sent to its window
    // from here (it is elevated), so this only waits.
    private static void WaitForHelper()
    {
        nint helper = User32.FindWindow(null, Tray.AppRoles.HelperHostTitle);
        if (helper == 0) return;
        User32.GetWindowThreadProcessId(helper, out uint id);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)id);
            if (!string.Equals(Native.ProcessInfo.PathOf(id), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!process.WaitForExit(CloseTimeoutMilliseconds))
                throw new TimeoutException(UiText.T("errors.app.shutdownUnconfirmed"));
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Log.Swallowed(gone);
        }
    }
}
