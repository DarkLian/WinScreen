using System.Reflection;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Mute from a view's menu with Windows' real mixer (roadmap step 8.3): a test window plays silence, so it has a sound
/// session nobody hears. A PC with no output device has no sessions at all, and the test says so and returns.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class ProgramSoundRealTests
{
    private static readonly Rectangle Spot = new(140, 140, 400, 300);

    internal static AudioSession[] SessionsOf(int process) =>
        AudioSessions.Instance.List().Where(session => session.ProcessId == process).ToArray();

    /// <summary>The test window's sessions once it plays, or empty when this PC can't play sound.</summary>
    internal static AudioSession[] StartSound(TargetProcess target)
    {
        if (AudioSessions.Instance.Devices().Length == 0) return [];
        target.PlaySilence();
        AudioSession[] sessions = [];
        Desktop.WaitFor(() => (sessions = SessionsOf(target.ProcessId)).Length > 0, 5000);
        return sessions;
    }

    internal static bool Muted(TargetProcess target) => SessionsOf(target.ProcessId).Any(session => session.Muted);

    internal static void SetMuted(TargetProcess target, bool muted)
    {
        foreach (string device in AudioSessions.Instance.Devices())
            AudioSessions.Instance.SetMuted(device, session => session.ProcessId == target.ProcessId, muted);
    }

    [Fact]
    public void A_view_mutes_its_windows_program_and_put_back_closing_and_an_earlier_mute_are_each_respected()
    {
        using var target = new TargetProcess(Spot);
        if (StartSound(target).Length == 0) { Skipped.Because("No output device here, so no sound session."); return; }
        string folder = Path.Combine(Path.GetTempPath(), "WinScreenSound-" + Path.GetRandomFileName());
        string journal = Path.Combine(folder, "muted-programs.json");
        try
        {
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                var controller = new WindowController([], new ChangeJournal(Path.Combine(folder, "journal.json")));
                var settings = new AppSettings
                {
                    ShowToolbar = false, ShowNotifications = false, WelcomeShown = true,
                    Hotkeys = CommandCatalog.All.ToDictionary(command => command, _ => ""),
                };
                var sound = new ProgramSound(AudioSessions.Instance, journal);
                using var app = new TrayApplication(controller, settings, Path.Combine(folder, "settings.json"), sound);
                MethodInfo open = typeof(TrayApplication).GetMethod("OpenViewOf", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo putBack = typeof(TrayApplication).GetMethod("PutBack", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(nint)])!;
                PipWindow Open()
                {
                    var view = (PipWindow)open.Invoke(app, [target.Handle])!;
                    Application.DoEvents();
                    return view;
                }
                try
                {
                    // Put back unmutes and closes the view.
                    PipWindow view = Open();
                    Assert.False(view.SoundState!());
                    Assert.True(view.SetSound!(true));
                    Assert.True(Muted(target));
                    Assert.True(view.SoundState!());
                    Assert.NotEmpty(ProgramSound.Load(journal)); // Written down.
                    putBack.Invoke(app, [target.Handle]);
                    Application.DoEvents();
                    Assert.False(Muted(target), "Put back left the program muted.");
                    Assert.True(view.IsDisposed);
                    Assert.False(File.Exists(journal));

                    // Closing the view unmutes.
                    view = Open();
                    Assert.True(view.SetSound!(true));
                    Assert.True(Muted(target));
                    view.Close();
                    Application.DoEvents();
                    Assert.False(Muted(target), "Closing the view left the program muted.");

                    // Turning it off from the menu unmutes.
                    view = Open();
                    Assert.True(view.SetSound!(true));
                    Assert.True(view.SetSound!(false));
                    Assert.False(Muted(target));
                    view.Close();

                    // Muted before: it stays muted.
                    SetMuted(target, true);
                    view = Open();
                    Assert.True(view.SetSound!(true));
                    view.Close();
                    Application.DoEvents();
                    Assert.True(Muted(target), "A program muted before WinScreen touched it was unmuted.");
                }
                finally
                {
                    SetMuted(target, false);
                    app.ExitThread();
                }
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
