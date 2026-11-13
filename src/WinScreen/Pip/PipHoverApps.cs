using WinScreen.Settings;

namespace WinScreen.Pip;

/// <summary>The kinds of program Enhanced hover knows. Anything else gets the helper only when it is listed by name.</summary>
internal enum PipHoverApp { None, Browser, Firefox, Electron, FileExplorer, Listed }

/// <summary>Enhanced hover's view of the app profiles (<see cref="Compatibility.AppProfiles"/>), with Settings' own list.</summary>
internal static class PipHoverApps
{
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
        // A known kind wins over its name in the list. The shell process also owns the desktop and the taskbar, and
        // only its folder windows count: its other windows are never given the helper, listed or not.
        if (Compatibility.AppProfiles.Find(program, rootClass, electronLayout) is { } profile &&
            (profile.Hover != PipHoverApp.None || profile.NeverByName))
            return profile.Hover;
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
    internal static bool Recommended(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.ChosenForMe == true;

    /// <summary>
    /// True when a hidden original of this kind keeps its strip at a side edge whatever Enhanced hover is set to,
    /// because under the taskbar it would stop drawing and taking pointer moves. Other kinds go there only while
    /// Enhanced hover is on for them.
    /// </summary>
    internal static bool SideEdgeAlways(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.SideEdgeAlways == true;

    /// <summary>True when, with Enhanced hover, the app's own middle-click scrolling works through a view.</summary>
    internal static bool ScrollsWithHover(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.ScrollsWithHover == true;
}
