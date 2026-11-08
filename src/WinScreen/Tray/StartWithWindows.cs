using Microsoft.Win32;

namespace WinScreen.Tray;

/// <summary>Whether WinScreen starts when this user signs in.</summary>
public sealed class StartWithWindows(string keyPath = StartWithWindows.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinScreen";

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

    public bool IsOn
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is not null;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (value)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
