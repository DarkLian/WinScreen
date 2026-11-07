using WinScreen.Native;

namespace WinScreen.Hotkeys;

/// <summary>Keeps WinScreen to one copy per sign-in, and asks a running copy to close cleanly.</summary>
internal static class RunningInstance
{
    public const string MutexName = @"Local\WinScreen.SingleInstance";

    private const int CloseTimeoutMilliseconds = 8000;

    /// <summary>
    /// Asks a running copy to close so it can put windows back first, then waits for it. Killing it
    /// would skip that, so the installer calls this before an update or uninstall.
    /// </summary>
    public static void Close()
    {
        nint window = User32.FindWindow(null, HotkeyHost.WindowTitle);
        if (window == 0)
            return;

        User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
        DateTime end = DateTime.UtcNow.AddMilliseconds(CloseTimeoutMilliseconds);
        while (DateTime.UtcNow < end && User32.IsWindow(window))
            Thread.Sleep(50);
    }
}
