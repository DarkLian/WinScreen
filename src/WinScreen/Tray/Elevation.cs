using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace WinScreen.Tray;

/// <summary>Scheduled tasks: the one way to start a program as administrator at sign-in without asking each time.</summary>
public interface ITaskScheduler
{
    bool Exists(string name);
    void Create(string name, string xml);
    void Delete(string name);
}

/// <summary>Windows' own Task Scheduler, through schtasks. Creating a task that runs as administrator needs one.</summary>
internal sealed class SchtasksScheduler : ITaskScheduler
{
    public bool Exists(string name) => Run("/Query", "/TN", name) == 0;

    public void Create(string name, string xml)
    {
        string file = Path.Combine(Path.GetTempPath(), $"winscreen-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode); // The encoding the XML says it has.
            if (Run("/Create", "/TN", name, "/XML", file, "/F") != 0)
                throw new IOException(Localization.UiText.T("errors.admin.task"));
        }
        finally { File.Delete(file); }
    }

    public void Delete(string name)
    {
        if (Exists(name) && Run("/Delete", "/TN", name, "/F") != 0)
            throw new IOException(Localization.UiText.T("errors.admin.task"));
    }

    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15000) ? process.ExitCode : -1;
        }
        catch (Win32Exception) { return -1; }
    }
}

/// <summary>Running WinScreen as administrator, so it can also change administrator apps' windows.</summary>
internal static class Elevation
{
    /// <summary>Given to a copy started as administrator, which doesn't ask again if Windows still said no.</summary>
    public const string ElevatedArgument = "--elevated";
    /// <summary>Followed by a process id: wait for that copy to close before starting.</summary>
    public const string AfterArgument = "--after";

    /// <summary>
    /// Starts a copy as administrator that takes over once this one has closed. Windows asks the person first;
    /// false when they said no or it couldn't start.
    /// </summary>
    public static bool StartElevatedCopy()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = $"{ElevatedArgument} {AfterArgument} {Environment.ProcessId}",
            })?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; } // Including "The operation was canceled by the user."
    }

    /// <summary>The copy this one should wait for, from its arguments.</summary>
    public static int? WaitingFor(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(AfterArgument);
        return at >= 0 && at + 1 < arguments.Count && int.TryParse(arguments[at + 1], out int id) ? id : null;
    }

    /// <summary>Waits a while for the copy that started this one to close, so only one runs at a time.</summary>
    public static void WaitForExit(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit(15000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { } // Gone already.
    }

    /// <summary>
    /// The task that starts WinScreen as administrator when this person signs in: at normal priority (tasks start
    /// lower otherwise), with no time limit, and on battery too.
    /// </summary>
    public static string TaskXml(string command, string user)
    {
        string who = SecurityElement.Escape(user), what = SecurityElement.Escape(command);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts WinScreen as administrator when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{who}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{who}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{what}</Command></Exec></Actions>
            </Task>
            """;
    }

    public static string CurrentUser => WindowsIdentity.GetCurrent().Name;
}
