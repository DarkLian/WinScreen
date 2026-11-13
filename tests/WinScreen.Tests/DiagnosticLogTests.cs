using System.Diagnostics;
using WinScreen.Diagnostics;
using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>WinScreen's own log: bounded, never in a hook's way, and never holding a person's text (roadmap P0-4).</summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WinScreenLog-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static LogEntry Entry(string name, params (string, LogValue)[] fields) => new(name, fields, null, null);

    private string Read() => string.Concat(new[] { DiagnosticLog.OlderFileName, DiagnosticLog.FileName }
        .Select(name => Path.Combine(_folder, name)).Where(File.Exists).Select(path =>
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }));

    [Fact]
    public void The_log_takes_turns_between_two_files_and_never_grows_past_them()
    {
        using var log = new DiagnosticLog(_folder, maxBytes: 4096);
        for (int i = 0; i < 500; i++) log.Enqueue(Entry("command", ("number", i)));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        string current = Path.Combine(_folder, DiagnosticLog.FileName), older = Path.Combine(_folder, DiagnosticLog.OlderFileName);
        Assert.True(File.Exists(older));
        Assert.True(new FileInfo(current).Length <= 4096 && new FileInfo(older).Length <= 4096);
        Assert.Contains("number=499", File.ReadAllText(current));
        Assert.DoesNotContain("number=0" + Environment.NewLine, Read());
    }

    [Fact]
    public void Titles_messages_and_folders_never_reach_the_file()
    {
        using var log = new DiagnosticLog(_folder);
        var failure = new IOException(@"Budget 2026 - Excel could not open C:\Users\Someone\Documents\secret.xlsx",
            new InvalidOperationException("Chat with Alex"));
        log.Enqueue(new LogEntry("swallowed", [], failure, "Some.cs:12"));
        log.Enqueue(Entry("press", ("program", LogValue.Program(@"C:\Users\Someone\Apps\tool.exe")),
            ("class", LogValue.WindowClass(@"C:\Users\Someone\Odd class name")), ("command", AppCommand.TogglePin)));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        string text = Read();
        foreach (string secret in new[] { "Budget", "Excel", "Someone", "secret", "Documents", "Alex", @"C:\" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains("program=tool.exe", text);
        Assert.Contains("class=Odd_class_name", text);
        Assert.Contains("command=TogglePin", text);
        Assert.Contains("error=IOException>InvalidOperationException", text);
        Assert.Contains("at=Some.cs:12", text);
    }

    [Fact]
    public void A_stalled_writer_never_holds_up_whoever_logs_and_says_how_much_it_dropped()
    {
        using var hold = new ManualResetEventSlim(false);
        using var log = new DiagnosticLog(_folder, capacity: 100) { BeforeWrite = () => hold.Wait() };
        log.Enqueue(Entry("first"));

        var clock = Stopwatch.StartNew();
        var caller = new Thread(() => { for (int i = 0; i < 5000; i++) log.Enqueue(Entry("busy", ("number", i))); });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "Logging waited for the writer.");
        Assert.True(clock.ElapsedMilliseconds < 1000, $"5000 entries took {clock.ElapsedMilliseconds} ms while the writer was held.");
        Assert.True(log.Dropped > 0);

        hold.Set();
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Contains("log.dropped count=", Read());
    }

    [Fact]
    public void A_line_repeated_many_times_is_written_once_and_counted()
    {
        using var log = new DiagnosticLog(_folder);
        for (int i = 0; i < 1000; i++) log.Enqueue(Entry("capture.fallback", ("code", LogValue.Code(unchecked((int)0x887A0005)))));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        string[] lines = Read().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("capture.fallback code=0x887A0005", lines[0]);
        Assert.Contains("(repeated 999 more times) capture.fallback", lines[1]);
    }

    [Fact]
    public void Writing_carries_on_while_someone_has_the_files_open()
    {
        using var log = new DiagnosticLog(_folder, maxBytes: 1024);
        log.Enqueue(Entry("first"));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        using (new FileStream(Path.Combine(_folder, DiagnosticLog.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            for (int i = 0; i < 200; i++) log.Enqueue(Entry("while.open", ("number", i)));
            Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        }
        for (int i = 0; i < 200; i++) log.Enqueue(Entry("after", ("number", i)));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Contains("after number=199", Read());
    }

    [Fact]
    public void Logging_after_the_log_closed_does_nothing_and_never_throws()
    {
        var log = new DiagnosticLog(_folder);
        log.Dispose();
        log.Enqueue(Entry("late"));
        Assert.False(log.Flush(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Logging_before_the_log_starts_does_nothing()
    {
        // Tests and the recovery watcher before its parent dies log into nothing: no file, no thread.
        if (Log.Current is not null) return;
        Log.Event("nothing", ("number", 1));
        Log.Swallowed(new IOException());
        Assert.True(Log.Flush(TimeSpan.FromMilliseconds(10)));
        Assert.Empty(Log.Files);
    }
}
