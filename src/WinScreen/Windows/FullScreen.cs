using System.Runtime.InteropServices;
using WinScreen.Settings;

namespace WinScreen.Windows;

public enum FullScreenState { None, Busy, Exclusive, Presentation }

public enum FullScreenAction { Run, CloseLatestView, Refuse, ExplainExclusive }

public static partial class FullScreen
{
    private const int NotPresent = 1;
    private const int Busy = 2;
    private const int RunningFullScreenGame = 3;
    private const int PresentationMode = 4;

    public static FullScreenState Current()
    {
        if (Native.Shell32.SHQueryUserNotificationState(out int state) != 0)
            return FullScreenState.None;
        return state switch
        {
            Busy => FullScreenState.Busy,
            RunningFullScreenGame => FullScreenState.Exclusive,
            PresentationMode => FullScreenState.Presentation,
            _ => FullScreenState.None,
        };
    }

    public static bool IsActive() => Current() != FullScreenState.None;

    public static bool IsAway() => Native.Shell32.SHQueryUserNotificationState(out int state) == 0 && state == NotPresent;
}

// Anything that only removes one of WinScreen's own views always works, so a view can never be left stuck over a game.
public static class FullScreenPolicy
{
    // Windows calls any foreground window that covers its display full screen, and Fill display makes one; a window
    // WinScreen filled itself is not a game to step back for. A game's exclusive mode and a presentation still count.
    public static FullScreenState Seen(FullScreenState reported, nint foreground, Func<nint, bool> filledByWinScreen) =>
        reported == FullScreenState.Busy && foreground != 0 && filledByWinScreen(foreground) ? FullScreenState.None : reported;

    // anyViewOpen: a hotkey pressed in a game is aimed at the game, not at the view.
    public static FullScreenAction Decide(AppCommand command, FullScreenState state, bool pause, bool allowPip,
        bool targetHasView, bool anyViewOpen, bool targetIsBorderless = false)
    {
        if (command is AppCommand.RestoreAll or AppCommand.RestoreWindow || Settings.CommandCatalog.Everywhere.Contains(command) ||
            (targetIsBorderless && command is AppCommand.ToggleBorderless or AppCommand.ToggleBorderlessFill))
            return FullScreenAction.Run;
        if (state == FullScreenState.Exclusive && command is AppCommand.ToggleBorderless or AppCommand.ToggleBorderlessFill)
            return FullScreenAction.Refuse;
        if (state == FullScreenState.None || !pause || targetHasView)
            return FullScreenAction.Run;
        if (command == AppCommand.PickPipSource)
            return allowPip && state == FullScreenState.Busy ? FullScreenAction.Run : FullScreenAction.Refuse;
        if (command != AppCommand.OpenPip)
            return FullScreenAction.Refuse;
        if (allowPip && state == FullScreenState.Busy)
            return FullScreenAction.Run;
        if (anyViewOpen)
            return FullScreenAction.CloseLatestView;
        return state == FullScreenState.Exclusive ? FullScreenAction.ExplainExclusive : FullScreenAction.Refuse;
    }
}
