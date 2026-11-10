using WinScreen.Settings;

namespace WinScreen.Pip;

/// <summary>The kinds of program Enhanced hover knows. Anything else gets the helper only when it is listed by name.</summary>
internal enum PipHoverApp { None, Browser, Electron, FileExplorer, Listed }

internal static class PipHoverApps
{
    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "vivaldi"];
    private static volatile PipHoverPreferences _preferences = new();

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
        // The shell process also owns the desktop and the taskbar; only its folder windows count.
        if (program.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            return rootClass == "CabinetWClass" ? PipHoverApp.FileExplorer : PipHoverApp.None;
        if (electronLayout && rootClass == "Chrome_WidgetWin_1") return PipHoverApp.Electron;
        return listed.Contains(program, StringComparer.OrdinalIgnoreCase) ? PipHoverApp.Listed : PipHoverApp.None;
    }

    /// <summary>True when a new view of this kind of program starts with Enhanced hover on.</summary>
    internal static bool StartsOn(PipHoverApp app, PipHoverPreferences preferences) => app switch
    {
        PipHoverApp.Browser => preferences.Browsers,
        PipHoverApp.Electron => preferences.Electron,
        PipHoverApp.FileExplorer => preferences.FileExplorer,
        PipHoverApp.Listed => true,
        _ => false,
    };
}
