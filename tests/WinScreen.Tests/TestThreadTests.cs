using System.Text.RegularExpressions;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Rules for the tests themselves. The Magnification API keeps updating only on the first thread that used it in a
/// process, so a test that turned filter Dark on from another thread stopped every Dark mode test after it in the same
/// run (TD-67: all nine DarkModeTests failed in whole runs whenever PressWatchTests ran first).
/// </summary>
public class TestThreadTests
{
    private static readonly Regex UiThread = new(@"\b(\w+)\.OnUiThread\(", RegexOptions.CultureInvariant);

    /// <summary>Each <c>new DarkMode(</c> whose nearest enclosing UI-thread call isn't Dark mode's own, by line.</summary>
    internal static List<int> DarkModeOffItsThread(string source)
    {
        var wrong = new List<int>();
        foreach (Match made in Regex.Matches(source, @"new DarkMode\("))
        {
            Match? nearest = UiThread.Matches(source[..made.Index]).LastOrDefault();
            if (nearest?.Groups[1].Value != "DarkModeTests")
                wrong.Add(source[..made.Index].Count(c => c == '\n') + 1);
        }
        return wrong;
    }

    [Fact]
    public void A_broken_example_is_caught() =>
        Assert.Equal([3], DarkModeOffItsThread("x\nWindowMenuBuilderTests.OnUiThread(() =>\n{ using var dark = new DarkMode(); });"));

    [Fact]
    public void Dark_mode_is_turned_on_only_on_its_own_test_thread()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinScreen.slnx"))) root = root.Parent;
        if (root is null)
        {
            Skipped.Because("The tests aren't running from a checkout, so there are no test sources to read.");
            return;
        }
        string tests = Path.Combine(root.FullName, "tests", "WinScreen.Tests");
        var problems = new List<string>();
        foreach (string file in Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                    Path.GetFileName(file) is not ("DarkModeTests.cs" or "TestThreadTests.cs")))
            foreach (int line in DarkModeOffItsThread(File.ReadAllText(file)))
                problems.Add($"{Path.GetFileName(file)}:{line}");
        Assert.True(problems.Count == 0, "Dark mode turned on outside DarkModeTests.OnUiThread: " + string.Join(", ", problems));
    }
}
