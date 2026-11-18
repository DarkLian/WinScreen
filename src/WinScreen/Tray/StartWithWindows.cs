using Microsoft.Win32;

namespace WinScreen.Tray;

// A start as administrator is a scheduled task instead of a Run entry, which only an elevated copy can set up or take
// away.
public sealed class StartWithWindows(string keyPath = StartWithWindows.RunKey, ITaskScheduler? tasks = null,
    Func<bool>? elevated = null)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TaskName = "WinScreen as administrator";
    private const string ValueName = "WinScreen";

    private readonly ITaskScheduler _tasks = tasks ?? new SchtasksScheduler();

    internal ITaskScheduler Tasks => _tasks;
    private readonly Func<bool> _elevated = elevated ?? (() => Native.ProcessInfo.SelfElevated);

    public bool AsAdministrator { get; set; }

    internal string Command { get; init; } = Environment.ProcessPath ?? "WinScreen.exe";

    public string TaskArguments { get; set; } = "";

    // The task is the elevated helper's to make or take away; this copy only writes the Run entry.
    public bool TaskByHelper { get; set; }

    internal object? ReadValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    internal void RestoreValue(object? value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (value is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, value);
    }

    internal bool ByTask => _tasks.Exists(TaskName);

    public bool IsOn
    {
        get => ReadValue() is not null || ByTask;
        set
        {
            // Without administrator rights the task can be neither made nor removed, so what a copy running as
            // administrator set up stays as it is until one runs again.
            if (AsAdministrator && !_elevated()) return;
            bool byTask = value && AsAdministrator;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (value && !byTask) key.SetValue(ValueName, $"\"{Command}\"");
                else key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            if (TaskByHelper && !_elevated()) return;
            if (byTask) _tasks.Create(TaskName, Elevation.TaskXml(Command, Elevation.CurrentUser, TaskArguments));
            else if (_tasks.Exists(TaskName)) _tasks.Delete(TaskName);
        }
    }
}
