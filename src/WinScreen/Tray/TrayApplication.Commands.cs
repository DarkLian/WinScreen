using static WinScreen.Localization.UiText;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Windows;

namespace WinScreen.Tray;

// CommandTable checks that each command is defined once. A new command is a line here plus its name in CommandCatalog.
public sealed partial class TrayApplication
{
    // Built without touching any state, so a test can list it.
    internal IEnumerable<CommandDefinition>[] CommandAreas() =>
        [WindowCommands(), ArrangeCommands(), ViewCommands(), EffectCommands(), AnchorCommands(), EverywhereCommands(), RecoveryCommands()];

    private static CommandDefinition Define(AppCommand command, Action<nint> run) => new(command, (window, _) => run(window));

    private static bool Always(nint window) => true;

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

    private IEnumerable<CommandDefinition> ViewCommands() =>
    [
        Define(AppCommand.OpenPip, window =>
        {
            if (_pips.TryGetValue(window, out var existing)) existing.Close();
            else OpenViewOf(window);
        }) with { IsOn = window => _pips.ContainsKey(window), Undoes = [WindowPart.View, WindowPart.Sound], Opens = window => !_pips.ContainsKey(window) && Native.User32.IsIconic(window) },
        Define(AppCommand.PickPipSource, _ => ShowPicker()) with { Target = CommandTarget.NoWindow, Opens = Always },
        Define(AppCommand.ToggleShrink, window => Shrink(window)) with { IsOn = window => _shrunk.ContainsKey(window), Undoes = [WindowPart.Tile] },
        Define(AppCommand.ToggleCrop, window => ToggleCrop(window)) with { IsOn = window => _windows.IsCropped(window), Undoes = [WindowPart.CropChoice, WindowPart.WindowChanges] },
    ];

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
        Define(AppCommand.MinimizeToAnchor, window =>
        {
            if (!_anchors.Has(window)) _anchors.Toggle(window);
            if (_anchors.Has(window)) Native.User32.ShowWindow(window, Native.User32.SW_MINIMIZE);
        }) with { Undoes = [WindowPart.Anchor] },
    ];

    private IEnumerable<CommandDefinition> EverywhereCommands() =>
        CommandCatalog.Everywhere.Select(command =>
            Define(command, _ => RunEverywhere(command)) with { Target = CommandTarget.EveryWindow });

    private IEnumerable<CommandDefinition> RecoveryCommands() =>
    [
        Define(AppCommand.RestoreWindow, PutBack) with { Recovers = true },
        Define(AppCommand.RestoreAll, _ => PutEveryWindowBack()) with { Target = CommandTarget.NoWindow, Recovers = true },
    ];

    // answer is told once. A command that opens a menu or dialog answers once it is open: a message posted before it
    // runs is handled by the menu's or dialog's own loop. If the command returns before that, its own outcome is the
    // answer.
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

    private void ResizeWindow(nint window, Size size)
    {
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _)) return;
        _placer.SetFrame(window, Windows.PlacementGeometry.Resized(frame, size, Screen.FromHandle(window).WorkingArea));
    }

    private static nint WindowTitled(string text)
    {
        for (nint window = Native.User32.GetWindow(Native.User32.GetDesktopWindow(), 5); window != 0; window = Native.User32.GetWindow(window, 2))
            if (Native.User32.IsWindowVisible(window) && WindowInfo.IsMainWindow(window) &&
                ProgramSetup.TitleMatches(WindowInfo.Title(window), text))
                return window;
        return 0;
    }

    private bool Has(WindowPart part, nint window) => part switch
    {
        WindowPart.CropChoice => _croppingWindow == window,
        WindowPart.View => _pips.ContainsKey(window),
        WindowPart.Sound => _sound.Has(window),
        WindowPart.Tile => _shrunk.ContainsKey(window),
        WindowPart.FilterDark => _dark.IsDark(window),
        WindowPart.ContentEffect => _content.Has(window),
        WindowPart.Anchor => _anchors.Has(window),
        WindowPart.WindowChanges => _windows.HasChanges(window),
        _ => false,
    };

    private bool TakeAway(WindowPart part, nint window)
    {
        switch (part)
        {
            case WindowPart.CropChoice: if (_croppingWindow == window) CancelCrop(); break;
            case WindowPart.View: if (_pips.TryGetValue(window, out var view)) view.Close(); break;
            case WindowPart.Sound: _sound.Unmute(window); break;
            case WindowPart.Tile: if (_shrunk.TryGetValue(window, out var tile)) tile.Close(); break;
            case WindowPart.FilterDark: if (_dark.IsDark(window)) _dark.Toggle(window); break;
            case WindowPart.ContentEffect: _content.TurnOff(window); break;
            case WindowPart.Anchor: if (_anchors.Has(window)) _anchors.Toggle(window); break;
            case WindowPart.WindowChanges: return _windows.RestoreWindow(window);
        }
        return true;
    }

    private int TakeAwayEverywhere(WindowPart part)
    {
        switch (part)
        {
            case WindowPart.CropChoice: CancelCrop(); break;
            case WindowPart.View: foreach (PipWindow view in _viewsInOrder.ToArray()) view.Close(); break;
            case WindowPart.Sound: return _sound.UnmuteAll();
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

    // False, with nothing after the windows taken away, when their own changes couldn't all be undone: WinScreen then
    // stays running.
    private bool TakeEverythingAwayForExit()
    {
        foreach (WindowPart part in PutBackOrder.Exit)
            if (TakeAwayEverywhere(part) != 0)
                return false;
        return true;
    }
}
