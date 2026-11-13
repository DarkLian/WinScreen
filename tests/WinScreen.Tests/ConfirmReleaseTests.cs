using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// tools\Confirm-Release.ps1 against sample result folders (roadmap P1-8): it passes only with a clean full pair run,
/// a full Windows 11 run and a logic run for the commit, and says what did not run. Logic only: no window opens.
/// </summary>
public sealed class ConfirmReleaseTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("winscreen-gate-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Results => Path.Combine(_root, "TestResults");
    private string Pair => Path.Combine(Results, "vm", "pair-20261114-100000");
    private string Host => Path.Combine(Results, "host", "20261114-110000");
    private string Logic => Path.Combine(Results, "logic", "20261114-120000");

    private static void Json(string folder, object value) =>
        File.WriteAllText(Path.Combine(folder, "source.json"), JsonSerializer.Serialize(value));

    /// <summary>A .trx as the test runner writes it, with only what the gate reads.</summary>
    private static void Trx(string folder, string suite, params (string Test, string Outcome)[] results)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
        foreach ((string test, string outcome) in results)
        {
            xml.Append($"<UnitTestResult testName=\"{test}\" outcome=\"{outcome}\">");
            if (outcome == "NotExecuted") xml.Append("<Output><ErrorInfo><Message>Needs a second display.</Message></ErrorInfo></Output>");
            xml.Append("</UnitTestResult>");
        }
        xml.Append("</Results></TestRun>");
        File.WriteAllText(Path.Combine(folder, suite + ".trx"), xml.ToString());
    }

    private static void Finished(string folder, int code = 0) => File.WriteAllText(Path.Combine(folder, "exitcode.txt"), code + "\r\n");

    /// <summary>Everything a release needs, all passing.</summary>
    private void Ready()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");

        Directory.CreateDirectory(Pair);
        Json(Pair, new { commit = Commit, clean = true, tool = "pair" });
        string rework = Directory.CreateDirectory(Path.Combine(Pair, "WinScreen-Rework")).FullName;
        Json(rework, new { commit = Commit, clean = true, tool = "vm", filter = (string?)null, packagedFilter = "FullyQualifiedName~BrowserInputTests" });
        Trx(rework, "packaged", ("A.Browser", "Passed"));
        Finished(rework);
        string win10 = Directory.CreateDirectory(Path.Combine(Pair, "WinScreen-Win10")).FullName;
        Json(win10, new { commit = Commit, clean = true, tool = "vm", filter = (string?)null });
        Trx(win10, "desktop", ("A.One", "Passed"), ("A.Two", "Passed"), ("A.Displays", "NotExecuted"));
        Trx(win10, "packaged", ("A.Smoke", "Passed"));
        Finished(win10);

        Directory.CreateDirectory(Host);
        Json(Host, new { commit = Commit, clean = true, tool = "host", windows = "11", all = true, administrator = false, filter = (string?)null });
        Trx(Host, "desktop", ("A.One", "Passed"), ("A.Calculator", "Passed"));
        File.WriteAllText(Path.Combine(Host, "desktop.returned-early.txt"),
            "A.Calculator\tNo Calculator of its own.\nA.Calculator\tNo Calculator of its own.\n");
        Finished(Host);

        Directory.CreateDirectory(Logic);
        Json(Logic, new { commit = Commit, clean = true, tool = "logic" });
        Trx(Logic, "logic", ("L.One", "Passed"));
        Finished(Logic);
    }

    private (int Code, string Output) Confirm(string version = "1.0.0")
    {
        string script = Path.Combine(AppContext.BaseDirectory, "tools", "Confirm-Release.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Version", version,
            "-Commit", Commit, "-ResultsRoot", Results, "-PropsFile", Path.Combine(_root, "Directory.Build.props") })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    [Fact]
    public void Recorded_runs_for_the_commit_pass_and_give_the_tag_lines_and_what_did_not_run()
    {
        Ready();
        (int code, string output) = Confirm();
        Assert.True(code == 0, output);
        Assert.Contains($"Release-Gate: {Commit}", output);
        Assert.Contains("Release-Gate-Windows-10: pair-20261114-100000 (desktop 2 passed, packaged 2 passed)", output);
        Assert.Contains("Release-Gate-Windows-11: 20261114-110000 (desktop 2 passed)", output);
        Assert.Contains("Release-Gate-Logic: 20261114-120000 (logic 1 passed)", output);
        Assert.Contains("Windows 10 (WinScreen-Win10) desktop: A.Displays - Needs a second display.", output);
        Assert.Contains("Windows 11 desktop: A.Calculator - No Calculator of its own. (returned early, 2 times)", output);
        Assert.Contains("A stable release also needs", output);
    }

    [Fact]
    public void A_prerelease_isnt_given_the_stable_list()
    {
        Ready();
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project><PropertyGroup><Version>1.0.0-rc</Version></PropertyGroup></Project>");
        (int code, string output) = Confirm("1.0.0-rc");
        Assert.True(code == 0, output);
        Assert.DoesNotContain("A stable release also needs", output);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "a failed test", "1 desktop tests failed" },
        { "uncommitted changes", "uncommitted changes" },
        { "another commit", "No full Windows 11 run for 0123456" },
        { "a filtered host run", "No full Windows 11 run for 0123456" },
        { "a host run on Windows 10", "ran on Windows 10" },
        { "a host run as administrator", "ran as administrator" },
        { "a missing trx", "no logic.trx" },
        { "an unfinished run", "didn't finish" },
        { "a failed exit code", "exit code 1" },
        { "another version", "Directory.Build.props says 0.9.0" },
        { "a filtered VM run", "ran only FullyQualifiedName~Anchor" },
        { "one VM", "1 VM results" },
        { "no desktop suite in the pair", "neither VM ran the desktop suite" },
        { "a newer failed run", "1 desktop tests failed" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Anything_missing_or_failed_refuses_and_says_what(string change, string said)
    {
        Ready();
        string win10 = Path.Combine(Pair, "WinScreen-Win10");
        switch (change)
        {
            case "a failed test": Trx(Host, "desktop", ("A.One", "Passed"), ("A.Two", "Failed")); break;
            case "uncommitted changes": Json(Logic, new { commit = Commit, clean = false, tool = "logic" }); break;
            case "another commit": Json(Host, new { commit = "fedcba9876543210fedcba9876543210fedcba98", clean = true, windows = "11", all = true }); break;
            case "a filtered host run": Json(Host, new { commit = Commit, clean = true, windows = "11", all = false, filter = "FullyQualifiedName~Anchor" }); break;
            case "a host run on Windows 10": Json(Host, new { commit = Commit, clean = true, windows = "10", all = true }); break;
            case "a host run as administrator": Json(Host, new { commit = Commit, clean = true, windows = "11", all = true, administrator = true }); break;
            case "a missing trx": File.Delete(Path.Combine(Logic, "logic.trx")); break;
            case "an unfinished run": File.Delete(Path.Combine(Logic, "exitcode.txt")); break;
            case "a failed exit code": Finished(Host, 1); break;
            case "another version": File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project><PropertyGroup><Version>0.9.0</Version></PropertyGroup></Project>"); break;
            case "a filtered VM run": Json(win10, new { commit = Commit, clean = true, filter = "FullyQualifiedName~Anchor" }); break;
            case "one VM": Directory.Delete(Path.Combine(Pair, "WinScreen-Rework"), recursive: true); break;
            case "no desktop suite in the pair": File.Delete(Path.Combine(win10, "desktop.trx")); break;
            case "a newer failed run":
                string newer = Directory.CreateDirectory(Path.Combine(Results, "host", "20261114-130000")).FullName;
                Json(newer, new { commit = Commit, clean = true, windows = "11", all = true });
                Trx(newer, "desktop", ("A.One", "Failed"));
                Finished(newer);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        (int code, string output) = Confirm();
        Assert.True(code == 1, output);
        Assert.Contains("Not ready to tag v1.0.0", output);
        Assert.Contains(said, output);
        Assert.DoesNotContain("Release-Gate:", output);
    }

    /// <summary>A later Windows 11 run for the commit: its tests passed (or failed), maybe some returned early.</summary>
    private void Rerun(string name, string commit, (string Test, string Outcome)[] results, string? early = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(Results, "host", name)).FullName;
        Json(folder, new { commit, clean = true, tool = "host", windows = "11", all = false, administrator = false, filter = "FullyQualifiedName~A" });
        Trx(folder, "desktop", results);
        if (early is not null) File.WriteAllText(Path.Combine(folder, "desktop.returned-early.txt"), early + "\tNo app here.\n");
        Finished(folder, results.Any(r => r.Outcome == "Failed") ? 1 : 0);
    }

    /// <summary>
    /// The owner's rule (13 Nov 2026): a full Windows 11 run's failures count only when every one passed again in a
    /// later run on this PC for the same commit, really run, in a run with no failures of its own. The gate says so.
    /// </summary>
    [Theory]
    [InlineData("passed again later", true, "(desktop 2 passed; 2 failed, passed again in 20261114-120000)")]
    [InlineData("a newer targeted run with nothing failed", true, "Release-Gate-Windows-11: 20261114-110000 (desktop 2 passed)")]
    [InlineData("one failure not run again", false, "1 desktop tests failed")]
    [InlineData("a rerun with a failure of its own", false, "2 desktop tests failed")]
    [InlineData("a rerun for another commit", false, "2 desktop tests failed")]
    [InlineData("a rerun before the full run", false, "2 desktop tests failed")]
    [InlineData("a rerun that returned early", false, "1 desktop tests failed")]
    public void A_full_runs_failures_count_only_when_they_passed_again_later(string change, bool passes, string said)
    {
        Ready();
        if (change != "a newer targeted run with nothing failed")
        {
            Trx(Host, "desktop", ("A.One", "Passed"), ("A.Two(x: 1)", "Failed"), ("A.Three", "Failed"), ("A.Calculator", "Passed"));
            Finished(Host, 1);
        }
        const string other = "fedcba9876543210fedcba9876543210fedcba98";
        switch (change)
        {
            case "passed again later": Rerun("20261114-120000", Commit, [("A.Two(x: 1)", "Passed"), ("A.Three", "Passed")]); break;
            case "a newer targeted run with nothing failed": Rerun("20261114-120000", Commit, [("A.One", "Passed")]); break;
            case "one failure not run again": Rerun("20261114-120000", Commit, [("A.Two(x: 1)", "Passed")]); break;
            case "a rerun with a failure of its own": Rerun("20261114-120000", Commit, [("A.Two(x: 1)", "Passed"), ("A.Three", "Passed"), ("A.Four", "Failed")]); break;
            case "a rerun for another commit": Rerun("20261114-120000", other, [("A.Two(x: 1)", "Passed"), ("A.Three", "Passed")]); break;
            case "a rerun before the full run": Rerun("20261114-100500", Commit, [("A.Two(x: 1)", "Passed"), ("A.Three", "Passed")]); break;
            case "a rerun that returned early": Rerun("20261114-120000", Commit, [("A.Two(x: 1)", "Passed"), ("A.Three", "Passed")], early: "A.Three"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        (int code, string output) = Confirm();
        Assert.True(code == (passes ? 0 : 1), output);
        Assert.Contains(said, output);
        if (!passes) Assert.DoesNotContain("Release-Gate:", output);
    }

    [Fact]
    public void A_version_that_isnt_a_release_version_is_refused()
    {
        Ready();
        (int code, string output) = Confirm("1.0");
        Assert.Equal(1, code);
        Assert.Contains("isn't a release version", output);
    }
}
