using static WinScreen.Localization.UiText;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Tray;

/// <summary>
/// What each command does (roadmap P1-6). Every area gives its own list; <see cref="CommandTable"/> checks that
/// each command is defined once, and <see cref="RunOnDisplay"/> runs them all the same way. A new command is a new
/// line here plus its name in <see cref="CommandCatalog"/>.
/// </summary>
public sealed partial class TrayApplication
{
    /// <summary>Every command, by area. Built without touching any state, so a test can list it.</summary>
    internal IEnumerable<CommandDefinition>[] CommandAreas() =>
        [WindowCommands(), ArrangeCommands(), ViewCommands(), EffectCommands(), AnchorCommands(), EverywhereCommands(), RecoveryCommands()];

    private static CommandDefinition Define(AppCommand command, Action<nint> run) => new(command, (window, _) => run(window));

    private static bool Always(nint window) => true;

    /// <summary>Changes WinScreen makes to the window itself, all through <see cref="WindowController"/>.</summary>
    private IEnumerable<CommandDefinition> WindowCommands() =>
    [
        Define(AppCommand.TogglePin, window =>
            Confirm(_windows.TogglePin(window) ? T("status.pinned") : T("status.unpinned"), WindowInfo.Title(window)))
            with { IsOn = window => _windows.IsPinned(window), Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.OpacityUp, window => ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(1)))) with { Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.OpacityDown, window => ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(-1)))) with { Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.ToggleDefaultOpacity, window => ShowOpacity(window, _windows.ToggleDefaultOpacity(window)))
            with { IsOn = window => _windows.OpacityOf(window) < 255, Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.ToggleClickThrough, window =>
        {
            if (_windows.ToggleClickThrough(window))
                Alert(T("status.clickThroughOn"), T("window.clickThrough.recoveryHelp"), ToolTipIcon.Info);
            else
                Confirm(T("status.clickThroughOff"), WindowInfo.Title(window));
        }) with { IsOn = window => _windows.IsClickThrough(window), Undoes = [WindowPart.WindowChanges] },
        new CommandDefinition(AppCommand.ToggleBorderless, (window, display) => _windows.ToggleBorderless(window, false, display))
            { IsOn = window => _windows.IsBorderless(window) && !_windows.IsBorderlessFilled(window), Undoes = [WindowPart.WindowChanges] },
        new CommandDefinition(AppCommand.ToggleBorderlessFill, (window, display) => _windows.ToggleBorderless(window, true, display))
            { IsOn = window => _windows.IsBorderlessFilled(window), Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.ToggleDarkTitleBar, window => _windows.ToggleDarkTitleBar(window))
            with { IsOn = window => _windows.IsDarkTitleBar(window), Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.ToggleRollUp, window => _windows.ToggleRollUp(window))
            with { IsOn = window => _windows.IsRolledUp(window), Undoes = [WindowPart.WindowChanges] },
        Define(AppCommand.SendToBottom, window => _windows.SendToBottom(window)),
        Define(AppCommand.ShowWindowInfo, ShowWindowInfo) with { Opens = Always },
        Define(AppCommand.SaveWindowSetup, window => SaveSetup(window))
            with { IsOn = window => _settings.SetupForWindow(window) is not null },
    ];

    /// <summary>Where a window is: the nine places, another display, exact bounds, and back.</summary>
    private IEnumerable<CommandDefinition> ArrangeCommands() =>
    [
        Define(AppCommand.Arrange, window => ShowArrangeMenu(window)) with { Opens = Always },
        .. Enum.GetValues<AppCommand>().Where(command => CommandCatalog.AnchorOf(command) is not null).Select(command =>
            Define(command, window => _placer.Align(window, CommandCatalog.AnchorOf(command)!.Value))),
        Define(AppCommand.MoveToNextDisplay, window => _placer.MoveToNextDisplay(window)),
        Define(AppCommand.PlaceExactly, window => PlaceExactly(window)) with { Opens = Always },
        Define(AppCommand.RestorePlacement, window =>
        {
            if (!_placer.RestorePrior(window))
                Alert(CommandCatalog.Name(AppCommand.RestorePlacement), T("errors.placement.unchanged"), ToolTipIcon.Info);
        }),
        Define(AppCommand.ShowWindowMenu, _ =>
        {
            Point point = Cursor.Position;
            nint underPointer = Native.User32.GetAncestor(
                Native.User32.WindowFromPoint(new Native.NativePoint { X = point.X, Y = point.Y }), Native.User32.GA_ROOT);
            ShowWindowMenu(underPointer, point);
        }) with { Target = CommandTarget.UnderPointer, Opens = Always },
    ];

    /// <summary>Views of a window or of the screen, Shrink tiles and crops.</summary>
    private IEnumerable<CommandDefinition> ViewCommands() =>
    [
        Define(AppCommand.OpenPip, window =>
        {
            if (_pips.TryGetValue(window, out var existing)) existing.Close();
            else OpenViewOf(window);
        }) with { IsOn = window => _pips.ContainsKey(window), Undoes = [WindowPart.View], Opens = window => !_pips.ContainsKey(window) && Native.User32.IsIconic(window) },
        Define(AppCommand.PickPipSource, _ => ShowPicker()) with { Target = CommandTarget.NoWindow, Opens = Always },
        Define(AppCommand.ToggleShrink, window => Shrink(window)) with { IsOn = window => _shrunk.ContainsKey(window), Undoes = [WindowPart.Tile] },
        Define(AppCommand.ToggleCrop, window => ToggleCrop(window)) with { IsOn = window => _windows.IsCropped(window), Undoes = [WindowPart.CropChoice, WindowPart.WindowChanges] },
    ];

    /// <summary>Dark and Glass, whichever kind of each Settings chooses. A window has at most one of them.</summary>
    private IEnumerable<CommandDefinition> EffectCommands() =>
    [
        Define(AppCommand.ToggleDark, window => ToggleDark(window))
            with { IsOn = window => _dark.IsDark(window) || _content.Has(window, Glass.ContentLook.Dark), Undoes = [WindowPart.FilterDark, WindowPart.ContentEffect] },
        Define(AppCommand.ToggleGlass, window => ToggleGlass(window))
            with { IsOn = window => _windows.IsGlass(window) || _content.Has(window, Glass.ContentLook.Glass), Undoes = [WindowPart.WindowChanges, WindowPart.ContentEffect] },
    ];

    private IEnumerable<CommandDefinition> AnchorCommands() =>
    [
        Define(AppCommand.ToggleAnchor, window => _anchors.Toggle(window)) with { IsOn = window => _anchors.Has(window), Undoes = [WindowPart.Anchor] },
        // The Anchor is the way back: clicking it shows the window again.
        Define(AppCommand.MinimizeToAnchor, window =>
        {
            if (!_anchors.Has(window)) _anchors.Toggle(window);
            if (_anchors.Has(window)) Native.User32.ShowWindow(window, Native.User32.SW_MINIMIZE);
        }) with { Undoes = [WindowPart.Anchor] },
    ];

    private IEnumerable<CommandDefinition> EverywhereCommands() =>
        CommandCatalog.Everywhere.Select(command =>
            Define(command, _ => RunEverywhere(command)) with { Target = CommandTarget.EveryWindow });

    /// <summary>Put back and Put every window back.</summary>
    private IEnumerable<CommandDefinition> RecoveryCommands() =>
    [
        Define(AppCommand.RestoreWindow, PutBack) with { Recovers = true },
        Define(AppCommand.RestoreAll, _ => PutEveryWindowBack()) with { Target = CommandTarget.NoWindow, Recovers = true },
    ];

    /// <summary>
    /// A command from the command line (roadmap P2-2), run as a hotkey would run it, on the window the script named:
    /// the active one, the one under the pointer, or the topmost open window whose title contains some text.
    /// </summary>
    /// <remarks>
    /// <paramref name="answer"/> is told how it went, once. A command that opens a menu or dialog answers once that
    /// is open: a message posted before it runs is handled by the menu's or dialog's own loop. If the command
    /// returns before that, its own outcome is the answer.
    /// </remarks>
    private void RunFromCommandLine(AppCommand command, string target, Action<CommandLineExit> answer)
    {
        bool answered = false;
        void Answer(CommandLineExit exit)
        {
            if (answered) return;
            answered = true;
            answer(exit);
        }
        Answer(Validate(command, target, out nint window) is { } refused ? refused : Run());

        CommandLineExit Run()
        {
            Diagnostics.Log.Event("command.fromCommandLine", ("name", command));
            if (_commands.TryGet(command, out CommandDefinition definition) && definition.Opens?.Invoke(window) == true)
                _toolbar.BeginInvoke(() => Answer(CommandLineExit.Done));
            return CommandLine.ExitFor(RunOnDisplay(command, window, null));
        }
    }

    /// <summary>The window a command from the command line is for, or why it can't run.</summary>
    private CommandLineExit? Validate(AppCommand command, string target, out nint window)
    {
        window = 0;
        if (!_commands.TryGet(command, out CommandDefinition definition)) return CommandLineExit.Usage;
        if (definition.Target == CommandTarget.Window)
        {
            if (target.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                window = WindowTitled(target["title:".Length..]);
            else if (string.Equals(target, "pointer", StringComparison.OrdinalIgnoreCase))
            {
                Point point = Cursor.Position;
                window = Native.User32.GetAncestor(Native.User32.WindowFromPoint(new Native.NativePoint { X = point.X, Y = point.Y }),
                    Native.User32.GA_ROOT);
            }
            else
                window = TargetOf(Native.User32.GetForegroundWindow());
            if (window == 0 && command != AppCommand.RestoreWindow) return CommandLineExit.NoWindow;
        }
        return null;
    }

    /// <summary>
    /// Gives a window one of the preset sizes (roadmap P2-14): its visible frame keeps its top-left corner, and the
    /// whole of it stays on its display, smaller there if the display is smaller.
    /// </summary>
    private void ResizeWindow(nint window, Size size)
    {
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _)) return;
        _placer.SetFrame(window, Windows.PlacementGeometry.Resized(frame, size, Screen.FromHandle(window).WorkingArea));
    }

    /// <summary>The topmost visible main window whose title contains the text (any case, * for any run).</summary>
    private static nint WindowTitled(string text)
    {
        for (nint window = Native.User32.GetWindow(Native.User32.GetDesktopWindow(), 5); window != 0; window = Native.User32.GetWindow(window, 2))
            if (Native.User32.IsWindowVisible(window) && WindowInfo.IsMainWindow(window) &&
                ProgramSetup.TitleMatches(WindowInfo.Title(window), text))
                return window;
        return 0;
    }

    /// <summary>True while a window has this part from WinScreen.</summary>
    private bool Has(WindowPart part, nint window) => part switch
    {
        WindowPart.CropChoice => _croppingWindow == window,
        WindowPart.View => _pips.ContainsKey(window),
        WindowPart.Tile => _shrunk.ContainsKey(window),
        WindowPart.FilterDark => _dark.IsDark(window),
        WindowPart.ContentEffect => _content.Has(window),
        WindowPart.Anchor => _anchors.Has(window),
        WindowPart.WindowChanges => _windows.HasChanges(window),
        _ => false,
    };

    /// <summary>Takes one part away from one window. False only when the window's own changes couldn't all be undone.</summary>
    private bool TakeAway(WindowPart part, nint window)
    {
        switch (part)
        {
            case WindowPart.CropChoice: if (_croppingWindow == window) CancelCrop(); break;
            case WindowPart.View: if (_pips.TryGetValue(window, out var view)) view.Close(); break;
            case WindowPart.Tile: if (_shrunk.TryGetValue(window, out var tile)) tile.Close(); break;
            case WindowPart.FilterDark: if (_dark.IsDark(window)) _dark.Toggle(window); break;
            case WindowPart.ContentEffect: _content.TurnOff(window); break;
            case WindowPart.Anchor: if (_anchors.Has(window)) _anchors.Toggle(window); break;
            case WindowPart.WindowChanges: return _windows.RestoreWindow(window);
        }
        return true;
    }

    /// <summary>Takes one part away from every window. Returns how many windows' own changes couldn't be undone.</summary>
    private int TakeAwayEverywhere(WindowPart part)
    {
        switch (part)
        {
            case WindowPart.CropChoice: CancelCrop(); break;
            case WindowPart.View: foreach (PipWindow view in _viewsInOrder.ToArray()) view.Close(); break;
            case WindowPart.Tile: foreach (var tile in _shrunk.Values.ToArray()) tile.Close(); break;
            case WindowPart.FilterDark: _dark.TurnAllOff(); break;
            case WindowPart.ContentEffect: _content.TurnAllOff(); break;
            case WindowPart.Anchor: _anchors.RemoveAll(); break;
            case WindowPart.WindowChanges: return _windows.RestoreAll();
        }
        return 0;
    }

    private bool HasAnythingToRestore(nint window) => Enum.GetValues<WindowPart>().Any(part => Has(part, window));

    private void PutBack(nint window)
    {
        if (!HasAnythingToRestore(window))
        {
            Alert(T("window.restore"), T("errors.window.unchanged"), ToolTipIcon.Info);
            return;
        }

        bool restored = true;
        foreach (WindowPart part in PutBackOrder.PutBack)
            restored &= TakeAway(part, window);
        if (restored)
            Confirm(T("window.restore"), T("recovery.restored"));
        else
            Alert(T("window.restore"), string.Join(Environment.NewLine, _windows.RestoreFailures));
    }

    private void PutEveryWindowBack()
    {
        int failed = 0;
        foreach (WindowPart part in PutBackOrder.PutBack)
            failed += TakeAwayEverywhere(part);
        if (failed > 0)
            Alert(T("errors.recovery.someFailed"),
                string.Format(T("errors.window.changeRefusedCount"), failed) + Environment.NewLine +
                string.Join(Environment.NewLine, _windows.RestoreFailures));
        else if (_windows.RecoveryBlocked)
            Alert(T("recovery.attention"), _windows.RecoveryWarning!);
        else
            Confirm(T("window.restoreAll"), T("recovery.restored"));
    }

    /// <summary>
    /// What exit takes away, in <see cref="PutBackOrder.Exit"/>'s order. False, with nothing after the windows taken
    /// away, when the windows' own changes couldn't all be undone: WinScreen then stays running.
    /// </summary>
    private bool TakeEverythingAwayForExit()
    {
        foreach (WindowPart part in PutBackOrder.Exit)
            if (TakeAwayEverywhere(part) != 0)
                return false;
        return true;
    }
}
