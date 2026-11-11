using Microsoft.Win32;

namespace WinScreen.Tray;

/// <summary>
/// Whether WinScreen starts when this user signs in. The usual way is an entry under Run; one that starts it as
/// administrator is a scheduled task instead, which only a copy running as administrator can set up or take away.
/// </summary>
public sealed class StartWithWindows(string keyPath = StartWithWindows.RunKey, ITaskScheduler? tasks = null,
    Func<bool>? elevated = null)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TaskName = "WinScreen as administrator";
    private const string ValueName = "WinScreen";

    private readonly ITaskScheduler _tasks = tasks ?? new SchtasksScheduler();
    private readonly Func<bool> _elevated = elevated ?? (() => Native.ProcessInfo.SelfElevated);

    /// <summary>Starting as administrator is chosen.</summary>
    public bool AsAdministrator { get; set; }

    /// <summary>What it starts: this copy of WinScreen.</summary>
    internal string Command { get; init; } = Environment.ProcessPath ?? "WinScreen.exe";

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

    /// <summary>True when it starts by the task, as administrator.</summary>
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
            if (byTask) _tasks.Create(TaskName, Elevation.TaskXml(Command, Elevation.CurrentUser));
            else if (_tasks.Exists(TaskName)) _tasks.Delete(TaskName);
        }
    }
}
