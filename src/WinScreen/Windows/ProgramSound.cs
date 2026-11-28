using System.Text.Json;
using WinScreen.Native;

namespace WinScreen.Windows;

// Identifier stays the same when the program restarts (Windows keeps the mute under it); Program is the full path views
// are matched by.
public sealed record MutedSession(string Device, string Identifier, string Instance, string Program);

// Windows remembers a program's mute across restarts, so each session is written down before it is muted, and every way
// out unmutes it. A session that was muted already is never touched.
internal sealed class ProgramSound(IAudioSessions audio, string? journalPath, Func<uint, string>? pathOf = null)
{
    private readonly Func<uint, string> _pathOf = pathOf ?? ProcessInfo.PathOf;

    private const int ReapplyMilliseconds = 1000, SlowReapplyMilliseconds = 30_000;

    private readonly Dictionary<nint, string> _mutedWindows = [];
    private readonly List<MutedSession> _sessions = [];
    // Read at start, after Recover has unmuted what it could, so saving never drops them.
    private readonly List<MutedSession> _elsewhere = journalPath is null ? [] : [.. Load(journalPath)];
    private long _reapplyAt;

    public bool AnyMuted => _mutedWindows.Count > 0 || _sessions.Count > 0;

    public bool Has(nint window) => _mutedWindows.ContainsKey(window);

    public bool? State(nint window)
    {
        if (_mutedWindows.ContainsKey(window)) return true;
        string program = ProgramOf(window);
        return program.Length > 0 && SessionsOf(program, audio.List()).Any() ? false : null;
    }

    public bool Mute(nint window)
    {
        string program = ProgramOf(window);
        if (program.Length == 0) return false;
        _mutedWindows[window] = program;
        if (MuteNewSessions(program)) return true;
        _mutedWindows.Remove(window);
        return false;
    }

    public bool Unmute(nint window)
    {
        if (!_mutedWindows.Remove(window, out string? program)) return true;
        if (_mutedWindows.ContainsValue(program)) return true;
        return UnmuteProgram(program);
    }

    public int UnmuteAll()
    {
        _mutedWindows.Clear();
        int failed = 0;
        foreach (string program in _sessions.Select(session => session.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            if (!UnmuteProgram(program)) failed++;
        return failed;
    }

    // A browser starts its sound process only when a page plays, so sessions started later are muted too. One the
    // person unmuted in Windows' mixer is left as they set it.
    public void Reapply()
    {
        long started = Environment.TickCount64;
        if (_mutedWindows.Count == 0 || started < _reapplyAt) return;
        foreach (nint gone in _mutedWindows.Keys.Where(window => !User32.IsWindow(window)).ToArray())
            Unmute(gone);
        foreach (string program in _mutedWindows.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            MuteNewSessions(program);
        // This runs on the thread that answers clicks: an audio service that stopped answering is asked rarely.
        long took = Environment.TickCount64 - started;
        _reapplyAt = Environment.TickCount64 + (took >= ReapplyMilliseconds ? SlowReapplyMilliseconds : ReapplyMilliseconds);
        if (took >= ReapplyMilliseconds) Diagnostics.Log.Event("sound.slow", ("milliseconds", took));
    }

    private bool MuteNewSessions(string program)
    {
        AudioSession[] fresh = SessionsOf(program, audio.List())
            .Where(session => !session.Muted && !_sessions.Any(known => known.Device == session.Device && known.Instance == session.Instance))
            .ToArray();
        if (fresh.Length == 0) return true;
        // Written down first: a crash between the two leaves an unmuted session to unmute, which changes nothing.
        MutedSession[] added = fresh.Select(session => new MutedSession(session.Device, session.Identifier, session.Instance, program)).ToArray();
        _sessions.AddRange(added);
        if (!TrySave())
        {
            foreach (MutedSession session in added) _sessions.Remove(session);
            return false;
        }
        // Kept when the call ran out of time: its thread may still mute it.
        foreach (MutedSession session in added)
            if (audio.SetMuted(session.Device, candidate => candidate.Instance == session.Instance, true) == 0)
                _sessions.Remove(session);
        TrySave();
        Diagnostics.Log.Event("sound.muted", ("program", Diagnostics.LogValue.Program(program)), ("sessions", added.Length));
        return true;
    }

    private bool UnmuteProgram(string program)
    {
        MutedSession[] mine = _sessions.Where(session => string.Equals(session.Program, program, StringComparison.OrdinalIgnoreCase)).ToArray();
        AudioSession[] now = audio.List();
        var devices = audio.Devices().ToHashSet();
        foreach (MutedSession session in mine)
        {
            // One not unmuted stays written down, for the next start to unmute.
            if (!devices.Contains(session.Device) || !Unmute(audio, now, session)) _elsewhere.Add(session);
            _sessions.Remove(session);
        }
        bool saved = TrySave();
        Diagnostics.Log.Event("sound.unmuted", ("program", Diagnostics.LogValue.Program(program)), ("sessions", mine.Length));
        return saved;
    }

    // After a restart, Windows starts the program's new sessions muted under the same identifier.
    private static bool Unmute(IAudioSessions audio, AudioSession[] now, MutedSession session)
    {
        bool stillThere = now.Any(candidate => candidate.Device == session.Device && candidate.Instance == session.Instance);
        return (stillThere
            ? audio.SetMuted(session.Device, candidate => candidate.Instance == session.Instance, false)
            : audio.SetMuted(session.Device, candidate => candidate.Identifier == session.Identifier && candidate.Muted, false))
            != AudioSessions.Unanswered;
    }

    private IEnumerable<AudioSession> SessionsOf(string program, AudioSession[] sessions)
    {
        var paths = new Dictionary<uint, string>();
        foreach (AudioSession session in sessions)
        {
            if (!paths.TryGetValue(session.ProcessId, out string? path))
                paths[session.ProcessId] = path = _pathOf(session.ProcessId);
            if (string.Equals(path, program, StringComparison.OrdinalIgnoreCase)) yield return session;
        }
    }

    private string ProgramOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return _pathOf(process);
    }

    private bool TrySave()
    {
        if (journalPath is null) return true;
        try
        {
            Save(journalPath, [.. _sessions, .. _elsewhere]);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Error("sound.saveFailed", failure);
            return false;
        }
    }

    internal static void Save(string path, IReadOnlyCollection<MutedSession> sessions)
    {
        if (sessions.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, sessions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static MutedSession[] Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            MutedSession?[]? read = JsonSerializer.Deserialize<MutedSession?[]>(File.ReadAllText(path));
            return read?.OfType<MutedSession>().Where(session => session is { Device.Length: > 0, Identifier.Length: > 0, Instance: not null, Program: not null })
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, path + ".invalid-" + Guid.NewGuid().ToString("N"));
                Settings.BackupFiles.Prune(path, ".invalid-");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(failure); }
            return [];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Swallowed(failure);
            return [];
        }
    }

    public static int Recover(IAudioSessions audio, string path)
    {
        MutedSession[] left = Load(path);
        if (left.Length == 0) return 0;
        AudioSession[] now = audio.List();
        var devices = audio.Devices().ToHashSet();
        var kept = new List<MutedSession>();
        foreach (MutedSession session in left)
        {
            if (!devices.Contains(session.Device) || !Unmute(audio, now, session)) kept.Add(session);
        }
        try { Save(path, kept); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Error("sound.saveFailed", failure); }
        Diagnostics.Log.Event("sound.recovered", ("sessions", left.Length), ("kept", kept.Count));
        return kept.Count;
    }
}
