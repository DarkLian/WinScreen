using WinScreen.Pip;

namespace WinScreen.Compatibility;

/// <summary>The Windows versions a test was seen passing on. None for a logic test, which needs no window.</summary>
[Flags]
internal enum WindowsVersions { None = 0, Windows10 = 1, Windows11 = 2, Both = Windows10 | Windows11 }

/// <summary>A test that showed a fact holds, as "Class.Method", and where it passed.</summary>
internal readonly record struct Verification(string Test, WindowsVersions On);

/// <summary>
/// What WinScreen knows about one kind of app. A row is recognised by every matcher it gives: program names (ignoring
/// case), the top-level window's class or the start of it (exactly), and Electron's files beside the program.
/// </summary>
internal sealed record AppProfile(string Name)
{
    public string[] Programs { get; init; } = [];
    public string? WindowClass { get; init; }
    public string? ClassPrefix { get; init; }
    public bool ElectronFiles { get; init; }

    /// <summary>The kind Enhanced hover knows it as, or None.</summary>
    public PipHoverApp Hover { get; init; }
    /// <summary>A program name in Settings' list is ignored for this app: it is never given the helper by name.</summary>
    public bool NeverByName { get; init; }
    /// <summary>Choose for me turns Enhanced hover on for it.</summary>
    public bool ChosenForMe { get; init; }
    /// <summary>Its hidden original keeps its strip at a side edge even with Enhanced hover off.</summary>
    public bool SideEdgeAlways { get; init; }
    /// <summary>With Enhanced hover on, its own middle-click scrolling works, so the view's option stands aside.</summary>
    public bool ScrollsWithHover { get; init; }
    /// <summary>Nothing is sent to it: a click on its view shows the original.</summary>
    public bool ShowsOriginalOnClick { get; init; }
    /// <summary>Windows of many apps, or of many kinds, share this program, so it is never offered for a list by itself.</summary>
    public bool SharedHost { get; init; }
    /// <summary>Windows owns its taskbar button, so hiding it leaves the button.</summary>
    public bool WindowsOwnsTaskbarButton { get; init; }
    /// <summary>
    /// Its views can be used directly with experimental features off (roadmap P1-3): clicks, double-clicks, drag
    /// selection, typing, the wheel and the context menu were seen working on Windows 10 and 11, and the original
    /// came back when WinScreen was killed during a visit (<see cref="RecoveredBy"/>). Enhanced hover and middle-click
    /// scrolling stay experimental for it.
    /// </summary>
    public bool InteractiveWithoutSwitch { get; init; }

    public Verification[] VerifiedBy { get; init; } = [];
    /// <summary>Tests that end WinScreen during a visit to this kind of app and see the original come back.</summary>
    public Verification[] RecoveredBy { get; init; } = [];

