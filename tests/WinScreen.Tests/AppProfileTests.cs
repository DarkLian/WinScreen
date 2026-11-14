using WinScreen.Compatibility;
using WinScreen.Pip;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// How each kind of app is treated, pinned as it was before the app profile table, so moving the facts into one
/// table can't change how any app is treated.
/// </summary>
public sealed class AppProfileTests
{
    [Theory]
    // Matched by process name, whatever the window.
    [InlineData("chrome", "AnyClass", false, PipHoverApp.Browser)]
    [InlineData("CHROME", "Chrome_WidgetWin_1", false, PipHoverApp.Browser)]
    [InlineData("firefox", "AnyClass", true, PipHoverApp.Firefox)]
    // A browser beside Electron-looking files is still a browser.
    [InlineData("chrome", "Chrome_WidgetWin_1", true, PipHoverApp.Browser)]
    [InlineData("msedge", "Chrome_WidgetWin_1", true, PipHoverApp.Browser)]
    // Electron needs both its files and its window class, matched exactly.
    [InlineData("Code", "chrome_widgetwin_1", true, PipHoverApp.None)]
    [InlineData("Code", "Chrome_WidgetWin_0", true, PipHoverApp.None)]
    // The shell's other windows are never File Explorer, nor listed.
    [InlineData("explorer", "cabinetwclass", false, PipHoverApp.None)]
    [InlineData("Explorer", "CabinetWClass", false, PipHoverApp.FileExplorer)]
    [InlineData("explorer", "WorkerW", false, PipHoverApp.None)]
    public void Each_kind_of_app_is_recognised_as_before(string program, string rootClass, bool electron, object expected) =>
        Assert.Equal((PipHoverApp)expected, PipHoverApps.Classify(program, rootClass, electron, ["explorer", "EXPLORER"]));

    [Theory]
    [InlineData("explorer", "Progman", PipHoverApp.None)]       // Listed by name, but the shell is never listed.
    [InlineData("Explorer", "CabinetWClass", PipHoverApp.FileExplorer)]
    [InlineData("chrome", "AnyClass", PipHoverApp.Browser)]     // A known kind wins over its own name in the list.
    [InlineData("LISTED", "AnyClass", PipHoverApp.Listed)]      // Names in the list match ignoring case.
    public void A_listed_name_never_overrides_a_known_kind(string program, string rootClass, object expected) =>
        Assert.Equal((PipHoverApp)expected, PipHoverApps.Classify(program, rootClass, false, ["explorer", "chrome", "listed"]));

    [Theory]
    [InlineData(PipHoverApp.Browser, true)]
    [InlineData(PipHoverApp.Firefox, true)]
    [InlineData(PipHoverApp.Electron, true)]
    [InlineData(PipHoverApp.FileExplorer, false)]
    [InlineData(PipHoverApp.Listed, false)]
    [InlineData(PipHoverApp.None, false)]
    public void Choose_for_me_picks_the_kinds_it_was_verified_with(object app, bool recommended) =>
        Assert.Equal(recommended, PipHoverApps.Recommended((PipHoverApp)app));

    [Theory]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", false, true, InputRoute.Automation)] // Whatever the process.
    [InlineData("Windows.UI.Core.CoreWindow", true, true, InputRoute.Automation)]
    [InlineData("ApplicationFrameInputSinkWindow", false, true, InputRoute.Automation)]
    [InlineData("windows.ui.core.corewindow", true, true, InputRoute.Messages)] // Classes match exactly.
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", true, false, InputRoute.Refused)]
    [InlineData("ApplicationFrameWindow", true, true, InputRoute.Messages)]
    [InlineData("", false, false, InputRoute.Refused)]
    public void Each_surface_gets_the_route_it_had(string className, bool sameProcess, bool inside, InputRoute route) =>
        Assert.Equal(route, PipInputRoutes.RouteFor(className, sameProcess, inside));

    [Theory]
    [InlineData("SunAwtFrame", true)]
    [InlineData("SunAwtCanvas", true)]
    [InlineData("SunAwt", true)]
    [InlineData("xSunAwtFrame", false)]
    [InlineData("SUNAWTFRAME", false)]
    public void Java_is_known_by_its_window_class_prefix(string windowClass, bool java) =>
        Assert.Equal(java, PipOriginalApps.IsJavaClass(windowClass));

