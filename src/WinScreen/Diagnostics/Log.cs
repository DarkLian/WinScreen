using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinScreen.Diagnostics;

// Only kinds that can't carry a person's text exist: window titles, typed keys, the clipboard and full paths have no
// way in.
public readonly struct LogValue
{
    private readonly object? _value;
    private readonly bool _hex;

    private LogValue(object? value, bool hex = false)
    {
        _value = value;
        _hex = hex;
    }

    public static implicit operator LogValue(int value) => new(value);
    public static implicit operator LogValue(long value) => new(value);
    public static implicit operator LogValue(uint value) => new(value);
    public static implicit operator LogValue(double value) => new(Math.Round(value, 3));
    public static implicit operator LogValue(bool value) => new(value);
    public static implicit operator LogValue(Enum? value) => new(value);

    // File name only: a full path names the person's folders.
    public static LogValue Program(string? pathOrName) => new(string.IsNullOrEmpty(pathOrName) ? "" : Path.GetFileName(pathOrName));

    public static LogValue WindowClass(string? name)
    {
        string value = name ?? "";
        int separator = value.LastIndexOfAny(['\\', '/']);
        if (separator >= 0) value = value[(separator + 1)..];
        return new(value.Length > 80 ? value[..80] : value);
    }

    public static LogValue ProgramOf(nint window) => Program(Windows.WindowInfo.ProgramOf(window));

    public static LogValue ClassOf(nint window) => WindowClass(Windows.WindowInfo.ClassName(window));

    public static LogValue Code(int code) => new(code, hex: true);

    // Only text WinScreen fixes itself; a test keeps it to this folder.
    internal static LogValue Known(string text) => new(text);

    internal string Text => _value switch
    {
        null => "",
        int code when _hex => "0x" + code.ToString("X8", CultureInfo.InvariantCulture),
        bool flag => flag ? "yes" : "no",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Escape(_value.ToString() ?? ""),
    };

    private static string Escape(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char c in text) plain.Append(char.IsControl(c) || c == ' ' ? '_' : c);
        return plain.ToString();
    }
}

// Callers only queue, so this is safe from hooks and any thread; one background thread writes.
public static class Log
{
    private static DiagnosticLog? s_current;

    internal static DiagnosticLog? Current => s_current;

    public static void Start(string folder, string role, string file = DiagnosticLog.FileName)
    {
        if (s_current is not null) return;
        s_current = new DiagnosticLog(folder, file: file);
        Event("log.started", ("role", LogValue.Known(role)), ("version", LogValue.Known(Updates.AppInfo.Version)),
            ("process", Environment.ProcessId));
    }

    // name is fixed text, never anything a person wrote.
    public static void Event(string name, params (string Key, LogValue Value)[] fields) =>
        s_current?.Enqueue(new LogEntry(name, fields, null, null));

