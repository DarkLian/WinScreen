using System.Reflection;
using System.Text.RegularExpressions;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Documents that point at tests keep pointing at tests that exist.</summary>
public class DocumentTests
{
    /// <summary>
    /// A test written in backticks: a class or a test file (`SomeTests`, `SomeTests.cs`), or either with a method
    /// (`SomeTests.A_thing_happens`). Probes (`*ProbeTests`) are throwaway and may be named after they are gone.
    /// </summary>
    private static readonly Regex Named = new(@"`(?<type>[A-Z]\w*Tests)(\.(?<member>\w+))?`", RegexOptions.CultureInvariant);

    /// <summary>
    /// The names in <paramref name="text"/> that are neither a test class in <paramref name="assembly"/> nor a file in
    /// <paramref name="sources"/> (file name without .cs → its text), or that name a method neither has.
    /// </summary>
    internal static List<string> Missing(string text, Assembly assembly, IReadOnlyDictionary<string, string> sources)
    {
        ILookup<string, Type> types = assembly.GetTypes().ToLookup(type => type.Name, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (Match match in Named.Matches(text))
        {
            string type = match.Groups["type"].Value, member = match.Groups["member"].Value;
            if (type.EndsWith("ProbeTests", StringComparison.Ordinal)) continue;
            bool isFile = sources.TryGetValue(type, out string? source);
            bool found = member is "" or "cs"
                ? types.Contains(type) || isFile
                : types[type].Any(found => found.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                      BindingFlags.Instance | BindingFlags.Static).Any(method => method.Name == member)) ||
                  isFile && Regex.IsMatch(source!, $@"\b(void|Task)\s+{member}\(");
            if (!found) missing.Add(match.Value.Trim('`'));
        }
        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Every_test_a_document_names_exists()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinScreen.slnx"))) root = root.Parent;
        if (root is null || !Directory.Exists(Path.Combine(root.FullName, "docs")))
        {
            Skipped.Because("The tests aren't running from a checkout, so there are no documents to read.");
            return;
        }

        string tests = Path.Combine(root.FullName, "tests", "WinScreen.Tests");
        Dictionary<string, string> sources = Directory.GetFiles(tests, "*Tests.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                           !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file)!, File.ReadAllText, StringComparer.Ordinal);

        // Current documents only: an archive of history keeps the names tests had at the time.
        var problems = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root.FullName, "docs"), "*.md")
                     .Where(file => !Path.GetFileName(file).Contains("HISTORY", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            foreach (string name in Missing(File.ReadAllText(file), typeof(DocumentTests).Assembly, sources))
                problems.Add($"{Path.GetFileName(file)}: {name}");
        }
        Assert.True(problems.Count == 0, "Named in a document but not a test here:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_document_check_fails_for_a_test_or_class_that_does_not_exist()
    {
        Assembly here = typeof(DocumentTests).Assembly;
        var sources = new Dictionary<string, string> { ["SomeFileTests"] = "public void A_test_in_the_file() { }" };

        Assert.Empty(Missing("`DocumentTests` and `DocumentTests.Every_test_a_document_names_exists`", here, sources));
        Assert.Empty(Missing("`SomeFileTests`, `SomeFileTests.cs` and `SomeFileTests.A_test_in_the_file`", here, sources));
        Assert.Empty(Missing("`AppSettings`, `Pip/PipWindow.cs` and `GoneProbeTests` (removed)", here, sources));
        Assert.Equal(["NoSuchTests", "NoSuchTests.cs", "DocumentTests.No_such_test", "SomeFileTests.No_such_test"],
            Missing("`NoSuchTests`, `NoSuchTests.cs`, `DocumentTests.No_such_test`, `SomeFileTests.No_such_test`", here, sources));
    }
}
