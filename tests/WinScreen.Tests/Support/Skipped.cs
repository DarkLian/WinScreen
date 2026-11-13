using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace WinScreen.Tests.Support;

/// <summary>
/// For a test that can't do its work here and returns: an app that isn't installed or is already open, a Windows
/// version, a display scale only the test VM may change. xunit 2 can't mark a test skipped once it has started, so it
/// still counts as passed; this names it and the reason in the file the test runners give in WINSCREEN_SKIPPED_FILE,
/// and they list it with what did not run (tools\TestRuns.ps1). Call it, then return.
/// </summary>
internal static class Skipped
{
    internal const string FileVariable = "WINSCREEN_SKIPPED_FILE";
    private static readonly object Gate = new();

    /// <summary>Says the calling test returns here without doing (the rest of) its work, and why.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Because(string reason, [CallerMemberName] string member = "", [CallerFilePath] string source = "") =>
        Record(reason, Environment.GetEnvironmentVariable(FileVariable), member, source);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Record(string reason, string? file, [CallerMemberName] string member = "", [CallerFilePath] string source = "")
    {
        // The caller's name first: a call just before "return" can be made a tail call, and then the test's own frame
        // is gone from the stack (seen with ScaledDisplayInputTests on Windows 11).
        string test = Named(member, source) ?? CurrentTest(new StackTrace()) ?? "an unknown test";
        Console.WriteLine($"Did not run: {test} - {reason}");
        if (string.IsNullOrEmpty(file)) return;
        // One line per call: the test, a tab, the reason. Tabs and line breaks in a reason would break the line.
        string line = test + "\t" + reason.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
        lock (Gate) File.AppendAllText(file, line);
    }

    /// <summary>Every [Fact] and [Theory] in this assembly by method name, public classes or not.</summary>
    private static readonly Lazy<ILookup<string, MethodInfo>> Tests = new(() => typeof(Skipped).Assembly.GetTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly))
        .Where(method => method.IsDefined(typeof(FactAttribute), inherit: true))
        .ToLookup(method => method.Name, StringComparer.Ordinal));

    /// <summary>
    /// The test a caller's member name (a lambda's is its method's) stands for: the one test of that name, or the one in
    /// the class named like its source file. Null for a helper that isn't a test, such as a check several tests share.
    /// </summary>
    internal static string? Named(string member, string source)
    {
        if (member.Length == 0) return null;
        MethodInfo[] found = Tests.Value[member].ToArray();
        if (found.Length > 1)
        {
            string file = Path.GetFileNameWithoutExtension(source.Replace('\\', '/').Split('/')[^1]);
            found = found.Where(method => method.DeclaringType!.Name == file).ToArray();
        }
        return found.Length == 1 ? found[0].DeclaringType!.FullName + "." + found[0].Name : null;
    }

    /// <summary>
    /// "Namespace.Class.Method" of the test on this stack: the method marked [Fact] or [Theory], or else the method a
    /// lambda, local function or async step was written in (a test can call this from another thread).
    /// </summary>
    internal static string? CurrentTest(StackTrace stack)
    {
        string? written = null;
        foreach (StackFrame frame in stack.GetFrames())
        {
            if (frame.GetMethod() is not MethodBase method || method.DeclaringType is not Type type) continue;
            if (type == typeof(Skipped)) continue;
            if (method.IsDefined(typeof(FactAttribute), inherit: true)) return type.FullName + "." + method.Name;
            if (written is null && Written(method, type) is { } name) written = name;
        }
        return written;
    }

    /// <summary>The test class and method a frame's code was written in, when it is in this assembly.</summary>
    private static string? Written(MethodBase method, Type type)
    {
        if (type.Assembly != typeof(Skipped).Assembly) return null;
        // Compiler-made types (<Name>d__3, <>c__DisplayClass4_0) sit inside the class the code was written in.
        string? fromType = null;
        while (type.Name.StartsWith('<') && type.DeclaringType is Type outer)
        {
            fromType ??= Between(type.Name);
            type = outer;
        }
        // <Name>b__0 (a lambda), <Name>g__Local|2_0 (a local function), MoveNext of <Name>d__3 (an async step).
        string? name = method.Name.StartsWith('<') ? Between(method.Name) : fromType ?? method.Name;
        return string.IsNullOrEmpty(name) ? null : type.FullName + "." + name;
    }

    private static string? Between(string compiled)
    {
        int end = compiled.IndexOf('>');
        return end > 1 ? compiled[1..end] : null;
    }
}
