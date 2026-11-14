using System.Text.Json;
using WinScreen.Native;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Mute from a view's menu (roadmap step 8.3), against a mixer made up for the test: what is written down, what is
/// muted and unmuted, and what recovery does after WinScreen ended without warning.
/// </summary>
public sealed class ProgramSoundTests
{
    private const string Player = @"C:\Apps\player.exe", Other = @"C:\Apps\other.exe", Speakers = "speakers", Headphones = "headphones";

    /// <summary>Another program's process; every other process, the windows' own included, counts as the player.</summary>
    private const uint OtherProcess = 4_000_000_000;

    private static string PathOf(uint process) => process == OtherProcess ? Other : Player;

    private static readonly nint Window = User32.GetDesktopWindow(), SecondWindow = User32.GetShellWindow();

    private sealed class Mixer : IAudioSessions
    {
        public readonly List<AudioSession> Sessions = [];
        public readonly HashSet<string> Present = [Speakers, Headphones];
        public Action<AudioSession, bool>? Setting;

        public AudioSession Add(uint process, bool muted = false, string device = Speakers, string? identifier = null)
        {
            var session = new AudioSession(device, identifier ?? "id-" + PathOf(process), "instance-" + Guid.NewGuid().ToString("N"), process, muted);
            Sessions.Add(session);
            return session;
        }

        public bool IsMuted(AudioSession session) => Sessions.Single(s => s.Instance == session.Instance).Muted;

        public AudioSession[] List() => Sessions.Where(s => Present.Contains(s.Device)).ToArray();

        public int SetMuted(string device, Func<AudioSession, bool> matches, bool muted)
        {
            int set = 0;
            for (int i = 0; i < Sessions.Count; i++)
            {
                AudioSession session = Sessions[i];
                if (session.Device != device || !Present.Contains(device) || !matches(session)) continue;
                Setting?.Invoke(session, muted);
                Sessions[i] = session with { Muted = muted };
                set++;
            }
            return set;
        }

        public string[] Devices() => Present.ToArray();
    }

