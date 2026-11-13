using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinScreen.Native;

namespace WinScreen.Diagnostics;

/// <summary>What a report needs to know about the PC: Windows, displays and scale, graphics, rights. Nothing personal.</summary>
internal static class StartupFacts
{
    /// <summary>Writes the facts to the log from a worker thread, so start-up doesn't wait for them.</summary>
    public static void LogInBackground() => ThreadPool.QueueUserWorkItem(_ => LogNow());

    internal static void LogNow()
    {
        try
        {
            int build = Environment.OSVersion.Version.Build;
            object? update = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
            LogValue dotnet = LogValue.Known(Environment.Version.ToString()), language = LogValue.Known(
                System.Globalization.CultureInfo.CurrentUICulture.Name);
            Log.Event("system", ("windows", build >= 22000 ? 11 : 10), ("build", build), ("update", update is int ubr ? ubr : 0),
                ("osArchitecture", RuntimeInformation.OSArchitecture), ("processArchitecture", RuntimeInformation.ProcessArchitecture),
                ("administrator", ProcessInfo.SelfElevated), ("dotnet", dotnet), ("language", language));
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle bounds = screens[i].Bounds;
                nint monitor = User32.MonitorFromPoint(new NativePoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, 2);
                Log.Event("display", ("number", i + 1), ("width", bounds.Width), ("height", bounds.Height), ("x", bounds.X),
                    ("y", bounds.Y), ("scale", Dpi.ScaleOf(monitor)), ("main", screens[i].Primary));
            }
            foreach ((string name, bool active) in DisplayAdapters.All())
                Log.Event("graphics", ("name", LogValue.Known(name)), ("drivingDisplay", active));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Swallowed(e);
        }
    }
}