    internal bool MatchesProgram(string program)
    {
        foreach (string name in Programs)
            if (name.Equals(program, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal bool MatchesClass(string windowClass) =>
        (WindowClass is null || windowClass == WindowClass) &&
        (ClassPrefix is null || windowClass.StartsWith(ClassPrefix, StringComparison.Ordinal));

    internal bool Matches(string program, string windowClass, bool electronFiles) =>
        (Programs.Length == 0 || MatchesProgram(program)) && MatchesClass(windowClass) && (!ElectronFiles || electronFiles);
}

/// <summary>A kind of window inside an app that takes input another way than the app's own windows.</summary>
internal sealed record InputSurface(string WindowClass, string Name, InputRoute Route, Verification[] VerifiedBy);

/// <summary>
/// Everything WinScreen knows about kinds of app, in one place. The classes that act on it read from here; the table
/// in FEATURE_COMPATIBILITY.md is made from it, and a test fails when the two disagree.
/// </summary>
internal static class AppProfiles
{
    private const WindowsVersions Both = WindowsVersions.Both, Windows10 = WindowsVersions.Windows10,
        Windows11 = WindowsVersions.Windows11, Logic = WindowsVersions.None;

    /// <summary>In order: the first row that matches is the one that counts.</summary>
    internal static readonly AppProfile[] All =
    [
        new("Chrome and Edge")
        {
            // Both browsers run every test below in the Windows 10 VM. The Windows 11 PC has no Edge, so there these tests
            // ran with Chrome only; the two share their engine and window classes.
            Programs = ["chrome", "msedge"],
            Hover = PipHoverApp.Browser, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true,
            VerifiedBy =
            [
                new("BrowserInputTests.A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys", Both),
                new("BrowserInputTests.A_double_click_selects_a_word_in_a_page_used_through_a_view", Both),
                new("BrowserInputTests.Two_pages_in_two_views_take_drags_double_clicks_and_menus_each_on_its_own", Both),
                new("PipMessageInputTests.A_browser_scrolls_under_the_wheel_in_a_view_used_directly", Both),
                new("BrowserInputTests.A_browser_view_is_used_directly_without_experimental_features_and_never_gets_the_helper", Both),
                new("BrowserInputTests.Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable", Both),
                new("BrowserInputTests.A_browsers_own_middle_click_scrolling_works_with_enhanced_hover", Both),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.A_browser_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
                new("BrowserInputTests.A_hook_owner_crash_restores_a_parked_browser_and_clears_hover", Both),
            ],
        },
        new("Brave and Vivaldi")
        {
            // The same engine as Chrome and Edge, and treated the same, but no test has run either: Interactive stays
            // experimental for them until one does.
            Programs = ["brave", "vivaldi"],
            Hover = PipHoverApp.Browser, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            VerifiedBy = [new("AppProfileTests.Every_row_is_reached_by_its_own_example", Logic)],
        },
        new("Firefox")
        {
            Programs = ["firefox"],
            Hover = PipHoverApp.Firefox, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true, // Since 14 Nov 2026; every input test below runs with the switch off.
            VerifiedBy =
            [
                new("AppCompatibilityTests.Firefox_takes_clicks_and_a_double_click_through_a_view", Both),
                new("AppCompatibilityTests.Firefox_takes_a_drag_selection_through_a_view", Both),
                new("AppCompatibilityTests.Firefox_takes_typing_through_a_view", Both),
                new("AppCompatibilityTests.Firefox_scrolls_under_the_wheel_and_opens_its_menu_by_the_pointer_through_a_view", Both),
                new("FrameworkHoverTests.Firefox_keeps_css_hover_when_chosen_in_settings", Both),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.Firefox_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
            ],
        },
        new("File Explorer")
        {
            Programs = ["explorer"], WindowClass = "CabinetWClass",
            Hover = PipHoverApp.FileExplorer, // Not chosen for the person: its tab menus stop responding with the helper on.
            VerifiedBy =
            [
                new("ExplorerHoverTests.A_hovered_row_stays_highlighted_in_the_view_only_with_enhanced_hover", Both),
                new("ExplorerInputTests.Double_clicking_a_folder_in_a_view_navigates_explorer", Both),
                new("ExplorerInputTests.Explorer_tab_menus_remain_stationary_during_sustained_navigation", Windows11),
            ],
        },
        new("The Windows shell: desktop and taskbar")
        {
            Programs = ["explorer"], NeverByName = true, SharedHost = true, // Every folder window too.
            VerifiedBy = [new("PipHoverAppsTests.A_program_is_one_of_the_known_kinds_or_listed_or_left_alone", Logic)],
        },
        new("Electron apps, such as VS Code and Discord")
        {
            WindowClass = "Chrome_WidgetWin_1", ElectronFiles = true,
            Hover = PipHoverApp.Electron, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            VerifiedBy =
            [
                new("FrameworkHoverTests.An_electron_app_keeps_css_hover_when_chosen_in_settings", Both),
                new("FrameworkHoverTests.An_electron_app_known_only_by_its_folder_keeps_css_hover", Both),
                new("AppCompatibilityTests.An_electron_editor_takes_a_double_click_typing_and_its_context_menu_through_a_view", Windows11),
            ],
        },
        new("Java apps, such as IntelliJ IDEA")
        {
            ClassPrefix = "SunAwt", ShowsOriginalOnClick = true,
            VerifiedBy = [new("AppCompatibilityTests.IntelliJ_shows_its_original_on_a_click_on_its_view_and_works_there", Windows11)],
        },
        new("Store apps, such as Calculator and Settings")
        {
            Programs = ["ApplicationFrameHost"], WindowClass = "ApplicationFrameWindow",
            SharedHost = true, WindowsOwnsTaskbarButton = true,
            VerifiedBy =
            [
                new("StoreAppInputTests.Calculator_adds_through_a_view_takes_keys_and_says_what_it_cannot_do", Both),
                new("StoreAppInputTests.A_hidden_store_app_brought_forward_from_elsewhere_is_shown_and_its_taskbar_button_stays", Both),
            ],
        },
    ];

    /// <summary>Windows that take clicks through accessibility, whatever program they belong to.</summary>
    internal static readonly InputSurface[] Surfaces =
    [
        new("Microsoft.UI.Content.DesktopChildSiteBridge", "WinUI 3 content in a desktop window, such as File Explorer's command bar",
            InputRoute.Automation, [new("ExplorerInputTests.Explorer_tab_menus_remain_stationary_during_sustained_navigation", Windows11)]),
        new("Windows.UI.Core.CoreWindow", "Store app content", InputRoute.Automation,
            [new("StoreAppInputTests.Calculator_adds_through_a_view_takes_keys_and_says_what_it_cannot_do", Both)]),
        new("ApplicationFrameInputSinkWindow", "The Store app frame's input window", InputRoute.Automation,
            [new("StoreAppInputTests.The_wheel_scrolls_a_store_app_through_a_view", Both)]),
    ];

    private static readonly Dictionary<string, InputRoute> SurfaceRoutes = Surfaces.ToDictionary(s => s.WindowClass, s => s.Route, StringComparer.Ordinal);

    /// <summary>The first row that matches, or null for an app WinScreen knows nothing about.</summary>
    internal static AppProfile? Find(string program, string windowClass, bool electronFiles)
    {
        foreach (AppProfile profile in All)
            if (profile.Matches(program, windowClass, electronFiles)) return profile;
        return null;
    }

    /// <summary>The row Enhanced hover knows as this kind, or null.</summary>
    internal static AppProfile? OfKind(PipHoverApp kind)
    {
        if (kind == PipHoverApp.None) return null;
        foreach (AppProfile profile in All)
            if (profile.Hover == kind) return profile;
        return null;
    }

    /// <summary>The route a surface takes, when it is one of <see cref="Surfaces"/>.</summary>
    internal static InputRoute? SurfaceRoute(string windowClass) =>
        SurfaceRoutes.TryGetValue(windowClass, out InputRoute route) ? route : null;

    internal static bool ShowsOriginalOnClick(string windowClass)
    {
        foreach (AppProfile profile in All)
            if (profile.ShowsOriginalOnClick && profile.MatchesClass(windowClass)) return true;
        return false;
    }

    /// <summary>True when views of this app can be used directly without the experimental switch.</summary>
    internal static bool InteractiveWithoutSwitch(string program, string windowClass, bool electronFiles) =>
        Find(program, windowClass, electronFiles)?.InteractiveWithoutSwitch == true;

    internal static bool IsSharedHost(string program)
    {
        foreach (AppProfile profile in All)
            if (profile.SharedHost && profile.MatchesProgram(program)) return true;
        return false;
    }

    internal static bool WindowsOwnsTaskbarButton(string windowClass)
    {
        foreach (AppProfile profile in All)
            if (profile.WindowsOwnsTaskbarButton && profile.WindowClass is not null && profile.MatchesClass(windowClass)) return true;
        return false;
    }
}
