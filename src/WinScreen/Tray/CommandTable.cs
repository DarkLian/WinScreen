using WinScreen.Settings;

namespace WinScreen.Tray;

/// <summary>What a command works on.</summary>
internal enum CommandTarget
{
    /// <summary>One window: the active one, the one under a menu, or the one a view stands for.</summary>
    Window,
    /// <summary>No window in particular, such as choosing what a new view shows or putting every window back.</summary>
    NoWindow,
    /// <summary>Every window at once. These only take things away, so they run at any time.</summary>
    EveryWindow,
    /// <summary>The window under the pointer, found when the command runs.</summary>
    UnderPointer,
}

/// <summary>How a command went, for the log and for anyone who asked for it (a script, a rule).</summary>
internal enum CommandOutcome
{
    /// <summary>It got past every check and ran. A failure while running adds a second line, Failed.</summary>
    Started,
    ClosedLatestView,
    ExclusiveFullScreen,
    PausedInFullScreen,
    Refused,
    NotTurnedOn,
    Failed,
}

/// <summary>
/// One command: what it does and how to tell whether it is on for a window. Its name, icon, experimental flag and
/// full-screen class stay where every surface already reads them (<see cref="CommandCatalog"/>,
/// <see cref="MenuParts.IconOf"/>, <see cref="Windows.FullScreenPolicy"/>).
/// </summary>
internal sealed record CommandDefinition(AppCommand Command, Action<nint, Rectangle?> Run)
{
    public CommandTarget Target { get; init; } = CommandTarget.Window;

    /// <summary>True while the command's change is in place on a window; null for a command with no state.</summary>
    public Func<nint, bool>? IsOn { get; init; }

    /// <summary>Put back and Put every window back: they run in full screen, on refused windows and with the switch off.</summary>
    public bool Recovers { get; init; }
}

/// <summary>Every command, defined once, gathered from the lists each area contributes.</summary>
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
