using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinScreen.Diagnostics;

/// <summary>
/// One value in a log line. Only kinds that can't carry a person's text exist: numbers, yes/no, named states, a
/// program's file name and a window class. Window titles, typed keys, the clipboard and full paths have no way in.
/// </summary>
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
    public static implicit operator LogValue(Enum value) => new(value);

    /// <summary>A program, by its file name only: a full path names the person's folders.</summary>
    public static LogValue Program(string? pathOrName) => new(string.IsNullOrEmpty(pathOrName) ? "" : Path.GetFileName(pathOrName));

    /// <summary>A window class name. Classes are set by programs, not people; capped and stripped of anything path-like.</summary>
    public static LogValue WindowClass(string? name)
    {
        string value = name ?? "";
        int separator = value.LastIndexOfAny(['\\', '/']);
        if (separator >= 0) value = value[(separator + 1)..];
        return new(value.Length > 80 ? value[..80] : value);
    }

    /// <summary>An error code, written as hex.</summary>
    public static LogValue Code(int code) => new(code, hex: true);

    /// <summary>Text WinScreen itself fixes, such as its version. Only for this folder; a test keeps it here.</summary>
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

/// <summary>
/// WinScreen's own short log, kept in its data folder to help fix problems. It never leaves the PC: a person can
/// save it with Save diagnostics and attach it to a report themselves. Callers only queue an entry, so this is safe
/// from hooks and any thread; one background thread writes. Nothing is written until <see cref="Start"/>.
/// </summary>
public static class Log
{
    private static DiagnosticLog? s_current;

    internal static DiagnosticLog? Current => s_current;

    /// <summary>Starts writing to <c>winscreen.log</c> in the data folder. Once per process.</summary>
    public static void Start(string folder, string role)
    {
        if (s_current is not null) return;
        s_current = new DiagnosticLog(folder);
        Event("log.started", ("role", LogValue.Known(role)), ("version", LogValue.Known(Updates.AppInfo.Version)),
            ("process", Environment.ProcessId));
    }

    /// <summary>Something that happened. <paramref name="name"/> is fixed text, such as "command" or "capture.fallback".</summary>
    public static void Event(string name, params (string Key, LogValue Value)[] fields) =>
        s_current?.Enqueue(new LogEntry(name, fields, null, null));

    /// <summary>An exception: its type, code and the methods it passed through. Never its message, which can hold titles and paths.</summary>
    public static void Error(string name, Exception error, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        s_current?.Enqueue(new LogEntry(name, [], error, Site(file, line)));

    /// <summary>An exception that is expected and handled where it is caught, kept so a pattern of them can be seen.</summary>
    public static void Swallowed(Exception error, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        s_current?.Enqueue(new LogEntry("swallowed", [], error, Site(file, line)));

    /// <summary>Waits up to <paramref name="timeout"/> for everything queued so far to reach the file. Never from a hook.</summary>
    public static bool Flush(TimeSpan timeout) => s_current?.Flush(timeout) ?? true;

    /// <summary>The log files, newest last, for Save diagnostics.</summary>
    internal static IReadOnlyList<string> Files => s_current?.Files ?? [];

    private static string Site(string file, int line) => $"{Path.GetFileName(file)}:{line}";
}

internal sealed record LogEntry(string Name, (string Key, LogValue Value)[] Fields, Exception? Error, string? Site)
{
    /// <summary>UTC, the cheapest clock to read on a hook thread; shown in local time when written.</summary>
    public long Ticks { get; } = DateTime.UtcNow.Ticks;
    internal ManualResetEventSlim? Flushed { get; init; }
}

/// <summary>The writer behind <see cref="Log"/>: a bounded queue, one thread, two files that take turns.</summary>
internal sealed class DiagnosticLog : IDisposable
{
    internal const string FileName = "winscreen.log";
    internal const string OlderFileName = "winscreen.1.log";
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
    private readonly Dictionary<string, (long Since, int Repeats)> _recent = [];
    private int _queued;
    private int _dropped;
    private int _failures;
    private volatile bool _stopping;
    private volatile bool _off;

    /// <summary>For tests: runs on the writer thread before each batch, so a test can hold the writer still.</summary>
    internal Action? BeforeWrite { get; init; }

    public DiagnosticLog(string folder, long maxBytes = 512 * 1024, int capacity = 2048, TimeSpan? mergeFor = null)
    {
        Directory.CreateDirectory(folder);
        _current = Path.Combine(folder, FileName);
        _older = Path.Combine(folder, OlderFileName);
        _maxBytes = maxBytes;
        _capacity = capacity;
        _mergeFor = mergeFor ?? TimeSpan.FromMinutes(1);
        // Waits for work without a timeout: no polling, no timer.
        _writer = new Thread(Write) { IsBackground = true, Name = "WinScreen log", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    internal IReadOnlyList<string> Files => new[] { _older, _current }.Where(File.Exists).ToArray();

    internal int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Queues an entry, or counts it as dropped when the queue is full. Never blocks and never throws.</summary>
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
        using var done = new ManualResetEventSlim(false);
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
            catch (Exception) when (!_stopping)
            {
                // Anything at all: an exception here would end the whole app. Give up quietly if it keeps happening.
                if (++_failures >= GiveUpAfterFailures) _off = true;
            }
            if (_stopping || _off)
            {
                try { if (!_off) Drain(); }
                catch (Exception) { _off = true; } // Closing anyway; the lines are lost, the app is not.
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

    /// <summary>The first of a run of identical lines is written; the rest are counted and summed up once.</summary>
    private void Add(StringBuilder lines, LogEntry entry)
    {
        string body = Body(entry);
        if (_recent.TryGetValue(body, out var seen) && entry.Ticks - seen.Since < _mergeFor.Ticks)
        {
            _recent[body] = (seen.Since, seen.Repeats + 1);
            return;
        }
        if (_recent.TryGetValue(body, out seen) && seen.Repeats > 0) lines.Append(Repeated(body, seen.Repeats));
        _recent[body] = (entry.Ticks, 0);
        lines.Append(Stamp(entry.Ticks)).Append(' ').Append(body).AppendLine();
    }

    private void EndMerges(StringBuilder lines, bool all)
    {
        long now = DateTime.UtcNow.Ticks;
        foreach ((string body, (long since, int repeats)) in _recent.ToArray())
        {
            if (!all && now - since < _mergeFor.Ticks) continue;
            if (repeats > 0) lines.Append(Repeated(body, repeats));
            _recent.Remove(body);
        }
    }

    private static string Repeated(string body, int repeats) =>
        $"{Stamp(DateTime.UtcNow.Ticks)} (repeated {repeats} more times) {body}{Environment.NewLine}";

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
            body.Append(" code=").Append(LogValue.Code(error.HResult).Text);
            body.Append(" via=").Append(Frames(error));
        }
        return body.Length > LongestLine ? body.ToString(0, LongestLine) : body.ToString();
    }

    /// <summary>Exception types, outermost first. Messages are left out: they can hold titles and paths.</summary>
    private static string Chain(Exception error)
    {
        var types = new List<string>();
        for (Exception? e = error; e is not null && types.Count < 4; e = e.InnerException) types.Add(e.GetType().Name);
        return string.Join('>', types);
    }

    /// <summary>The methods the exception passed through, innermost first. File names and lines are left out.</summary>
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

    /// <summary>Appends line by line, switching files whenever the next line wouldn't fit, however big the batch.</summary>
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
