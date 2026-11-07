using static WinScreen.Settings.UiText;
using WinScreen.Anchors;
using WinScreen.Dark;
using WinScreen.Hotkeys;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Windows;
using WinScreen.Updates;

namespace WinScreen.Tray;

/// <summary>The running app: a tray icon, the global hotkeys, the toolbar, and what each command does.</summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly WindowController _windows;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly HotkeyHost _hotkeys = new();
    private readonly NotifyIcon _tray = new();
    private readonly StartWithWindows _startup = new();
    private readonly DarkMode _dark = new();
    private readonly AnchorSet _anchors = new();
    private readonly PinBorders _borders = new();
    private readonly Dictionary<nint, ShrinkTile> _shrunk = [];
    private readonly Dictionary<nint, PipWindow> _pips = [];
    private readonly Dictionary<nint, WindowIdentity> _setupDone = [];
    private readonly Dictionary<nint, long> _setupRetryAt = [];
    private readonly ToolStripMenuItem _targetTitle = new() { Enabled = false };
    private readonly ToolStripMenuItem _statusItem = new();
    private string _lastStatus = "Ready";
    private WindowIdentity? _lastIdentity;
    private nint _menuWindow;
    private WindowIdentity? _menuIdentity;
    private nint _croppingWindow;
    private readonly WindowToolbar _toolbar;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private readonly AutomaticUpdates _updates;
    private nint _lastWindow;
    private SettingsWindow? _settingsWindow;
    private CropOverlay? _cropping;

    public TrayApplication(WindowController windows, AppSettings settings, string settingsPath)
    {
        _windows = windows;
        _settings = settings;
        _settingsPath = settingsPath;
        _updates = new AutomaticUpdates(VelopackUpdateSession.Create, message => Notify(T("WinScreen update"), message));
        _toolbar = new WindowToolbar(CanUseToolbar, IsOn, Run);
        _ = _toolbar.Handle;
        _dark.Ignore(_toolbar.Handle);

        _hotkeys.Pressed += id => Run((AppCommand)id, User32.GetForegroundWindow());
        _hotkeys.CloseRequested += ExitThread;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_targetTitle);
        menu.Items.Add(_statusItem);
        _statusItem.Click += (_, _) => MessageBox.Show(_lastStatus, "WinScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(new ToolStripSeparator());
        menu.Opening += (_, _) =>
        {
            _menuIdentity = _lastIdentity?.Matches() == true ? _lastIdentity : null;
            _menuWindow = _menuIdentity?.Window ?? 0;
            _targetTitle.Text = T("Target: ") + (_menuWindow == 0 ? T("No eligible window") : WindowInfo.Title(_menuWindow));
            foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
                if (item.Tag is AppCommand action)
                {
                    item.Checked = _menuWindow != 0 && IsOn(action, _menuWindow);
                    item.Enabled = _menuWindow != 0 && (action == AppCommand.RestoreWindow || (!Paused && _windows.CanControl(_menuWindow)));
                }
        };
        menu.Items.Add(T("Pin or unpin last window"), null, (_, _) => RunFromMenu(AppCommand.TogglePin)).Tag = AppCommand.TogglePin;
        menu.Items.Add(T("See-through at the default level"), null, (_, _) => RunFromMenu(AppCommand.ToggleDefaultOpacity)).Tag = AppCommand.ToggleDefaultOpacity;
        menu.Items.Add(T("Click-through"), null, (_, _) => RunFromMenu(AppCommand.ToggleClickThrough)).Tag = AppCommand.ToggleClickThrough;
        menu.Items.Add(T("Picture-in-Picture of last window"), null, (_, _) => RunFromMenu(AppCommand.OpenPip)).Tag = AppCommand.OpenPip;
        menu.Items.Add(T("Dark mode for last window"), null, (_, _) => RunFromMenu(AppCommand.ToggleDark)).Tag = AppCommand.ToggleDark;
        menu.Items.Add(T("Anchor for last window"), null, (_, _) => RunFromMenu(AppCommand.ToggleAnchor)).Tag = AppCommand.ToggleAnchor;
        menu.Items.Add(T("Shrink last window"), null, (_, _) => RunFromMenu(AppCommand.ToggleShrink)).Tag = AppCommand.ToggleShrink;
        menu.Items.Add(T("Crop last window"), null, (_, _) => RunFromMenu(AppCommand.ToggleCrop)).Tag = AppCommand.ToggleCrop;
        menu.Items.Add(T("Remember last window's setup"), null, (_, _) => RunFromMenu(AppCommand.SaveWindowSetup)).Tag = AppCommand.SaveWindowSetup;
        menu.Items.Add(T("Restore all windows"), null, (_, _) => Run(AppCommand.RestoreAll, 0));
        menu.Items.Add(T("Restore this window"), null, (_, _) => RunFromMenu(AppCommand.RestoreWindow)).Tag = AppCommand.RestoreWindow;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Settings"), null, (_, _) => OpenSettings());
        menu.Items.Add(T("Exit"), null, (_, _) => ExitThread());

        _tray.Icon = LoadIcon();
        _tray.Text = "WinScreen";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;

        IReadOnlyList<string> taken;
        try { taken = Apply(settings); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { taken = [failure.Message]; }
        Notify(T("WinScreen is running"), taken.Count == 0
            ? T("Right-click this icon for more.")
            : T("Some hotkeys are taken by another app: ") + string.Join(", ", taken));
        _statusItem.Text = T("Last result: ") + _lastStatus;
        if (_windows.RecoveryWarning is { } warning)
            Notify(T("Recovery needs attention"), warning);

        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;
            await _updates.Check();
        };
        _updateTimer.Start();
    }

    /// <summary>Puts settings into effect and saves them. Returns the hotkeys another app already holds.</summary>
    private IReadOnlyList<string> Apply(AppSettings settings)
    {
        bool experimentalBefore = _settings.ExperimentalFeatures;
        var desired = new Dictionary<int, (uint Modifiers, Keys Key)>();
        var invalid = new List<string>();
        foreach (var (command, value) in settings.Hotkeys)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!HotkeyText.TryParse(value, out uint modifiers, out Keys key))
                invalid.Add(command + ": " + value);
            else
                desired[(int)command] = (modifiers, key);
        }
        if (invalid.Count > 0) return invalid;
        bool startupBefore = _startup.IsOn;
        if (!_hotkeys.TryReplaceBindings(desired, () =>
            {
                try
                {
                    if (settings.StartWithWindows is { } startup) _startup.IsOn = startup;
                    settings.Save(_settingsPath);
                }
                catch
                {
                    _startup.IsOn = startupBefore;
                    throw;
                }
            }, out var conflicts))
            return conflicts.Select(id => ((AppCommand)id) + ": " + settings.Hotkeys[(AppCommand)id]).ToArray();
        _settings.CopyFrom(settings);
        _windows.SetExcluded(_settings.ExcludedPrograms);
        _windows.SetIncluded(_settings.IncludedPrograms);
        _windows.DefaultOpacity = _settings.DefaultAlpha;
        _toolbar.Enable(_settings.ShowToolbar);
        _updates.SetEnabled(_settings.AutomaticUpdates);
        _updateTimer.Interval = 30_000;
        foreach (var pip in _pips.Values)
        {
            pip.AllowClickForwarding = _settings.ExperimentalFeatures && _settings.PipClickForwarding;
            if (!_settings.ExperimentalFeatures) pip.SetAutoMove(false);
        }
        if (experimentalBefore && !_settings.ExperimentalFeatures)
        {
            CancelCrop();
            _dark.TurnAllOff();
            foreach (var tile in _shrunk.Values.ToArray()) tile.Close();
            if (_windows.EndExperimentalEffects() > 0)
                Notify(T("Recovery needs attention"), T("Some windows couldn't be restored"));
        }
        return [];
    }
    private bool Paused => _settings.PauseInFullScreen && FullScreen.IsActive();

    private bool CanUseToolbar(nint window)
    {
        return !Paused && _windows.CanControl(window);
    }

    private bool IsOn(AppCommand command, nint window)
    {
        return command switch
        {
            AppCommand.TogglePin => _windows.IsPinned(window),
            AppCommand.ToggleClickThrough => _windows.IsClickThrough(window),
            AppCommand.ToggleDark => _dark.IsDark(window),
            AppCommand.ToggleGlass => _windows.IsGlass(window),
            AppCommand.ToggleAnchor => _anchors.Has(window),
            AppCommand.ToggleDefaultOpacity => _windows.OpacityOf(window) < 255,
            AppCommand.SaveWindowSetup => _settings.SetupForWindow(window) is not null,
            AppCommand.ToggleCrop => _windows.IsCropped(window),
            _ => false,
        };
    }

    private void Watch()
    {
        nint foreground = User32.GetForegroundWindow();
        if (_windows.CanControl(foreground))
        {
            _lastWindow = foreground;
            if (_lastIdentity is null || _lastIdentity.Window != foreground || !_lastIdentity.Matches())
            {
                try { _lastIdentity = WindowIdentity.Capture(foreground); }
                catch (WindowOperationException) { _lastIdentity = null; }
            }
            ApplySavedSetup(foreground);
        }

        _windows.Refresh(Cursor.Position, _settings.OpaqueOnHover, _settings.PausePinWhenMaximized);
        _borders.Show(_settings.HighlightPinned ? _windows.VisiblePinnedWindows() : []);
    }

    /// <summary>
    /// Gives a window its program's saved setup the first time it comes forward, and never again: pinning
    /// needs a window that is forward, and whatever the person changes after that must stay changed.
    /// </summary>
    private void ApplySavedSetup(nint window)
    {
        if (Paused || !WindowInfo.IsMainWindow(window) || _setupRetryAt.GetValueOrDefault(window) > Environment.TickCount64)
            return;
        if (_setupDone.TryGetValue(window, out var prior) && prior.Matches())
            return;
        foreach (var stale in _setupDone.Where(pair => !pair.Value.Matches()).Select(pair => pair.Key).ToArray())
        { _setupDone.Remove(stale); _setupRetryAt.Remove(stale); }
        if (_settings.SetupForWindow(window) is not { } setup)
            return;
        try
        {
            var identity = WindowIdentity.Capture(window);
            if (setup.Pinned && !_windows.IsPinned(window)) _windows.TogglePin(window);
            identity.Require();
            if (setup.Alpha < 255) _windows.SetOpacity(window, setup.Alpha);
            identity.Require();
            if (_settings.ExperimentalFeatures && setup.Dark && !_dark.IsDark(window)) _dark.Toggle(window);
            _setupDone[window] = identity;
            _setupRetryAt.Remove(window);
        }
        catch (WindowOperationException failure)
        {
            _setupRetryAt[window] = Environment.TickCount64 + 5000;
            Notify(T("WinScreen couldn't do that"), failure.Message);
        }
    }
    /// <summary>Saves how this window is now for its program, or forgets the program's setup if it has none.</summary>
    private void SaveSetup(nint window)
    {
        string program = WindowInfo.ProgramOf(window);
        var setup = new ProgramSetup
        {
            Program = program,
            ExecutablePath = WindowInfo.ExecutablePath(window),
            Pinned = _windows.IsPinned(window),
            Alpha = _windows.OpacityOf(window),
            Dark = _dark.IsDark(window),
        };

        _settings.SavedSetups.RemoveAll(saved => (!string.IsNullOrEmpty(setup.ExecutablePath) && string.Equals(saved.ExecutablePath, setup.ExecutablePath, StringComparison.OrdinalIgnoreCase)) || (string.IsNullOrEmpty(saved.ExecutablePath) && string.Equals(saved.Program, program, StringComparison.OrdinalIgnoreCase)));
        if (setup.ChangesAnything)
            _settings.SavedSetups.Add(setup);
        _settings.Save(_settingsPath);
        _setupDone[window] = WindowIdentity.Capture(window);
        Notify(setup.ChangesAnything ? T("Setup saved") : T("Saved setup removed"), setup.ChangesAnything ? setup.ToString() : program);
    }

    private void Shrink(nint window)
    {
        if (_pips.Remove(window, out var preview)) preview.Close();
        if (_shrunk.ContainsKey(window))
            return;

        var tile = new ShrinkTile(window, _windows);
        _shrunk[window] = tile;
        tile.FormClosed += (_, _) => _shrunk.Remove(window);
        tile.Problem += text => Notify(T("WinScreen couldn't do that"), text);
        tile.ShowOnTop();
        if (!tile.IsDisposed)
            _dark.Ignore(tile.Handle);
    }

    private void ToggleCrop(nint window)
    {
        if (_windows.IsCropped(window))
        {
            _windows.RemoveCrop(window);
            return;
        }

        if (_cropping is { IsDisposed: false } || !User32.GetWindowRect(window, out Rect r))
            return;

        var overlay = new CropOverlay(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        var identity = WindowIdentity.Capture(window);
        overlay.Chosen += part =>
        {
            if (_cropping != overlay || overlay.IsDisposed) return;
            try
            {
                identity.Require();
                _windows.Crop(window, part);
                User32.SetForegroundWindow(window);
            }
            catch (WindowOperationException failure)
            {
                Notify(T("WinScreen couldn't do that"), failure.Message);
            }
        };
        _cropping = overlay;
        _croppingWindow = window;
        overlay.ShowOnTop();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsDisposed: false })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _startup, Apply);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void ExitThreadCore()
    {
        CancelCrop();
        foreach (var preview in _pips.Values.ToArray()) preview.Close();
        foreach (var tile in _shrunk.Values.ToArray()) tile.Close();
        _dark.TurnAllOff();
        _anchors.RemoveAll();
        if (_windows.RestoreAll() != 0)
        {
            Notify(T("Recovery needs attention"), "Exit was cancelled because some windows could not be restored. Use Restore all windows to retry; no update was scheduled. " + string.Join(Environment.NewLine, _windows.RestoreFailures));
            return;
        }
        _updateTimer.Dispose();
        _watch.Dispose();
        _borders.Dispose();
        _dark.Dispose();
        _anchors.Dispose();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
            form.Close();
        _toolbar.Dispose();
        Task.Run(_updates.ApplyOnExit).GetAwaiter().GetResult();
        _updates.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private void Run(AppCommand command, nint window)
    {
        if (command == AppCommand.RestoreWindow &&
            (window == 0 || !_windows.CanControl(window)) && _lastIdentity?.Matches() == true)
        {
            User32.GetWindowThreadProcessId(window, out uint pid);
            if (window == 0 || pid == Environment.ProcessId) window = _lastIdentity.Window;
        }
        bool recovery = command is AppCommand.RestoreAll or AppCommand.RestoreWindow;
        if (!recovery && (Paused || !_windows.CanControl(window)))
        {
            Notify(T("WinScreen couldn't do that"), Paused ? T("Commands are paused for the full-screen app.") : T("The target is unavailable or excluded. Recovery is still available from the tray."));
            return;
        }
        if (!recovery && !_settings.ExperimentalFeatures && command is AppCommand.ToggleDark or AppCommand.ToggleGlass or AppCommand.ToggleShrink or AppCommand.ToggleCrop)
        {
            Notify(T("WinScreen couldn't do that"), T("Enable experimental effects in Settings to use this action."));
            return;
        }

        try
        {
            switch (command)
            {
                case AppCommand.TogglePin:
                    Notify(_windows.TogglePin(window) ? T("Pinned on top") : T("Unpinned"), WindowInfo.Title(window));
                    break;
                case AppCommand.OpacityUp:
                    _windows.ChangeOpacity(window, WindowController.OpacityStep);
                    break;
                case AppCommand.OpacityDown:
                    _windows.ChangeOpacity(window, -WindowController.OpacityStep);
                    break;
                case AppCommand.ToggleDefaultOpacity:
                    _windows.ToggleDefaultOpacity(window);
                    break;
                case AppCommand.ToggleClickThrough:
                    Notify(_windows.ToggleClickThrough(window) ? T("Click-through on") : T("Click-through off"),
                        T("Restore all windows from the tray icon if you can't reach it."));
                    break;
                case AppCommand.OpenPip:
                    if (_pips.TryGetValue(window, out var existing)) { existing.Activate(); break; }
                    if (_shrunk.Remove(window, out var tile)) tile.Close();
                    var pip = new PipWindow(window, _windows);
                    pip.AllowClickForwarding = _settings.ExperimentalFeatures && _settings.PipClickForwarding;
                    _pips[window] = pip;
                    pip.FormClosed += (_, _) => _pips.Remove(window);
                    pip.Problem += text => Notify(T("WinScreen couldn't do that"), text);
                    pip.ShowOnTop();
                    if (pip.IsDisposed) break;
                    _dark.Ignore(pip.Handle);
                    if (_settings.ExperimentalFeatures && _settings.PipAutoMove)
                        pip.SetAutoMove(true);
                    break;
                case AppCommand.ToggleDark:
                    _dark.Toggle(window);
                    break;
                case AppCommand.ToggleGlass:
                    _windows.ToggleGlass(window);
                    break;
                case AppCommand.ToggleAnchor:
                    _anchors.Toggle(window);
                    break;
                case AppCommand.SaveWindowSetup:
                    SaveSetup(window);
                    break;
                case AppCommand.ToggleShrink:
                    Shrink(window);
                    break;
                case AppCommand.ToggleCrop:
                    ToggleCrop(window);
                    break;
                case AppCommand.RestoreAll:
                    CancelCrop();
                    foreach (var preview in _pips.Values.ToArray()) preview.Close();
                    foreach (var shrunk in _shrunk.Values.ToArray()) shrunk.Close();
                    _dark.TurnAllOff();
                    _anchors.RemoveAll();
                    int failed = _windows.RestoreAll();
                    if (failed > 0)
                        Notify(T("Some windows couldn't be restored"), string.Format(T("{0} window(s) refused the change."), failed) + Environment.NewLine + string.Join(Environment.NewLine, _windows.RestoreFailures));
                    else
                        Notify(T("Restore all windows"), T("All changes restored."));
                    break;
                case AppCommand.RestoreWindow:
                    if (_croppingWindow == window) CancelCrop();
                    if (_pips.Remove(window, out var view)) view.Close();
                    if (_shrunk.Remove(window, out var shrunkTile)) shrunkTile.Close();
                    if (_dark.IsDark(window)) _dark.Toggle(window);
                    if (_anchors.Has(window)) _anchors.Toggle(window);
                    Notify(T("Restore this window"), _windows.RestoreWindow(window) ? T("All changes restored.") : string.Join(Environment.NewLine, _windows.RestoreFailures));
                    break;
            }
        }
        catch (WindowOperationException failure)
        {
            Notify(T("WinScreen couldn't do that"), failure.Message);
        }
    }

    private void Notify(string title, string text)
    {
        _lastStatus = title + ": " + text;
        _statusItem.Text = T("Last result: ") + (_lastStatus.Length > 120 ? _lastStatus[..120] + "…" : _lastStatus);
        if (!_settings.ShowNotifications)
            return;
        _tray.ShowBalloonTip(1500, title, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.None);
    }

    private void CancelCrop()
    {
        var overlay = _cropping;
        _cropping = null;
        _croppingWindow = 0;
        overlay?.Close();
    }

    private void RunFromMenu(AppCommand command)
    {
        if (_menuIdentity?.Matches() != true)
        {
            Notify(T("WinScreen couldn't do that"), T("The target is unavailable or excluded. Recovery is still available from the tray."));
            return;
        }
        Run(command, _menuIdentity.Window);
    }

    private static Icon LoadIcon()
    {
        string? path = Environment.ProcessPath;
        return (path is null ? null : Icon.ExtractAssociatedIcon(path)) ?? SystemIcons.Application;
    }
}
