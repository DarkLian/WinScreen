using System.Text.Json;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>One program's sound session that WinScreen muted, as saved on disk until it is unmuted.</summary>
/// <param name="Device">The output device the session plays on.</param>
/// <param name="Identifier">Stays the same when the program restarts; Windows keeps the mute under it.</param>
/// <param name="Instance">The session itself, while it lasts.</param>
/// <param name="Program">The program's full path, which views of its windows are matched by.</param>
public sealed record MutedSession(string Device, string Identifier, string Instance, string Program);

/// <summary>
/// Mute from a view's menu (roadmap step 8.3). It silences the program of the view's window, every one of its
/// processes, and nothing else. Windows remembers a program's mute across restarts, so each session is written down
/// before it is muted, and every way out unmutes it: turning it off, Put back, the view closing, exit, the recovery
/// watcher and the next start. A session that was muted already is never touched, so it stays muted.
/// </summary>
internal sealed class ProgramSound(IAudioSessions audio, string? journalPath, Func<uint, string>? pathOf = null)
{
    /// <summary>A process's program path. Tests give their own, for sessions that aren't real.</summary>
    private readonly Func<uint, string> _pathOf = pathOf ?? ProcessInfo.PathOf;

    private const int ReapplyMilliseconds = 1000;

    /// <summary>Each view's window whose program the person muted, with that program's path.</summary>
    private readonly Dictionary<nint, string> _mutedWindows = [];
    private readonly List<MutedSession> _sessions = [];
    /// <summary>Muted sessions on a device that went away before they could be unmuted: the next start unmutes them.</summary>
    /// <remarks>Read at start, after <see cref="Recover"/> has unmuted what it could, so saving never drops them.</remarks>
    private readonly List<MutedSession> _elsewhere = journalPath is null ? [] : [.. Load(journalPath)];
    private long _reapplyAt;

    /// <summary>True while anything WinScreen muted is still muted: a recovery watcher is then needed.</summary>
    public bool AnyMuted => _mutedWindows.Count > 0 || _sessions.Count > 0;

    public bool Has(nint window) => _mutedWindows.ContainsKey(window);

    /// <summary>
    /// Null when the window's program has no sound session, so there is nothing to offer; otherwise whether WinScreen
    /// has it muted for this window.
    /// </summary>
    public bool? State(nint window)
    {
        if (_mutedWindows.ContainsKey(window)) return true;
        string program = ProgramOf(window);
        return program.Length > 0 && SessionsOf(program, audio.List()).Any() ? false : null;
    }

    /// <summary>Mutes the window's program. False when it couldn't be written down first, and nothing changed.</summary>
    public bool Mute(nint window)
    {
        string program = ProgramOf(window);
        if (program.Length == 0) return false;
        _mutedWindows[window] = program;
        if (MuteNewSessions(program)) return true;
        _mutedWindows.Remove(window);
        return false;
    }

    /// <summary>Takes the window's mute away; the program is unmuted once no other view keeps it muted. False when that failed.</summary>
    public bool Unmute(nint window)
    {
        if (!_mutedWindows.Remove(window, out string? program)) return true;
        if (_mutedWindows.ContainsValue(program)) return true;
        return UnmuteProgram(program);
    }

    /// <summary>Unmutes everything WinScreen muted. How many programs couldn't be unmuted.</summary>
    public int UnmuteAll()
    {
        _mutedWindows.Clear();
        int failed = 0;
        foreach (string program in _sessions.Select(session => session.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            if (!UnmuteProgram(program)) failed++;
        return failed;
    }

    /// <summary>
    /// While a program is muted, sessions it starts later are muted too: a browser starts its sound process only when a
    /// page plays, and starts it again. A session the person unmuted in Windows' mixer is left as they set it.
    /// </summary>
    public void Reapply()
    {
        if (_mutedWindows.Count == 0 || Environment.TickCount64 < _reapplyAt) return;
        _reapplyAt = Environment.TickCount64 + ReapplyMilliseconds;
        foreach (nint gone in _mutedWindows.Keys.Where(window => !User32.IsWindow(window)).ToArray())
            Unmute(gone);
        foreach (string program in _mutedWindows.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            MuteNewSessions(program);
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
            if (devices.Contains(session.Device)) Unmute(audio, now, session);
            else _elsewhere.Add(session);
            _sessions.Remove(session);
        }
        bool saved = TrySave();
        Diagnostics.Log.Event("sound.unmuted", ("program", Diagnostics.LogValue.Program(program)), ("sessions", mine.Length));
        return saved;
    }

    /// <summary>
    /// The session itself while it lasts; after the program restarted, its new sessions under the same identifier, which
    /// Windows started muted because it remembered.
    /// </summary>
    private static void Unmute(IAudioSessions audio, AudioSession[] now, MutedSession session)
    {
        bool stillThere = now.Any(candidate => candidate.Device == session.Device && candidate.Instance == session.Instance);
        if (stillThere)
            audio.SetMuted(session.Device, candidate => candidate.Instance == session.Instance, false);
        else
            audio.SetMuted(session.Device, candidate => candidate.Identifier == session.Identifier && candidate.Muted, false);
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

    /// <summary>What the file holds. A file that can't be read as a list of sessions is moved aside and counts as empty.</summary>
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

    /// <summary>
    /// Unmutes what a copy of WinScreen that ended without warning left muted: at the next start, from the recovery
    /// watcher, and from exit and crash handlers. A session on a device that isn't there now stays written down for the
    /// next time. How many sessions are left.
    /// </summary>
    public static int Recover(IAudioSessions audio, string path)
    {
        MutedSession[] left = Load(path);
        if (left.Length == 0) return 0;
        AudioSession[] now = audio.List();
        var devices = audio.Devices().ToHashSet();
        var kept = new List<MutedSession>();
        foreach (MutedSession session in left)
        {
            if (devices.Contains(session.Device)) Unmute(audio, now, session);
            else kept.Add(session);
        }
        try { Save(path, kept); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Error("sound.saveFailed", failure); }
        Diagnostics.Log.Event("sound.recovered", ("sessions", left.Length), ("kept", kept.Count));
        return kept.Count;
    }
}
