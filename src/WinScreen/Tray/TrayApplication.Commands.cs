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

    /// <summary>Changes WinScreen makes to the window itself, all through <see cref="WindowController"/>.</summary>
    private IEnumerable<CommandDefinition> WindowCommands() =>
    [
        Define(AppCommand.TogglePin, window =>
            Confirm(_windows.TogglePin(window) ? T("status.pinned") : T("status.unpinned"), WindowInfo.Title(window)))
            with { IsOn = window => _windows.IsPinned(window) },
        Define(AppCommand.OpacityUp, window => ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(1)))),
        Define(AppCommand.OpacityDown, window => ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(-1)))),
        Define(AppCommand.ToggleDefaultOpacity, window => ShowOpacity(window, _windows.ToggleDefaultOpacity(window)))
            with { IsOn = window => _windows.OpacityOf(window) < 255 },
        Define(AppCommand.ToggleClickThrough, window =>
        {
            if (_windows.ToggleClickThrough(window))
                Alert(T("status.clickThroughOn"), T("window.clickThrough.recoveryHelp"), ToolTipIcon.Info);
            else
                Confirm(T("status.clickThroughOff"), WindowInfo.Title(window));
        }) with { IsOn = window => _windows.IsClickThrough(window) },
        new CommandDefinition(AppCommand.ToggleBorderless, (window, display) => _windows.ToggleBorderless(window, false, display))
            { IsOn = window => _windows.IsBorderless(window) && !_windows.IsBorderlessFilled(window) },
        new CommandDefinition(AppCommand.ToggleBorderlessFill, (window, display) => _windows.ToggleBorderless(window, true, display))
            { IsOn = window => _windows.IsBorderlessFilled(window) },
        Define(AppCommand.ToggleDarkTitleBar, window => _windows.ToggleDarkTitleBar(window))
            with { IsOn = window => _windows.IsDarkTitleBar(window) },
        Define(AppCommand.ToggleRollUp, window => _windows.ToggleRollUp(window))
            with { IsOn = window => _windows.IsRolledUp(window) },
        Define(AppCommand.SendToBottom, window => _windows.SendToBottom(window)),
        Define(AppCommand.ShowWindowInfo, ShowWindowInfo),
        Define(AppCommand.SaveWindowSetup, window => SaveSetup(window))
            with { IsOn = window => _settings.SetupForWindow(window) is not null },
    ];

    /// <summary>Where a window is: the nine places, another display, exact bounds, and back.</summary>
    private IEnumerable<CommandDefinition> ArrangeCommands() =>
    [
        Define(AppCommand.Arrange, window => ShowArrangeMenu(window)),
        .. Enum.GetValues<AppCommand>().Where(command => CommandCatalog.AnchorOf(command) is not null).Select(command =>
            Define(command, window => _placer.Align(window, CommandCatalog.AnchorOf(command)!.Value))),
        Define(AppCommand.MoveToNextDisplay, window => _placer.MoveToNextDisplay(window)),
        Define(AppCommand.PlaceExactly, window => PlaceExactly(window)),
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
        }) with { Target = CommandTarget.UnderPointer },
    ];

    /// <summary>Views of a window or of the screen, Shrink tiles and crops.</summary>
    private IEnumerable<CommandDefinition> ViewCommands() =>
    [
        Define(AppCommand.OpenPip, window =>
        {
            if (_pips.TryGetValue(window, out var existing)) existing.Close();
            else OpenViewOf(window);
        }) with { IsOn = window => _pips.ContainsKey(window) },
        Define(AppCommand.PickPipSource, _ => ShowPicker()) with { Target = CommandTarget.NoWindow },
        Define(AppCommand.ToggleShrink, window => Shrink(window)) with { IsOn = window => _shrunk.ContainsKey(window) },
        Define(AppCommand.ToggleCrop, window => ToggleCrop(window)) with { IsOn = window => _windows.IsCropped(window) },
    ];

    /// <summary>Dark and Glass, whichever kind of each Settings chooses. A window has at most one of them.</summary>
    private IEnumerable<CommandDefinition> EffectCommands() =>
    [
        Define(AppCommand.ToggleDark, window => ToggleDark(window))
            with { IsOn = window => _dark.IsDark(window) || _content.Has(window, Glass.ContentLook.Dark) },
        Define(AppCommand.ToggleGlass, window => ToggleGlass(window))
            with { IsOn = window => _windows.IsGlass(window) || _content.Has(window, Glass.ContentLook.Glass) },
    ];

    private IEnumerable<CommandDefinition> AnchorCommands() =>
    [
        Define(AppCommand.ToggleAnchor, window => _anchors.Toggle(window)) with { IsOn = window => _anchors.Has(window) },
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

    private void PutBack(nint window)
    {
        if (!HasAnythingToRestore(window))
        {
            Alert(T("window.restore"), T("errors.window.unchanged"), ToolTipIcon.Info);
            return;
        }

        if (_croppingWindow == window) CancelCrop();
        if (_pips.TryGetValue(window, out var view)) view.Close();
        if (_shrunk.TryGetValue(window, out var shrunkTile)) shrunkTile.Close();
        if (_dark.IsDark(window)) _dark.Toggle(window);
        _content.TurnOff(window);
        if (_anchors.Has(window)) _anchors.Toggle(window);
        if (_windows.RestoreWindow(window))
            Confirm(T("window.restore"), T("recovery.restored"));
        else
            Alert(T("window.restore"), string.Join(Environment.NewLine, _windows.RestoreFailures));
    }

    private void PutEveryWindowBack()
    {
        CancelCrop();
        foreach (PipWindow preview in _viewsInOrder.ToArray()) preview.Close();
        foreach (var shrunk in _shrunk.Values.ToArray()) shrunk.Close();
        _dark.TurnAllOff();
        _content.TurnAllOff();
        _anchors.RemoveAll();
        int failed = _windows.RestoreAll();
        if (failed > 0)
            Alert(T("errors.recovery.someFailed"),
                string.Format(T("errors.window.changeRefusedCount"), failed) + Environment.NewLine +
                string.Join(Environment.NewLine, _windows.RestoreFailures));
        else if (_windows.RecoveryBlocked)
            Alert(T("recovery.attention"), _windows.RecoveryWarning!);
        else
            Confirm(T("window.restoreAll"), T("recovery.restored"));
    }
}
