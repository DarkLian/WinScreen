using System.Xml.Linq;
using Microsoft.Win32;
using WinScreen.Settings;
using WinScreen.Tray;
using Xunit;

namespace WinScreen.Tests;

/// <summary>How WinScreen starts at sign-in once it runs as administrator, checked against a stand-in Task Scheduler.</summary>
public sealed class AdministratorStartTests : IDisposable
{
    private readonly string _key = @"Software\WinScreenTests\Admin" + Guid.NewGuid().ToString("N");
    private readonly FakeTasks _tasks = new();
    private bool _elevated;

    private StartWithWindows Startup(bool asAdministrator) =>
        new(_key, _tasks, () => _elevated) { AsAdministrator = asAdministrator, Command = @"C:\Apps\Win Screen\WinScreen.exe" };

    private object? RunValue => new StartWithWindows(_key, _tasks).ReadValue();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    private sealed class FakeTasks : ITaskScheduler
    {
        public Dictionary<string, string> Made { get; } = [];
        public bool Exists(string name) => Made.ContainsKey(name);
        public void Create(string name, string xml) => Made[name] = xml;
        public void Delete(string name) => Made.Remove(name);
    }

    [Fact]
    public void As_administrator_it_starts_by_a_task_instead_of_the_Run_entry()
    {
        _elevated = true;
        Startup(false).IsOn = true;
        Assert.NotNull(RunValue);

        StartWithWindows admin = Startup(true);
        admin.IsOn = true;
        Assert.Null(RunValue);
        Assert.True(_tasks.Exists(StartWithWindows.TaskName));
        Assert.True(admin.IsOn);
    }

    [Fact]
    public void Without_administrator_rights_nothing_an_administrator_copy_set_up_is_touched()
    {
        _elevated = true;
        Startup(true).IsOn = true;
        _elevated = false;

        Startup(true).IsOn = false;

        Assert.True(_tasks.Exists(StartWithWindows.TaskName));
        Assert.Null(RunValue);
    }

    [Fact]
    public void Turning_administrator_off_brings_back_the_Run_entry_and_removes_the_task()
    {
        _elevated = true;
        Startup(true).IsOn = true;

        Startup(false).IsOn = true;

        Assert.False(_tasks.Exists(StartWithWindows.TaskName));
        Assert.Equal("\"C:\\Apps\\Win Screen\\WinScreen.exe\"", RunValue);

        Startup(false).IsOn = false;
        Assert.Null(RunValue);
    }

    [Fact]
    public void The_task_starts_this_person_as_administrator_at_normal_priority()
    {
        XDocument task = XDocument.Parse(Elevation.TaskXml(@"C:\A & B\WinScreen.exe", @"PC\Ann <test>"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("HighestAvailable", task.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal(@"PC\Ann <test>", task.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal(@"C:\A & B\WinScreen.exe", task.Descendants(ns + "Command").Single().Value);
        Assert.Equal("4", task.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", task.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
    }

    /// <summary>
    /// Windows' own Task Scheduler takes the task as written. It needs administrator rights, so it runs only then,
    /// under a name of its own that is removed straight away.
    /// </summary>
    [Fact]
    public void The_real_Task_Scheduler_accepts_the_task()
    {
        if (!Native.ProcessInfo.SelfElevated) return;
        const string name = "WinScreen test task";
        var tasks = new SchtasksScheduler();
        try
        {
            tasks.Create(name, Elevation.TaskXml(@"C:\Windows\System32\rundll32.exe", Elevation.CurrentUser));
            Assert.True(tasks.Exists(name));
        }
        finally { tasks.Delete(name); }
        Assert.False(tasks.Exists(name));
    }

    [Theory]
    [InlineData(new[] { "--elevated", "--after", "1234" }, 1234)]
    [InlineData(new[] { "--after" }, null)]
    [InlineData(new string[0], null)]
    public void A_copy_started_as_administrator_waits_for_the_one_before(string[] arguments, int? expected) =>
        Assert.Equal(expected, Elevation.WaitingFor(arguments));

    [Fact]
    public void Running_as_administrator_stays_with_this_computer_when_settings_move()
    {
        string file = Path.Combine(Path.GetTempPath(), $"winscreen-admin-transfer-{Guid.NewGuid():N}.json");
        try
        {
            SettingsTransfer.Export(new AppSettings { RunAsAdministrator = true }, file);
            Assert.DoesNotContain(nameof(AppSettings.RunAsAdministrator), File.ReadAllText(file));
            Assert.False(SettingsTransfer.Import(file, new AppSettings()).RunAsAdministrator);
        }
        finally { File.Delete(file); }
    }
}

/// <summary>
/// A Task Scheduler with no tasks, so tests of starting with Windows don't see the task a copy of WinScreen running as
/// administrator on this PC made.
/// </summary>
internal sealed class NoTasks : ITaskScheduler
{
    public static readonly NoTasks Instance = new();
    public bool Exists(string name) => false;
    public void Create(string name, string xml) { }
    public void Delete(string name) { }
}
