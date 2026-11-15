using Microsoft.Win32;
using WinScreen.Native;

namespace WinScreen.Tests.Support;

/// <summary>
/// A virtual desktop made for one test with Ctrl+Win+D, and closed again with Ctrl+Win+F4 only while it is the one in
/// front. The desktop the test started on is shown again at the end, and the person's own desktops are left as they
/// were, which the end checks.
/// </summary>
internal sealed class TestDesktop : IDisposable
{
    private const string Key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private readonly string _before;

    public TestDesktop()
    {
        _before = Ids();
        Home = VirtualDesktops.CurrentDesktop() ?? throw new InvalidOperationException("The desktop in front can't be read.");
        Desktop.Press(Keys.D, Keys.ControlKey, Keys.LWin);
        Desktop.WaitFor(() => VirtualDesktops.CurrentDesktop() is { } now && now != Home, 5000, Application.DoEvents);
        Made = VirtualDesktops.CurrentDesktop() ?? Guid.Empty;
        if (Made == Home || Made == Guid.Empty) throw new InvalidOperationException("Ctrl+Win+D made no desktop.");
        Pump(1500);
    }

    public Guid Home { get; }
    public Guid Made { get; }

    public void GoHome() => GoTo(Home);
    public void GoToMade() => GoTo(Made);

    private static void GoTo(Guid desktop)
    {
        foreach (Keys direction in new[] { Keys.Left, Keys.Right })
            for (int press = 0; press < 8 && VirtualDesktops.CurrentDesktop() != desktop; press++)
            {
                Desktop.Press(direction, Keys.ControlKey, Keys.LWin);
                Pump(1200);
            }
        if (VirtualDesktops.CurrentDesktop() != desktop) throw new InvalidOperationException($"Couldn't get to desktop {desktop}.");
        Pump(800);
    }

    public static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static string Ids() => Registry.GetValue(Key, "VirtualDesktopIDs", null) is byte[] ids ? Convert.ToHexString(ids) : "";

    public void Dispose()
    {
        if (VirtualDesktops.Exists(Made) == true)
        {
            GoTo(Made);
            if (VirtualDesktops.CurrentDesktop() == Made)
            {
                Desktop.Press(Keys.F4, Keys.ControlKey, Keys.LWin);
                Desktop.WaitFor(() => VirtualDesktops.Exists(Made) == false, 5000, Application.DoEvents);
            }
        }
        GoTo(Home);
        if (Ids() != _before) throw new InvalidOperationException("The desktops aren't as they were before the test.");
    }
}
