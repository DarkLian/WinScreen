using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// A test that can't do its work here says so through <see cref="Skipped"/>, so a green run lists what it didn't
/// really do (roadmap P1-8). Logic only: no window opens.
/// </summary>
public sealed class SkippedTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "winscreen-skipped-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose() => File.Delete(_file);

    private string[] Lines() => File.ReadAllLines(_file);

    [Fact]
    public void A_test_that_returns_early_is_named_with_its_reason()
    {
        Skipped.Record("Firefox isn't in test-apps.", _file);
        Skipped.Record("Two\tlines\nin one.", _file);
        Assert.Equal(
        [
            $"{typeof(SkippedTests).FullName}.{nameof(A_test_that_returns_early_is_named_with_its_reason)}\tFirefox isn't in test-apps.",
            $"{typeof(SkippedTests).FullName}.{nameof(A_test_that_returns_early_is_named_with_its_reason)}\tTwo lines in one.",
        ], Lines());
    }

    [Fact]
    public async Task The_test_is_found_after_an_await_from_a_lambda_on_another_thread_and_from_a_local_function()
    {
        await Task.Yield();
        Skipped.Record("after an await", _file);
        var thread = new Thread(() => Skipped.Record("on another thread", _file));
        thread.Start();
        thread.Join();
        Local();
        void Local() => Skipped.Record("in a local function", _file);

        string name = $"{typeof(SkippedTests).FullName}.{nameof(The_test_is_found_after_an_await_from_a_lambda_on_another_thread_and_from_a_local_function)}";
        Assert.Equal([$"{name}\tafter an await", $"{name}\ton another thread", $"{name}\tin a local function"], Lines());
    }

    [Fact]
    public void Nothing_is_written_when_no_runner_asked_for_the_list()
    {
        Skipped.Record("no file", null);
        Skipped.Record("no file", "");
        Assert.False(File.Exists(_file));
    }

    // ── Tests that return early without saying so ─────────────────────────────────────────────────────────────────

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Sources = new(() =>
    {
        Assembly assembly = typeof(SkippedTests).Assembly;
        const string prefix = "TestSource.";
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            sources[name[prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }
        return sources;
    });

    /// <summary>An if whose condition (up to three levels of brackets deep) is followed straight by return.</summary>
    private static readonly Regex BareReturn = new(
        @"\bif\s*\((?<condition>(?:[^;{}()]|\((?:[^;{}()]|\((?:[^;{}()]|\([^;{}()]*\))*\))*\))*)\)\s*return\s*;",
        RegexOptions.CultureInvariant);

    /// <summary>What says the PC can't run a test: an app or file, the Windows version, displays, permissions.</summary>
    private static readonly Regex AboutThisPc = new(
        @"File\.Exists|Directory\.Exists|OSVersion|Is64BitOperatingSystem|DisplayScale\.Allowed|InstalledAppsWanted|" +
        @"SelfElevated|WINSCREEN_TEST_|Screen\.AllScreens|OpenCalculator",
        RegexOptions.CultureInvariant);

    internal static List<string> QuietReturns(IEnumerable<KeyValuePair<string, string>> sources)
    {
        var problems = new List<string>();
        foreach ((string path, string code) in sources)
        {
            foreach (Match match in BareReturn.Matches(code))
            {
                string condition = match.Groups["condition"].Value;
                if (!AboutThisPc.IsMatch(condition)) continue;
                int line = code.AsSpan(0, match.Index).Count('\n') + 1;
                problems.Add($"{path}:{line}: if ({Regex.Replace(condition, @"\s+", " ")}) return; Say why with Skipped.Because first.");
            }
        }
        return problems;
    }

    [Fact]
    public void The_tests_own_sources_are_embedded()
    {
        Assert.Contains("Support/Skipped.cs", Sources.Value.Keys);
        Assert.Contains("SkippedTests.cs", Sources.Value.Keys);
    }

    [Fact]
    public void No_test_returns_early_for_this_PC_without_saying_why()
    {
        // This file's samples are the check's own examples; *ProbeTests.cs are local throwaway probes, never committed.
        List<string> problems = QuietReturns(Sources.Value.Where(pair =>
            pair.Key != "SkippedTests.cs" && !pair.Key.EndsWith("ProbeTests.cs", StringComparison.Ordinal)));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_check_finds_quiet_returns_and_leaves_ones_that_say_why()
    {
        static KeyValuePair<string, string> Entry(string code) => new("Sample.cs", code);
        Assert.Single(QuietReturns([Entry("if (!File.Exists(Path.Combine(Apps, \"x.exe\"))) return;")]));
        Assert.Single(QuietReturns([Entry("if (Environment.OSVersion.Version.Build < 22000)\n    return;")]));
        Assert.Single(QuietReturns([Entry("if (!InstalledAppsWanted || Process.GetProcessesByName(\"A\").Length > 0) return;")]));
        Assert.Single(QuietReturns([Entry("if (!Desktop.WaitFor(() => Find(OpenCalculator()) != 0, 9)) return;")]));
        Assert.Empty(QuietReturns([Entry("if (!File.Exists(x)) { Skipped.Because(\"No x.\"); return; }")]));
        Assert.Empty(QuietReturns([Entry("if (step != \"before-mutation\") return;")]));
        Assert.Empty(QuietReturns([Entry("if (!File.Exists(x)) return false;")]));
    }
}