    // Never the message: it can hold titles and paths.
    public static void Error(string name, Exception error, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        s_current?.Enqueue(new LogEntry(name, [], error, Site(file, line)));

    public static void Swallowed(Exception error, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        s_current?.Enqueue(new LogEntry("swallowed", [], error, Site(file, line)));

    // Never from a hook: it waits.
    public static bool Flush(TimeSpan timeout) => s_current?.Flush(timeout) ?? true;

    internal static IReadOnlyList<string> Files => s_current?.Files ?? [];

    private static string Site(string file, int line) => $"{Path.GetFileName(file)}:{line}";
}

internal sealed record LogEntry(string Name, (string Key, LogValue Value)[] Fields, Exception? Error, string? Site)
{
    // UTC: the cheapest clock to read on a hook thread.
    public long Ticks { get; } = DateTime.UtcNow.Ticks;
    internal ManualResetEventSlim? Flushed { get; init; }
}

internal sealed class DiagnosticLog : IDisposable
{
    internal const string FileName = "winscreen.log";
    internal const string OlderFileName = "winscreen.1.log";
    internal const string HelperFileName = "winscreen-administrator.log";
    private const int LongestLine = 2000;
    private const int GiveUpAfterFailures = 5;

    private readonly string _current;
    private readonly string _older;
    private readonly long _maxBytes;
    private readonly int _capacity;
    private readonly TimeSpan _mergeFor;
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private (string Body, long Since, long Last, int Repeats)? _run;
    private int _queued;
    private int _dropped;
    private int _failures;
    private volatile bool _stopping;
    private volatile bool _off;

    internal Action? BeforeWrite { get; init; }

    public DiagnosticLog(string folder, long maxBytes = 512 * 1024, int capacity = 2048, TimeSpan? mergeFor = null, string file = FileName)
    {
        Directory.CreateDirectory(folder);
        _current = Path.Combine(folder, file);
        _older = Path.Combine(folder, OlderOf(file));
        _maxBytes = maxBytes;
        _capacity = capacity;
        _mergeFor = mergeFor ?? TimeSpan.FromMinutes(1);
        _writer = new Thread(Write) { IsBackground = true, Name = "WinScreen log", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    internal IReadOnlyList<string> Files => new[] { _older, _current }.Where(File.Exists).ToArray();

    internal static string OlderOf(string file) => Path.GetFileNameWithoutExtension(file) + ".1" + Path.GetExtension(file);

    internal int Dropped => Volatile.Read(ref _dropped);

    // Never blocks and never throws: hooks call it.
    public void Enqueue(LogEntry entry)
    {
        if (_off || _stopping) return;
        if (Interlocked.Increment(ref _queued) > _capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(entry);
        _wake.Set();
    }

    public bool Flush(TimeSpan timeout)
    {
        if (_off || _stopping) return false;
        // Not disposed: after a timeout the writer still sets it.
        var done = new ManualResetEventSlim(false);
        _queue.Enqueue(new LogEntry("", [], null, null) { Flushed = done });
        _wake.Set();
        return done.Wait(timeout);
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private void Write()
    {
        while (true)
        {
            _wake.WaitOne();
            try
            {
                BeforeWrite?.Invoke();
                Drain();
            }
            catch (Exception)
            {
                // Anything at all: an exception here would end the whole app. Give up quietly if it keeps happening.
                if (++_failures >= GiveUpAfterFailures) _off = true;
            }
            if (_stopping || _off)
            {
                try { if (!_off) Drain(); }
                catch (Exception) { _off = true; }
                ReleaseFlushes();
                return;
            }
        }
    }

    private void Drain()
    {
        var lines = new StringBuilder();
        var flushes = new List<ManualResetEventSlim>();
        while (_queue.TryDequeue(out LogEntry? entry))
        {
            if (entry.Flushed is { } flushed)
            {
                flushes.Add(flushed);
                continue;
            }
            Interlocked.Decrement(ref _queued);
            Add(lines, entry);
        }
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) lines.Append(Stamp(DateTime.UtcNow.Ticks)).Append(" log.dropped count=").Append(dropped).AppendLine();
        if (_stopping || flushes.Count > 0) EndMerges(lines, all: true);
        else EndMerges(lines, all: false);
        if (lines.Length > 0) Append(lines.ToString());
        _failures = 0;
        foreach (ManualResetEventSlim flushed in flushes) flushed.Set();
    }

    private void Add(StringBuilder lines, LogEntry entry)
    {
        string body = Body(entry);
        if (_run is { } run && run.Body == body && entry.Ticks - run.Since < _mergeFor.Ticks)
        {
            _run = run with { Last = entry.Ticks, Repeats = run.Repeats + 1 };
            return;
        }
        EndRun(lines);
        _run = (body, entry.Ticks, entry.Ticks, 0);
        lines.Append(Stamp(entry.Ticks)).Append(' ').Append(body).AppendLine();
    }

    private void EndMerges(StringBuilder lines, bool all)
    {
        if (_run is { } run && (all || DateTime.UtcNow.Ticks - run.Since >= _mergeFor.Ticks)) EndRun(lines);
    }

    private void EndRun(StringBuilder lines)
    {
        if (_run is { Repeats: > 0 } run)
            lines.Append(Stamp(run.Last)).Append(" (repeated ").Append(run.Repeats).Append(" more times) ").Append(run.Body).AppendLine();
        _run = null;
    }

    private static string Stamp(long ticks) =>
        new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    internal static string Body(LogEntry entry)
    {
        var body = new StringBuilder(entry.Name);
        foreach ((string key, LogValue value) in entry.Fields) body.Append(' ').Append(key).Append('=').Append(value.Text);
        if (entry.Error is { } error)
        {
            body.Append(" at=").Append(entry.Site);
            body.Append(" error=").Append(Chain(error));
            // By the name of the message the person saw, never its text.
            if (error is Windows.WindowOperationException refusal)
                body.Append(" reason=").Append(Localization.UiText.KeyOf(refusal.Message) ?? "unknown");
            body.Append(" code=").Append(LogValue.Code(error.HResult).Text);
            body.Append(" via=").Append(Frames(error));
        }
        return body.Length > LongestLine ? body.ToString(0, LongestLine) : body.ToString();
    }

    // Messages are left out: they can hold titles and paths.
    private static string Chain(Exception error)
    {
        var types = new List<string>();
        for (Exception? e = error; e is not null && types.Count < 4; e = e.InnerException) types.Add(e.GetType().Name);
        return string.Join('>', types);
    }

    private static string Frames(Exception error)
    {
        var methods = new List<string>();
        foreach (StackFrame frame in new StackTrace(error, false).GetFrames())
        {
            if (frame.GetMethod() is not { } method) continue;
            methods.Add($"{method.DeclaringType?.Name}.{method.Name}");
            if (methods.Count == 6) break;
        }
        return methods.Count == 0 ? "-" : string.Join('<', methods);
    }

    private void Append(string text)
    {
        FileStream? file = null;
        try
        {
            foreach (string line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                file ??= Open();
                if (file.Length > 0 && file.Length + bytes.Length > _maxBytes)
                {
                    file.Dispose();
                    TakeTurns();
                    file = Open();
                }
                file.Write(bytes);
            }
        }
        finally
        {
            file?.Dispose();
        }

        // Others may read while this writes (Save diagnostics), and the file may be moved under them.
        FileStream Open() => new(_current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    private void TakeTurns()
    {
        try
        {
            File.Move(_current, _older, overwrite: true);
        }
        catch (IOException)
        {
            // The older file is open somewhere without sharing: start the current one again instead of growing it.
            using var reset = new FileStream(_current, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    private void ReleaseFlushes()
    {
        while (_queue.TryDequeue(out LogEntry? entry)) entry.Flushed?.Set();
    }
}