    [Theory]
    // Before the table: browsers, Firefox and Electron at the side edge always; others only with Enhanced hover on.
    [InlineData(PipHoverApp.Browser, true)]
    [InlineData(PipHoverApp.Firefox, true)]
    [InlineData(PipHoverApp.Electron, true)]
    [InlineData(PipHoverApp.FileExplorer, false)]
    [InlineData(PipHoverApp.Listed, false)]
    [InlineData(PipHoverApp.None, false)]
    public void The_side_edge_and_the_apps_own_scrolling_are_for_the_kinds_they_were_before(object app, bool kinds)
    {
        Assert.Equal(kinds, PipHoverApps.SideEdgeAlways((PipHoverApp)app));
        Assert.Equal(kinds, PipHoverApps.ScrollsWithHover((PipHoverApp)app));
    }

    [Theory]
    [InlineData("ApplicationFrameHost", true)]
    [InlineData("applicationframehost", true)]
    [InlineData("explorer", true)]
    [InlineData("Calculator", false)]
    [InlineData("", false)]
    public void Store_apps_and_the_shell_are_programs_many_windows_share(string program, bool shared) =>
        Assert.Equal(shared, AppProfiles.IsSharedHost(program));

    [Theory]
    [InlineData("ApplicationFrameWindow", true)]
    [InlineData("applicationframewindow", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]
    public void Windows_keeps_the_taskbar_button_of_a_store_app_frame(string windowClass, bool owned) =>
        Assert.Equal(owned, AppProfiles.WindowsOwnsTaskbarButton(windowClass));

    /// <summary>One app of each row, as Windows reports it. Each must land on its own row, not an earlier one.</summary>
    public static TheoryData<string, string, string, bool> Examples => new()
    {
        { "Chrome and Edge", "chrome", "Chrome_WidgetWin_1", false },
        { "Brave and Vivaldi", "brave", "Chrome_WidgetWin_1", false },
        { "Firefox", "firefox", "MozillaWindowClass", false },
        { "File Explorer", "explorer", "CabinetWClass", false },
        { "The Windows shell: desktop and taskbar", "explorer", "Shell_TrayWnd", false },
        { "Electron apps, such as VS Code and Discord", "Code", "Chrome_WidgetWin_1", true },
        { "Java apps, such as IntelliJ IDEA", "idea64", "SunAwtFrame", false },
        { "Store apps, such as Calculator and Settings", "ApplicationFrameHost", "ApplicationFrameWindow", false },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void Every_row_is_reached_by_its_own_example(string row, string program, string windowClass, bool electron) =>
        Assert.Equal(row, AppProfiles.Find(program, windowClass, electron)?.Name);

    [Fact]
    public void Every_row_has_an_example_and_a_name_of_its_own()
    {
        // A row with nothing to match would take every app that reaches it.
        Assert.All(AppProfiles.All, profile => Assert.True(profile.Programs.Length > 0 || profile.WindowClass is not null ||
            profile.ClassPrefix is not null, profile.Name + " matches everything."));
        string[] names = AppProfiles.All.Select(profile => profile.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(names.Order(), Examples.Select(example => (string)example[0]).Order());
        Assert.Equal(AppProfiles.Surfaces.Length, AppProfiles.Surfaces.Select(surface => surface.WindowClass).Distinct().Count());
    }

    [Fact]
    public void Every_row_names_tests_that_exist_and_run_on_the_windows_it_says()
    {
        var problems = new List<string>();
        IEnumerable<(string Row, Verification[] By)> rows = AppProfiles.All.Select(profile => (profile.Name, profile.VerifiedBy))
            .Concat(AppProfiles.All.Where(profile => profile.RecoveredBy.Length > 0).Select(profile => (profile.Name, profile.RecoveredBy)))
            .Concat(AppProfiles.Surfaces.Select(surface => (surface.Name, surface.VerifiedBy)));
        foreach ((string row, Verification[] by) in rows)
        {
            if (by.Length == 0) problems.Add($"{row}: no test");
            foreach (Verification verification in by)
            {
                string[] parts = verification.Test.Split('.');
                Type? type = parts.Length == 2 ? typeof(AppProfileTests).Assembly.GetType("WinScreen.Tests." + parts[0]) : null;
                System.Reflection.MethodInfo? method = type?.GetMethod(parts[1]);
                bool test = method is not null && (method.IsDefined(typeof(FactAttribute), true) || method.IsDefined(typeof(TheoryAttribute), true));
                if (!test) problems.Add($"{row}: {verification.Test} isn't a test");
                // A test that needs no window says nothing about a Windows version, and one that does must say which.
                bool windows = type is not null && type.GetCustomAttributes(typeof(TraitAttribute), true).Length > 0;
                if (test && windows == (verification.On == WindowsVersions.None))
                    problems.Add($"{row}: {verification.Test} says {verification.On}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("chrome", "Chrome_WidgetWin_1", false, true)]
    [InlineData("msedge", "Chrome_WidgetWin_1", false, true)]
    [InlineData("brave", "Chrome_WidgetWin_1", false, true)]
    [InlineData("vivaldi", "Chrome_WidgetWin_1", false, true)]
    [InlineData("firefox", "MozillaWindowClass", false, true)]
    [InlineData("Code", "Chrome_WidgetWin_1", true, true)]
    [InlineData("explorer", "CabinetWClass", false, true)]
    [InlineData("ApplicationFrameHost", "ApplicationFrameWindow", false, false)]
    [InlineData("WinScreen.TestTarget", "WindowsForms10.Window.8.app.0.1", false, false)]
    public void Only_kinds_with_their_evidence_are_used_through_a_view_without_experimental_features(string program, string windowClass,
        bool electron, bool without) =>
        Assert.Equal(without, AppProfiles.InteractiveWithoutSwitch(program, windowClass, electron));

    /// <summary>
    /// A kind of app leaves the experimental switch only with its evidence (roadmap P1-3): input seen working on
    /// Windows 10 and 11, and the original seen coming back after WinScreen ended during a visit, on both.
    /// </summary>
    [Fact]
    public void A_kind_used_without_the_switch_was_seen_working_and_coming_back_on_both_versions()
    {
        var problems = new List<string>();
        foreach (AppProfile profile in AppProfiles.All.Where(profile => profile.InteractiveWithoutSwitch))
        {
            if (profile.ShowsOriginalOnClick) problems.Add($"{profile.Name}: a click shows its original, so nothing is sent to it.");
            if (profile.VerifiedBy.Count(verification => verification.On == WindowsVersions.Both) < 4)
                problems.Add($"{profile.Name}: fewer than four input tests seen on both Windows versions.");
            if (!profile.RecoveredBy.Any(verification => verification.On == WindowsVersions.Both))
                problems.Add($"{profile.Name}: no recovery test seen on both Windows versions.");
        }
        foreach (AppProfile profile in AppProfiles.All.Where(profile => !profile.InteractiveWithoutSwitch && profile.RecoveredBy.Length > 0))
            problems.Add($"{profile.Name}: recovery tests listed, but not used without the switch.");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_compatibility_page_shows_the_table_as_it_is()
    {
        string page = AppProfileDocument.Page();
        string expected = AppProfileDocument.Replace(page);
        if (expected != page && Environment.GetEnvironmentVariable("WINSCREEN_UPDATE_DOCS") == "1" && AppProfileDocument.Write(expected))
            return;
        Assert.True(expected == page,
            "FEATURE_COMPATIBILITY.md doesn't match the app profiles. Run this test with WINSCREEN_UPDATE_DOCS=1 to rewrite it.");
    }
}

/// <summary>
/// The app table on the compatibility page, made from <see cref="AppProfiles"/>. The page is embedded in the tests, so
/// the check also runs where there is no checkout.
/// </summary>
internal static class AppProfileDocument
{
    private const string Start = "<!-- app-profiles: made from src/WinScreen/Compatibility/AppProfile.cs by AppProfileTests; don't edit -->";
    private const string End = "<!-- app-profiles end -->";

    internal static string Page()
    {
        using Stream stream = typeof(AppProfileDocument).Assembly.GetManifestResourceStream("Doc.FEATURE_COMPATIBILITY.md")
            ?? throw new InvalidOperationException("The compatibility page isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    internal static string Replace(string page)
    {
        int start = page.IndexOf(Start, StringComparison.Ordinal), end = page.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("The compatibility page has no app table markers.");
        return page[..start] + Start + "\n" + Table() + End + page[(end + End.Length)..];
    }

    internal static string Table()
    {
        var text = new System.Text.StringBuilder();
        text.Append("| Kind of app | Recognised by | Used through a view | Enhanced hover | A click shows the original | Tested on | Tests |\n");
        text.Append("|---|---|---|---|---|---|---|\n");
        foreach (AppProfile profile in AppProfiles.All)
        {
            string used = profile.ShowsOriginalOnClick ? "—" : profile.InteractiveWithoutSwitch ? "Yes" : "With experimental features on";
            string hover = profile.ShowsOriginalOnClick ? "Not used: nothing is sent to it"
                : profile.Hover == PipHoverApp.None ? (profile.NeverByName ? "Never" : "Only if added by name")
                : profile.ChosenForMe ? "On with Choose for me" : "Off unless chosen";
            Verification[] all = [.. profile.VerifiedBy, .. profile.RecoveredBy];
            text.Append($"| {profile.Name} | {RecognisedBy(profile)} | {used} | {hover} | {(profile.ShowsOriginalOnClick ? "Always" : "—")} | " +
                $"{Versions(all)} | {Tests(all)} |\n");
        }
        text.Append('\n');
        text.Append("| Window inside an app | Window class | Used through | Tested on | Tests |\n");
        text.Append("|---|---|---|---|---|\n");
        foreach (InputSurface surface in AppProfiles.Surfaces)
        {
            string route = surface.Route == InputRoute.Automation ? "Accessibility" : surface.Route.ToString();
            text.Append($"| {surface.Name} | `{surface.WindowClass}` | {route} | {Versions(surface.VerifiedBy)} | {Tests(surface.VerifiedBy)} |\n");
        }
        return text.ToString();
    }

    private static string RecognisedBy(AppProfile profile)
    {
        var parts = new List<string>();
        if (profile.Programs.Length > 0)
            parts.Add("program " + string.Join(", ", profile.Programs[..^1].Select(name => $"`{name}`")) +
                (profile.Programs.Length > 1 ? " or " : "") + $"`{profile.Programs[^1]}`");
        if (profile.WindowClass is not null) parts.Add($"window class `{profile.WindowClass}`");
        if (profile.ClassPrefix is not null) parts.Add($"a window class starting `{profile.ClassPrefix}`");
        if (profile.ElectronFiles) parts.Add("Electron's files beside the program");
        string all = string.Join(" and ", parts);
        return char.ToUpperInvariant(all[0]) + all[1..];
    }

    private static string Versions(Verification[] by)
    {
        WindowsVersions on = by.Aggregate(WindowsVersions.None, (all, verification) => all | verification.On);
        return on switch
        {
            WindowsVersions.Both => "Windows 10 and 11",
            WindowsVersions.Windows10 => "Windows 10",
            WindowsVersions.Windows11 => "Windows 11",
            _ => "A rule, checked without a window",
        };
    }

    private static string Tests(Verification[] by) =>
        string.Join(", ", by.Select(verification => $"`{verification.Test[(verification.Test.IndexOf('.') + 1)..]}`"));

    /// <summary>Rewrites the page in the checkout these tests were built from, keeping its line endings.</summary>
    internal static bool Write(string page)
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "docs", "FEATURE_COMPATIBILITY.md");
            if (!File.Exists(Path.Combine(folder.FullName, "WinScreen.slnx")) || !File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            bool crlf = System.Text.Encoding.UTF8.GetString(bytes).Contains("\r\n", StringComparison.Ordinal);
            File.WriteAllText(path, crlf ? page.Replace("\n", "\r\n") : page, new System.Text.UTF8Encoding(bom));
            return true;
        }
        return false;
    }
}
