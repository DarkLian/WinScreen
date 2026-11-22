using WinScreen.Settings;

namespace WinScreen.Tray;

internal enum CommandTarget
{
    Window,
    NoWindow,
    // These only take things away, so they run at any time.
    EveryWindow,
    UnderPointer,
    // The view in front, or the view of the window in front (TrayApplication.ViewFor).
    View,
}

internal enum CommandOutcome
{
    // A failure while running adds a second line, Failed.
    Started,
    ClosedLatestView,
    ExclusiveFullScreen,
    PausedInFullScreen,
    Refused,
    NotTurnedOn,
    Failed,
}

// One per thing Put back has to take away; the orders in PutBackOrder are the only place the order is written.
internal enum WindowPart
{
    CropChoice,
    View,
    Sound,
    Tile,
    FilterDark,
    ContentEffect,
    Anchor,
    WindowChanges,
}

internal static class PutBackOrder
{
    // WinScreen's own surfaces go first, so the window is uncovered and shown before its own changes are undone.
    public static readonly WindowPart[] PutBack =
    [
        WindowPart.CropChoice, WindowPart.View, WindowPart.Sound, WindowPart.Tile, WindowPart.FilterDark, WindowPart.ContentEffect,
        WindowPart.Anchor, WindowPart.WindowChanges,
    ];

    // The windows go back first: if that fails WinScreen stays running, its views then the way back to a hidden window.
    public static readonly WindowPart[] Exit =
    [
        WindowPart.CropChoice, WindowPart.WindowChanges, WindowPart.View, WindowPart.Sound, WindowPart.Tile, WindowPart.FilterDark,
        WindowPart.ContentEffect, WindowPart.Anchor,
    ];
}

// Name, icon, experimental flag and full-screen class stay where every surface already reads them (CommandCatalog,
// MenuParts.IconOf, FullScreenPolicy).
internal sealed record CommandDefinition(AppCommand Command, Action<nint, Rectangle?> Run)
{
    public CommandTarget Target { get; init; } = CommandTarget.Window;

    public Func<nint, bool>? IsOn { get; init; }

    // Run in full screen, on refused windows and with the switch off.
    public bool Recovers { get; init; }

    public WindowPart[] Undoes { get; init; } = [];

    // A script that ran it is told how it went once the menu or dialog is open, not once it is closed.
    public Func<nint, bool>? Opens { get; init; }
}

internal sealed class CommandTable
{
    private readonly Dictionary<AppCommand, CommandDefinition> _definitions = [];

    public CommandTable(params IEnumerable<CommandDefinition>[] areas)
    {
        foreach (CommandDefinition definition in areas.SelectMany(area => area))
        {
            if (!_definitions.TryAdd(definition.Command, definition))
                throw new InvalidOperationException($"{definition.Command} is defined twice.");
        }
    }

    public IEnumerable<CommandDefinition> Definitions => _definitions.Values;

    public bool TryGet(AppCommand command, out CommandDefinition definition) =>
        _definitions.TryGetValue(command, out definition!);

    public bool IsOn(AppCommand command, nint window) =>
        _definitions.TryGetValue(command, out CommandDefinition? definition) && definition.IsOn?.Invoke(window) == true;
}
