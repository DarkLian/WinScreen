using WinScreen.Pip;

namespace WinScreen.Compatibility;

[Flags]
internal enum WindowsVersions { None = 0, Windows10 = 1, Windows11 = 2, Both = Windows10 | Windows11 }

internal readonly record struct Verification(string Test, WindowsVersions On);

internal sealed record AppProfile(string Name)
{
    public string[] Programs { get; init; } = [];
    public string? WindowClass { get; init; }
    public string? ClassPrefix { get; init; }
    public bool ElectronFiles { get; init; }

    public PipHoverApp Hover { get; init; }
    public bool NeverByName { get; init; }
    public bool ChosenForMe { get; init; }
    public bool SideEdgeAlways { get; init; }
    public bool ScrollsWithHover { get; init; }
    public bool ShowsOriginalOnClick { get; init; }
    public bool SharedHost { get; init; }
    public bool WindowsOwnsTaskbarButton { get; init; }
    public bool InteractiveWithoutSwitch { get; init; }

    public Verification[] VerifiedBy { get; init; } = [];
    public DragHandOver Drags { get; init; }

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

internal enum DragHandOver
{
    None,
    Items,
    // Known by the hand pointer: browsers don't expose page contents to accessibility unless a screen reader turned
    // that on.
    Links,
}

internal sealed record InputSurface(string WindowClass, string Name, InputRoute Route, Verification[] VerifiedBy);

// FEATURE_COMPATIBILITY.md's table is made from this; a test fails when the two disagree.
internal static class AppProfiles
{
    private const WindowsVersions Both = WindowsVersions.Both, Windows10 = WindowsVersions.Windows10,
        Windows11 = WindowsVersions.Windows11, Logic = WindowsVersions.None;

