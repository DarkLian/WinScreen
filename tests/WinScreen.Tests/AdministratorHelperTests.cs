using System.Xml.Linq;
using WinScreen.Diagnostics;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>The ordinary copy and its administrator helper (10-2): who is which, what each acts on, and what they say.</summary>
public sealed class AdministratorHelperTests
{
    [Theory]
    // selfElevated, splitToken, runAsAdministrator, helperWanted, singleAsked → role
    [InlineData(false, true, true, true, false, AppRole.Ordinary)]
    [InlineData(false, false, false, true, false, AppRole.Ordinary)]
    [InlineData(true, true, true, true, false, AppRole.Helper)]
    [InlineData(true, true, true, true, true, AppRole.Elevated)] // The helper's fallback.
    [InlineData(true, true, true, false, false, AppRole.Elevated)] // The file's way back to the one elevated copy.
    [InlineData(true, true, false, true, false, AppRole.Elevated)] // Started as administrator by hand, with the choice off.
    [InlineData(true, false, true, true, false, AppRole.Elevated)] // UAC off: no ordinary copy could run beside it.
    public void A_copy_takes_its_role_from_its_rights_and_the_choices(bool elevated, bool split, bool runAs, bool helper, bool single, AppRole expected) =>
        Assert.Equal(expected, AppRoles.Choose(elevated, split, runAs, helper, single));

    [Fact]
    public void Each_window_is_reached_by_exactly_one_copy_of_the_pair()
    {
        foreach (bool administratorApp in new[] { false, true })
        {
            Assert.NotEqual(AppRoles.Reaches(AppRole.Ordinary, administratorApp), AppRoles.Reaches(AppRole.Helper, administratorApp));
            Assert.True(AppRoles.Reaches(AppRole.Elevated, administratorApp));
        }
        Assert.True(AppRoles.Reaches(AppRole.Helper, true));
        Assert.False(AppRoles.Reaches(AppRole.Helper, false)); // A WinScreen bug can't act with administrator rights on ordinary windows.
    }

    [Fact]
    public void Only_an_ordinary_copy_that_can_elevate_starts_a_helper_and_only_when_asked()
    {
        Assert.True(AppRoles.WantsHelper(AppRole.Ordinary, runAsAdministrator: true, helperWanted: true, canElevate: true));
        Assert.False(AppRoles.WantsHelper(AppRole.Ordinary, true, true, canElevate: false)); // A standard account.
        Assert.False(AppRoles.WantsHelper(AppRole.Ordinary, false, true, true));
        Assert.False(AppRoles.WantsHelper(AppRole.Ordinary, true, false, true));
        Assert.False(AppRoles.WantsHelper(AppRole.Elevated, true, true, true));
        Assert.False(AppRoles.WantsHelper(AppRole.Helper, true, true, true));
    }

    [Fact]
    public void The_helper_keeps_its_own_journal_log_locks_and_window_so_neither_copy_touches_the_others()
    {
        Assert.NotEqual(AppRoles.JournalFile(AppRole.Ordinary), AppRoles.JournalFile(AppRole.Helper));
        Assert.NotEqual(AppRoles.SoundJournalFile(AppRole.Ordinary), AppRoles.SoundJournalFile(AppRole.Helper));
        Assert.NotEqual(AppRoles.LogFile(AppRole.Ordinary), AppRoles.LogFile(AppRole.Helper));
        Assert.NotEqual(AppRoles.MutexName(AppRole.Ordinary), AppRoles.MutexName(AppRole.Helper));
        Assert.NotEqual(AppRoles.RecoveryMutexName(AppRole.Ordinary), AppRoles.RecoveryMutexName(AppRole.Helper));
        Assert.NotEqual(AppRoles.HostTitle(AppRole.Ordinary), AppRoles.HostTitle(AppRole.Helper));
        // The ordinary and the one elevated copy are the same app: one of them at a time, the same files as before 10-2.
        Assert.Equal(AppRoles.MutexName(AppRole.Ordinary), AppRoles.MutexName(AppRole.Elevated));
        Assert.Equal("changed-windows.json", AppRoles.JournalFile(AppRole.Elevated));
        Assert.Equal(Hotkeys.HotkeyHost.WindowTitle, AppRoles.HostTitle(AppRole.Ordinary));
        Assert.Equal("winscreen-administrator.1.log", DiagnosticLog.OlderOf(AppRoles.LogFile(AppRole.Helper)));
        Assert.Equal(DiagnosticLog.OlderFileName, DiagnosticLog.OlderOf(DiagnosticLog.FileName));
    }

