using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace WinScreen.Tray;

public interface ITaskScheduler
{
    bool Exists(string name);
    void Create(string name, string xml);
    void Delete(string name);

    bool Run(string name) => false;

    string? CommandOf(string name) => null;
}

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

internal static class Elevation
{
    // It doesn't ask again if Windows still said no.
    public const string ElevatedArgument = "--elevated";
    public const string AfterArgument = "--after";

    public static bool StartElevatedCopy(bool openSettings = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = $"{ElevatedArgument} {AfterArgument} {Environment.ProcessId}" +
                            (openSettings ? $" {SettingsPageArgument} 0" : ""),
            })?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; } // Including "The operation was canceled by the user."
    }

    public const string SettingsPageArgument = "--settings-page";

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

    public static int? SettingsPage(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(SettingsPageArgument);
        return at >= 0 && at + 1 < arguments.Count && int.TryParse(arguments[at + 1], out int page) && page >= 0 ? page : null;
    }

    public static int? WaitingFor(IReadOnlyList<string> arguments)
    {
        int at = arguments.ToList().IndexOf(AfterArgument);
        return at >= 0 && at + 1 < arguments.Count && int.TryParse(arguments[at + 1], out int id) ? id : null;
    }

    public static void WaitForExit(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit(15000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { Diagnostics.Log.Swallowed(e); } // Gone already.
    }

    // Normal priority (tasks start lower otherwise), no time limit, and on battery too.
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

    // The sign-in task runs as administrator without asking; otherwise Windows asks the person first.
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

    // Windows gives a copy it starts the person's own environment, so a data folder elsewhere is passed as an argument.
    public static string DataFolderArguments() =>
        Environment.GetEnvironmentVariable(AppRoles.DataFolderVariable) is { Length: > 0 } folder
            ? $"{AppRoles.DataFolderArgument} \"{folder}\"" : "";

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
