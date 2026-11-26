using WinScreen.Hotkeys;
using WinScreen.Native;

namespace WinScreen.Tray;

// Started from a shortcut (the taskbar, Start, the desktop), WinScreen opens Settings: once it has started, or in the
// copy already running. Started at sign-in, after an update or by the installer it stays in the tray.
internal static class Launch
{
    // A message of its own, so a copy running as administrator can let just this one in from a copy that isn't.
    internal const string OpenSettingsMessage = "WinScreen.OpenSettings";

    internal static int OpenSettings { get; } = StartedBy.RegisterWindowMessage(OpenSettingsMessage);

    internal static bool FromShortcut() =>
        ByHand(StartedBy.Shortcut(), Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

    // A shortcut in a Startup folder is Windows starting WinScreen at sign-in.
    internal static bool ByHand(string? link, params string[] startupFolders) =>
        !string.IsNullOrEmpty(link) && !startupFolders.Any(folder => !string.IsNullOrEmpty(folder) &&
            string.Equals(Path.GetDirectoryName(link)?.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    internal static void LetIn(nint hostWindow)
    {
        if (OpenSettings != 0) StartedBy.LetIn(hostWindow, OpenSettings);
    }

    // The running copy opens its Settings; false when there is none to ask, such as one in another folder.
    internal static bool AskRunningCopy()
    {
        nint window = User32.FindWindow(null, HotkeyHost.WindowTitle);
        if (window == 0 || OpenSettings == 0) return false;
        User32.GetWindowThreadProcessId(window, out uint running);
        // Its path is all a copy without administrator rights may read of one running with them.
        if (!string.Equals(ProcessInfo.PathOf(running), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
        User32.AllowSetForegroundWindow(running);
        return User32.PostMessage(window, OpenSettings, 0, 0);
    }
}
