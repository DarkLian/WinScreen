using System.Runtime.InteropServices;

namespace WinScreen.Windows;

/// <summary>Tells when a game or other full-screen app is running.</summary>
public static partial class FullScreen
{
    private const int Busy = 2;
    private const int RunningFullScreenGame = 3;
    private const int PresentationMode = 4;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    public static bool IsActive()
    {
        return SHQueryUserNotificationState(out int state) == 0
               && state is Busy or RunningFullScreenGame or PresentationMode;
    }
}