    [Fact]
    public void A_watcher_knows_whose_journal_it_watches()
    {
        Assert.Equal(42, RecoveryWatcher.ParentFrom(["--watch", "42"]));
        Assert.Equal(42, RecoveryWatcher.ParentFrom(["--watch", "42", AppRoles.HelperArgument]));
        Assert.Null(RecoveryWatcher.ParentFrom(["--watch"]));
        Assert.Null(RecoveryWatcher.ParentFrom(["--watch", "42", "--something-else"]));
        Assert.Equal(AppRole.Helper, AppRoles.WatcherRole(["--watch", "42", AppRoles.HelperArgument]));
        Assert.Equal(AppRole.Ordinary, AppRoles.WatcherRole(["--watch", "42"]));
    }

    [Fact]
    public void A_data_folder_is_taken_only_as_a_full_path()
    {
        Assert.Equal(@"C:\Data\WinScreen", AppRoles.DataFolderFrom([AppRoles.HelperArgument, AppRoles.DataFolderArgument, @"C:\Data\WinScreen"]));
        Assert.Null(AppRoles.DataFolderFrom([AppRoles.DataFolderArgument, @"relative\folder"]));
        Assert.Null(AppRoles.DataFolderFrom([AppRoles.DataFolderArgument]));
        Assert.Null(AppRoles.DataFolderFrom([AppRoles.HelperArgument]));
    }

    [Fact]
    public void Messages_go_through_whole_and_anything_else_is_dropped()
    {
        var run = new HelperLink.Message(HelperLink.Run, 7, 0x1234, HelperLink.Message.From(new Rectangle(10, 20, 300, 200)));
        HelperLink.Message back = HelperLink.Decode(HelperLink.Encode(run))!;
        Assert.Equal(HelperLink.Run, back.Kind);
        Assert.Equal(7, back.Command);
        Assert.Equal(0x1234, back.Window);
        Assert.Equal(new Rectangle(10, 20, 300, 200), back.DisplayBounds);
        Assert.Null(HelperLink.Decode(HelperLink.Encode(new HelperLink.Message(HelperLink.SettingsSaved)))!.DisplayBounds);
        var alert = new HelperLink.Message(HelperLink.Alert, Title: "Pin", Text: "Line \"one\"\nline two", Icon: 2);
        Assert.Equal(alert.Text, HelperLink.Decode(HelperLink.Encode(alert))!.Text);
        Assert.Null(HelperLink.Decode(null));
        Assert.Null(HelperLink.Decode(""));
        Assert.Null(HelperLink.Decode("not json"));
        Assert.Null(HelperLink.Decode("{\"Kind\":\"\"}"));
        Assert.Null(HelperLink.Decode("{\"Kind\":\"run\",\"Text\":\"" + new string('x', 3 * 1024 * 1024) + "\"}"));
    }

    [Fact]
    public void The_sign_in_task_starts_the_helper_when_it_is_chosen()
    {
        XElement Exec(string xml) => XDocument.Parse(xml).Descendants().First(element => element.Name.LocalName == "Exec");
        XElement plain = Exec(Elevation.TaskXml(@"C:\Apps\WinScreen.exe", @"PC\person"));
        Assert.DoesNotContain(plain.Elements(), element => element.Name.LocalName == "Arguments");
        XElement helper = Exec(Elevation.TaskXml(@"C:\Apps\WinScreen.exe", @"PC\person", AppRoles.HelperArgument));
        Assert.Equal(AppRoles.HelperArgument, helper.Elements().Single(element => element.Name.LocalName == "Arguments").Value);
        Assert.Equal(@"C:\Apps\WinScreen.exe", helper.Elements().Single(element => element.Name.LocalName == "Command").Value);
    }

    [Fact]
    public void Nothing_but_a_copy_of_this_very_program_counts_as_the_other_end()
    {
        Assert.False(HelperLink.IsCopyOfThis(0, elevated: false));
        Assert.False(HelperLink.IsCopyOfThis((uint)Environment.ProcessId, elevated: Native.ProcessInfo.SelfElevated)); // Itself.
        // Another program (this test host's parent shell, or any process that isn't WinScreen.exe from this folder).
        uint other = (uint)System.Diagnostics.Process.GetProcessesByName("explorer").Select(p => p.Id).FirstOrDefault();
        if (other != 0) Assert.False(HelperLink.IsCopyOfThis(other, elevated: false));
    }
}
