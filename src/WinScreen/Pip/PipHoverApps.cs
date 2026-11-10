using WinScreen.Settings;

namespace WinScreen.Pip;

/// <summary>The kinds of program Enhanced hover knows. Anything else gets the helper only when it is listed by name.</summary>
internal enum PipHoverApp { None, Browser, Firefox, Electron, FileExplorer, Listed }

internal static class PipHoverApps
{
    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "vivaldi"];
    // Nothing is on until Settings has been read.
    private static volatile PipHoverPreferences _preferences = new() { ChooseForMe = false };

    /// <summary>What Settings says now. Read by views and by the input worker.</summary>
    internal static PipHoverPreferences Preferences
    {
        get => _preferences;
        set => _preferences = value.Validated();
    }

    /// <param name="program">The process name, without ".exe".</param>
    /// <param name="rootClass">The class of the top-level window the pointer is in.</param>
    /// <param name="electronLayout">The program's folder has Electron's "resources\app" beside it.</param>
    internal static PipHoverApp Classify(string program, string rootClass, bool electronLayout, IEnumerable<string> listed)
    {
        if (Browsers.Contains(program, StringComparer.OrdinalIgnoreCase)) return PipHoverApp.Browser;
        if (program.Equals("firefox", StringComparison.OrdinalIgnoreCase)) return PipHoverApp.Firefox;
        // The shell process also owns the desktop and the taskbar; only its folder windows count.
        if (program.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            return rootClass == "CabinetWClass" ? PipHoverApp.FileExplorer : PipHoverApp.None;
        if (electronLayout && rootClass == "Chrome_WidgetWin_1") return PipHoverApp.Electron;
        return listed.Contains(program, StringComparer.OrdinalIgnoreCase) ? PipHoverApp.Listed : PipHoverApp.None;
    }

    /// <summary>True when a new view of this kind of program starts with Enhanced hover on.</summary>
    internal static bool StartsOn(PipHoverApp app, PipHoverPreferences preferences) => app switch
    {
        PipHoverApp.Listed => true,
        _ when preferences.ChooseForMe => Recommended(app),
        PipHoverApp.Browser => preferences.Browsers,
        PipHoverApp.Firefox => preferences.Firefox,
        PipHoverApp.Electron => preferences.Electron,
        PipHoverApp.FileExplorer => preferences.FileExplorer,
        _ => false,
    };

    /// <summary>
    /// What "choose for me" turns on: the kinds where hover was seen to hold and draw. File Explorer is left to the
    /// person, because its tab menus stop responding with the helper on.
    /// </summary>
    internal static bool Recommended(PipHoverApp app) => app is PipHoverApp.Browser or PipHoverApp.Firefox or PipHoverApp.Electron;
}
