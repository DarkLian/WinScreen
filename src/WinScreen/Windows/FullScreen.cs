using System.Runtime.InteropServices;
using WinScreen.Settings;

namespace WinScreen.Windows;

public enum FullScreenState { None, Busy, Exclusive, Presentation }

/// <summary>What a command should do while a full-screen app is running.</summary>
public enum FullScreenAction { Run, CloseLatestView, Refuse, ExplainExclusive }

/// <summary>Tells when a game or other full-screen app is running.</summary>
public static partial class FullScreen
{
    private const int NotPresent = 1;
    private const int Busy = 2;
    private const int RunningFullScreenGame = 3;
    private const int PresentationMode = 4;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    public static FullScreenState Current()
    {
        if (SHQueryUserNotificationState(out int state) != 0)
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

    /// <summary>True while the PC is locked, on a screen saver, or switched to another user.</summary>
    public static bool IsAway() => SHQueryUserNotificationState(out int state) == 0 && state == NotPresent;
}

/// <summary>
/// Which commands go ahead during a full-screen app. Pausing stops new changes, but anything that only
/// removes one of WinScreen's own views always works, so a view can never be left stuck over a game.
/// </summary>
public static class FullScreenPolicy
{
    /// <param name="targetHasView">The command would take away a view of the window it was aimed at.</param>
    /// <param name="anyViewOpen">A view is open somewhere. A hotkey pressed in a game is aimed at the game, not at the view.</param>
    public static FullScreenAction Decide(AppCommand command, FullScreenState state, bool pause, bool allowPip,
        bool targetHasView, bool anyViewOpen, bool targetIsBorderless = false)
    {
        if (command is AppCommand.RestoreAll or AppCommand.RestoreWindow ||
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
