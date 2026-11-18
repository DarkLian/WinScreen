using WinScreen.Settings;

namespace WinScreen.Pip;

internal enum PipHoverApp { None, Browser, Firefox, Electron, FileExplorer, Listed }

internal static class PipHoverApps
{
    // Nothing is on until Settings has been read.
    private static volatile PipHoverPreferences _preferences = new() { ChooseForMe = false };

    internal static PipHoverPreferences Preferences
    {
        get => _preferences;
        set => _preferences = value.Validated();
    }

    internal static PipHoverApp Classify(string program, string rootClass, bool electronLayout, IEnumerable<string> listed)
    {
        // A known kind wins over its name in the list. The shell process also owns the desktop and the taskbar, and
        // only its folder windows count: its other windows are never given the helper, listed or not.
        if (Compatibility.AppProfiles.Find(program, rootClass, electronLayout) is { } profile &&
            (profile.Hover != PipHoverApp.None || profile.NeverByName))
            return profile.Hover;
        return listed.Contains(program, StringComparer.OrdinalIgnoreCase) ? PipHoverApp.Listed : PipHoverApp.None;
    }

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

    internal static bool Recommended(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.ChosenForMe == true;

    // Under the taskbar such an original would stop drawing and taking pointer moves.
    internal static bool SideEdgeAlways(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.SideEdgeAlways == true;

    internal static bool ScrollsWithHover(PipHoverApp app) => Compatibility.AppProfiles.OfKind(app)?.ScrollsWithHover == true;
}