    // The first row that matches is the one that counts.
    internal static readonly AppProfile[] All =
    [
        new("Chrome and Edge")
        {
            Programs = ["chrome", "msedge"],
            Hover = PipHoverApp.Browser, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true, Drags = DragHandOver.Links,
            VerifiedBy =
            [
                new("BrowserInputTests.A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys", Both),
                new("BrowserInputTests.A_double_click_selects_a_word_in_a_page_used_through_a_view", Both),
                new("BrowserInputTests.Two_pages_in_two_views_take_drags_double_clicks_and_menus_each_on_its_own", Both),
                new("PipMessageInputTests.A_browser_scrolls_under_the_wheel_in_a_view_used_directly", Both),
                new("BrowserInputTests.A_browser_view_is_used_directly_without_experimental_features_and_never_gets_the_helper", Both),
                new("BrowserInputTests.Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable", Both),
                new("BrowserInputTests.A_browsers_own_middle_click_scrolling_works_with_enhanced_hover", Both),
                new("BrowserInputTests.A_link_is_dragged_out_of_a_view_into_another_window", Both),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.A_browser_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
                new("BrowserInputTests.A_hook_owner_crash_restores_a_parked_browser_and_clears_hover", Both),
            ],
        },
        new("Brave and Vivaldi")
        {
            Programs = ["brave", "vivaldi"],
            Hover = PipHoverApp.Browser, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true, Drags = DragHandOver.Links,
            VerifiedBy =
            [
                new("BrowserInputTests.A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys", Both),
                new("BrowserInputTests.A_double_click_selects_a_word_in_a_page_used_through_a_view", Both),
                new("BrowserInputTests.Two_pages_in_two_views_take_drags_double_clicks_and_menus_each_on_its_own", Both),
                new("PipMessageInputTests.A_browser_scrolls_under_the_wheel_in_a_view_used_directly", Both),
                new("BrowserInputTests.A_browser_view_is_used_directly_without_experimental_features_and_never_gets_the_helper", Both),
                new("BrowserInputTests.Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable", Both),
                new("BrowserInputTests.A_link_is_dragged_out_of_a_view_into_another_window", Both),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.A_browser_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
            ],
        },
        new("Firefox")
        {
            Programs = ["firefox"],
            Hover = PipHoverApp.Firefox, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true,
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
            Hover = PipHoverApp.FileExplorer,  // Not ChosenForMe: its tab menus stop responding with the helper on.
            InteractiveWithoutSwitch = true,
            Drags = DragHandOver.Items,
            VerifiedBy =
            [
                new("ExplorerHoverTests.A_hovered_row_stays_highlighted_in_the_view_only_with_enhanced_hover", Both),
                new("ExplorerInputTests.Double_clicking_a_folder_in_a_view_navigates_explorer", Both),
                new("ExplorerInputTests.A_submenu_opens_beside_its_menu_and_takes_the_click_through_a_view", Both),
                new("ExplorerInputTests.A_file_is_renamed_by_typing_through_a_view", Both),
                new("ExplorerInputTests.A_drag_selects_files_through_a_view", Both),
                new("ExplorerInputTests.The_wheel_scrolls_the_file_list_through_a_view", Both),
                new("ExplorerInputTests.A_file_is_dragged_out_of_a_view", Both),
                new("ExplorerInputTests.Explorer_tab_menus_remain_stationary_during_sustained_navigation", Windows11),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.File_explorer_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
            ],
        },
        new("The Windows shell: desktop and taskbar")
        {
            Programs = ["explorer"], NeverByName = true, SharedHost = true,  // Every folder window too.
            VerifiedBy = [new("PipHoverAppsTests.A_program_is_one_of_the_known_kinds_or_listed_or_left_alone", Logic)],
        },
        new("Electron apps, such as VS Code and Discord")
        {
            WindowClass = "Chrome_WidgetWin_1", ElectronFiles = true,
            Hover = PipHoverApp.Electron, ChosenForMe = true, SideEdgeAlways = true, ScrollsWithHover = true,
            InteractiveWithoutSwitch = true,
            VerifiedBy =
            [
                new("FrameworkHoverTests.An_electron_app_keeps_css_hover_when_chosen_in_settings", Both),
                new("FrameworkHoverTests.An_electron_app_known_only_by_its_folder_keeps_css_hover", Both),
                new("AppCompatibilityTests.An_electron_app_takes_clicks_and_a_double_click_through_a_view", Both),
                new("AppCompatibilityTests.An_electron_app_takes_a_drag_selection_through_a_view", Both),
                new("AppCompatibilityTests.An_electron_app_takes_typing_through_a_view", Both),
                new("AppCompatibilityTests.An_electron_app_scrolls_under_the_wheel_and_opens_its_menu_by_the_pointer_through_a_view", Both),
                new("AppCompatibilityTests.An_electron_editor_takes_a_double_click_typing_and_its_context_menu_through_a_view", Windows11),
            ],
            RecoveredBy =
            [
                new("AppEndToEndTests.An_electron_app_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit", Both),
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
                new("StoreAppInputTests.A_hidden_store_app_picked_from_the_taskbar_or_switcher_is_shown_and_its_taskbar_button_stays", Both),
            ],
        },
    ];

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

    internal static AppProfile? Find(string program, string windowClass, bool electronFiles)
    {
        foreach (AppProfile profile in All)
            if (profile.Matches(program, windowClass, electronFiles)) return profile;
        return null;
    }

    internal static AppProfile? OfKind(PipHoverApp kind)
    {
        if (kind == PipHoverApp.None) return null;
        foreach (AppProfile profile in All)
            if (profile.Hover == kind) return profile;
        return null;
    }

    internal static InputRoute? SurfaceRoute(string windowClass) =>
        SurfaceRoutes.TryGetValue(windowClass, out InputRoute route) ? route : null;

    internal static bool ShowsOriginalOnClick(string windowClass)
    {
        foreach (AppProfile profile in All)
            if (profile.ShowsOriginalOnClick && profile.MatchesClass(windowClass)) return true;
        return false;
    }

    internal static DragHandOver DragsOf(string program, string windowClass, bool electronFiles) =>
        Find(program, windowClass, electronFiles)?.Drags ?? DragHandOver.None;

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