    private static string NewJournal() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "muted-programs.json");

    [Fact]
    public void Muting_writes_each_session_down_first_and_leaves_other_programs_and_muted_sessions_alone()
    {
        var mixer = new Mixer();
        AudioSession playing = mixer.Add(1), alreadyMuted = mixer.Add(2, muted: true), other = mixer.Add(OtherProcess);
        string journal = NewJournal();
        var writtenFirst = new List<bool>();
        mixer.Setting = (session, _) => writtenFirst.Add(ProgramSound.Load(journal).Any(saved => saved.Instance == session.Instance));
        var sound = new ProgramSound(mixer, journal, PathOf);

        Assert.False(sound.State(Window));
        Assert.True(sound.Mute(Window));

        Assert.True(mixer.IsMuted(playing));
        Assert.True(mixer.IsMuted(alreadyMuted));
        Assert.False(mixer.IsMuted(other));
        Assert.Equal([true], writtenFirst);
        Assert.Equal([playing.Instance], ProgramSound.Load(journal).Select(saved => saved.Instance));
        Assert.True(sound.State(Window));
        Assert.True(sound.Has(Window));
        Assert.True(sound.AnyMuted);

        Assert.True(sound.Unmute(Window));
        Assert.False(mixer.IsMuted(playing));
        Assert.True(mixer.IsMuted(alreadyMuted)); // Muted before WinScreen touched it, so it stays muted.
        Assert.False(File.Exists(journal));
        Assert.False(sound.AnyMuted);
    }

    [Fact]
    public void A_program_with_no_sound_has_nothing_to_offer()
    {
        var mixer = new Mixer();
        mixer.Add(OtherProcess);
        Assert.Null(new ProgramSound(mixer, null, PathOf).State(Window));
    }

    [Fact]
    public void Two_views_of_one_program_keep_it_muted_until_both_let_go()
    {
        var mixer = new Mixer();
        AudioSession playing = mixer.Add(1);
        var sound = new ProgramSound(mixer, NewJournal(), PathOf);
        Assert.True(sound.Mute(Window));
        Assert.True(sound.Mute(SecondWindow));

        sound.Unmute(Window);
        Assert.True(mixer.IsMuted(playing));
        sound.Unmute(SecondWindow);
        Assert.False(mixer.IsMuted(playing));
    }

    [Fact]
    public void A_session_started_later_is_muted_too_but_one_the_person_unmuted_is_left_alone()
    {
        var mixer = new Mixer();
        AudioSession first = mixer.Add(1);
        var sound = new ProgramSound(mixer, NewJournal(), PathOf);
        Assert.True(sound.Mute(Window));
        int index = mixer.Sessions.FindIndex(s => s.Instance == first.Instance);
        mixer.Sessions[index] = mixer.Sessions[index] with { Muted = false }; // Unmuted in Windows' mixer.
        AudioSession later = mixer.Add(3); // A browser's sound process starting when a page plays.

        sound.Reapply();

        Assert.True(mixer.IsMuted(later));
        Assert.False(mixer.IsMuted(first));
        Assert.Equal(0, sound.UnmuteAll());
        Assert.False(mixer.IsMuted(later));
    }

    [Fact]
    public void Nothing_is_muted_when_it_cannot_be_written_down()
    {
        var mixer = new Mixer();
        AudioSession playing = mixer.Add(1);
        string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        string journal = Path.Combine(folder, "muted-programs.json");
        Directory.CreateDirectory(journal); // A folder where the file should be: saving fails.
        var sound = new ProgramSound(mixer, journal, PathOf);

        Assert.False(sound.Mute(Window));
        Assert.False(mixer.IsMuted(playing));
        Assert.False(sound.AnyMuted);
    }

    [Fact]
    public void Recovery_unmutes_what_was_left_and_finds_a_restarted_program_by_its_identifier()
    {
        var mixer = new Mixer();
        AudioSession left = mixer.Add(1, muted: true);
        AudioSession restarted = mixer.Add(4, muted: true, identifier: "id-restarted");
        AudioSession mutedByPerson = mixer.Add(5, muted: true, identifier: "id-other-instance");
        string journal = NewJournal();
        ProgramSound.Save(journal,
        [
            new MutedSession(Speakers, left.Identifier, left.Instance, Player),
            new MutedSession(Speakers, "id-restarted", "instance-gone", Player),
        ]);

        Assert.Equal(0, ProgramSound.Recover(mixer, journal));

        Assert.False(mixer.IsMuted(left));
        Assert.False(mixer.IsMuted(restarted)); // Windows started it muted because it remembered.
        Assert.True(mixer.IsMuted(mutedByPerson));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void A_session_on_a_device_that_is_not_there_waits_for_the_next_start()
    {
        var mixer = new Mixer();
        AudioSession onHeadphones = mixer.Add(1, device: Headphones);
        string journal = NewJournal();
        var sound = new ProgramSound(mixer, journal, PathOf);
        Assert.True(sound.Mute(Window));
        mixer.Present.Remove(Headphones);

        Assert.True(sound.Unmute(Window));
        Assert.False(sound.AnyMuted);
        Assert.Single(ProgramSound.Load(journal));
        Assert.Equal(1, ProgramSound.Recover(mixer, journal));

        // A new start keeps it written down, and unmutes it once the headphones are back.
        var next = new ProgramSound(mixer, journal, PathOf);
        mixer.Add(1);
        Assert.True(next.Mute(Window));
        Assert.True(next.Unmute(Window));
        Assert.Single(ProgramSound.Load(journal));
        mixer.Present.Add(Headphones);
        Assert.Equal(0, ProgramSound.Recover(mixer, journal));
        Assert.False(mixer.IsMuted(onHeadphones));
    }

    [Fact]
    public void A_damaged_file_is_moved_aside_and_counts_as_empty()
    {
        string journal = NewJournal();
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "{ not a list");

        Assert.Empty(ProgramSound.Load(journal));
        Assert.False(File.Exists(journal));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(journal)!, "*.invalid-*"));
    }

    /// <summary>
    /// The file's shape, written out: a copy of WinScreen reads what an earlier one left, so a renamed or dropped field
    /// would leave a program muted. Every field of the record is in the file and read back.
    /// </summary>
    [Fact]
    public void The_saved_shape_stays_readable()
    {
        const string written = """[{"Device":"d","Identifier":"i","Instance":"n","Program":"C:\\p.exe"}]""";
        var session = new MutedSession("d", "i", "n", @"C:\p.exe");
        Assert.Equal(written, JsonSerializer.Serialize(new[] { session }));
        string journal = NewJournal();
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, written);
        Assert.Equal(session, Assert.Single(ProgramSound.Load(journal)));
        Assert.Equal(typeof(MutedSession).GetProperties().Select(p => p.Name).Order(),
            JsonDocument.Parse(written).RootElement[0].EnumerateObject().Select(p => p.Name).Order());
    }
}
