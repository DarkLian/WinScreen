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

    /// <summary>Runs a task now, as it would run at its trigger. False when it couldn't.</summary>
    bool Run(string name) => false;

    /// <summary>The program a task starts, or null when there is no such task or it can't be read.</summary>
    string? CommandOf(string name) => null;
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

    public bool Run(string name) => Run("/Run", "/TN", name) == 0;

    public string? CommandOf(string name)
    {
        if (Run(out string xml, "/Query", "/TN", name, "/XML") != 0) return null;
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value.Trim();
        }
        catch (System.Xml.XmlException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            return null;
        }
    }

    private static int Run(params string[] arguments) => Run(out _, arguments);

    private static int Run(out string output, params string[] arguments)
    {
        output = "";
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            error.GetAwaiter().GetResult();
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

    /// <summary>Followed by a page number: open Settings on that page once started.</summary>
    public const string SettingsPageArgument = "--settings-page";

    /// <summary>
    /// Starts a copy with this one's rights that takes over once this one has closed, and opens Settings on
    /// <paramref name="settingsPage"/>. It doesn't ask for administrator rights again. False when it couldn't start.
    /// </summary>
    public static bool StartFreshCopy(int settingsPage)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (string argument in new[] { ElevatedArgument, AfterArgument, Environment.ProcessId.ToString(),
                         SettingsPageArgument, settingsPage.ToString() })
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Win32Exception failure)
        {
            Diagnostics.Log.Swallowed(failure);
            return false;
        }
    }

    /// <summary>The Settings page to open once started, from the arguments.</summary>
    public static int? SettingsPage(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(SettingsPageArgument);
        return at >= 0 && at + 1 < arguments.Count && int.TryParse(arguments[at + 1], out int page) && page >= 0 ? page : null;
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
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { Diagnostics.Log.Swallowed(e); } // Gone already.
    }

    /// <summary>
    /// The task that starts WinScreen as administrator when this person signs in: at normal priority (tasks start
    /// lower otherwise), with no time limit, and on battery too.
    /// </summary>
    /// <param name="arguments">What it is given: the administrator helper's argument with the helper chosen (10-2).</param>
    public static string TaskXml(string command, string user, string arguments = "")
    {
        string who = SecurityElement.Escape(user), what = SecurityElement.Escape(command);
        string given = arguments.Length == 0 ? "" : $"<Arguments>{SecurityElement.Escape(arguments)}</Arguments>";
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
              <Actions Context="Author"><Exec><Command>{what}</Command>{given}</Exec></Actions>
            </Task>
            """;
    }

    public static string CurrentUser => WindowsIdentity.GetCurrent().Name;

    /// <summary>
    /// Starts the administrator helper (10-2): by the sign-in task when there is one, which Windows runs as administrator
    /// without asking; otherwise Windows asks the person first. False when they said no or it couldn't start.
    /// </summary>
    public static bool StartHelper(ITaskScheduler tasks)
    {
        // The task only when it starts this very program: another copy (an installed one, beside a portable one) would
        // start its own helper. A copy with its data elsewhere (tests) tells its helper where, which a task can't.
        string? dataFolder = Environment.GetEnvironmentVariable(AppRoles.DataFolderVariable);
        if (dataFolder is null && string.Equals(tasks.CommandOf(StartWithWindows.TaskName), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
            tasks.Run(StartWithWindows.TaskName))
            return true;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = (AppRoles.HelperArgument + " " + DataFolderArguments()).Trim(),
            })?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; } // Including "The operation was canceled by the user."
    }

    /// <summary>
    /// Where this copy's data is, for a copy it starts through Windows (which gives it the person's own environment):
    /// empty for the usual place.
    /// </summary>
    public static string DataFolderArguments() =>
        Environment.GetEnvironmentVariable(AppRoles.DataFolderVariable) is { Length: > 0 } folder
            ? $"{AppRoles.DataFolderArgument} \"{folder}\"" : "";

    /// <summary>
    /// From the helper, when no ordinary copy could be started as the person: the one elevated copy of before
    /// (<see cref="AppRoles.SingleArgument"/>), so WinScreen runs either way. False when it didn't start.
    /// </summary>
    public static bool StartSingleCopy()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(AppRoles.SingleArgument);
            start.ArgumentList.Add(ElevatedArgument);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Win32Exception failure)
        {
            Diagnostics.Log.Swallowed(failure);
            return false;
        }
    }
}
