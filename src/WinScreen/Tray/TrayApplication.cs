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
    private readonly HashSet<nint> _setupDone = [];
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
        menu.Items.Add(T("Pin or unpin last window"), null, (_, _) => Run(AppCommand.TogglePin, _lastWindow));
        menu.Items.Add(T("Picture-in-Picture of last window"), null, (_, _) => Run(AppCommand.OpenPip, _lastWindow));
        menu.Items.Add(T("Dark mode for last window"), null, (_, _) => Run(AppCommand.ToggleDark, _lastWindow));
        menu.Items.Add(T("Anchor for last window"), null, (_, _) => Run(AppCommand.ToggleAnchor, _lastWindow));
        menu.Items.Add(T("Shrink last window"), null, (_, _) => Run(AppCommand.ToggleShrink, _lastWindow));
        menu.Items.Add(T("Crop last window"), null, (_, _) => Run(AppCommand.ToggleCrop, _lastWindow));
        menu.Items.Add(T("Remember last window's setup"), null, (_, _) => Run(AppCommand.SaveWindowSetup, _lastWindow));
        menu.Items.Add(T("Restore all windows"), null, (_, _) => Run(AppCommand.RestoreAll, 0));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Settings"), null, (_, _) => OpenSettings());
        menu.Items.Add(T("Exit"), null, (_, _) => ExitThread());

        _tray.Icon = LoadIcon();
        _tray.Text = "WinScreen";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;

        IReadOnlyList<string> taken = Apply(settings);
        Notify(T("WinScreen is running"), taken.Count == 0
            ? T("Right-click this icon for more.")
            : T("Some hotkeys are taken by another app: ") + string.Join(", ", taken));

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
        _windows.SetExcluded(settings.ExcludedPrograms);
        _windows.SetIncluded(settings.IncludedPrograms);
        _windows.DefaultOpacity = settings.DefaultAlpha;
        _toolbar.Enable(settings.ShowToolbar);
        settings.Save(_settingsPath);
        _updates.SetEnabled(settings.AutomaticUpdates);
        _updateTimer.Interval = 30_000;

        _hotkeys.UnregisterAll();
        var taken = new List<string>();
        foreach ((AppCommand command, string text) in settings.Hotkeys)
        {
            if (text.Length == 0)
                continue;
            if (!HotkeyText.TryParse(text, out uint modifiers, out Keys key) || !_hotkeys.TryRegister((int)command, modifiers, key, out _))
                taken.Add(text);
        }

        return taken;
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
            AppCommand.SaveWindowSetup => _settings.SetupFor(WindowInfo.ProgramOf(window)) is not null,
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
        if (Paused || _setupDone.Contains(window) || !WindowInfo.IsMainWindow(window))
            return;

        _setupDone.RemoveWhere(known => !User32.IsWindow(known));
        _setupDone.Add(window);
        if (_settings.SetupFor(WindowInfo.ProgramOf(window)) is not { } setup)
            return;

        try
        {
            if (setup.Pinned && !_windows.IsPinned(window))
                _windows.TogglePin(window);
            if (setup.Alpha < 255)
                _windows.SetOpacity(window, setup.Alpha);
            if (setup.Dark && !_dark.IsDark(window))
                _dark.Toggle(window);
        }
        catch (WindowOperationException failure)
        {
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
            Pinned = _windows.IsPinned(window),
            Alpha = _windows.OpacityOf(window),
            Dark = _dark.IsDark(window),
        };

        _settings.SavedSetups.RemoveAll(saved => string.Equals(saved.Program, program, StringComparison.OrdinalIgnoreCase));
        if (setup.ChangesAnything)
            _settings.SavedSetups.Add(setup);
        _settings.Save(_settingsPath);
        _setupDone.Add(window);
        Notify(setup.ChangesAnything ? T("Setup saved") : T("Saved setup removed"), setup.ChangesAnything ? setup.ToString() : program);
    }

    private void Shrink(nint window)
    {
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
        overlay.Chosen += part =>
        {
            try
            {
                _windows.Crop(window, part);
                User32.SetForegroundWindow(window);
            }
            catch (WindowOperationException failure)
            {
                Notify(T("WinScreen couldn't do that"), failure.Message);
            }
        };
        _cropping = overlay;
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
        _updateTimer.Dispose();
        _watch.Dispose();
        _borders.Dispose();
        _dark.Dispose();
        _anchors.Dispose();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
            form.Close();
        _toolbar.Dispose();
        _windows.RestoreAll();
        Task.Run(_updates.ApplyOnExit).GetAwaiter().GetResult();
        _updates.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private void Run(AppCommand command, nint window)
    {
        if (command != AppCommand.RestoreAll && (Paused || !_windows.CanControl(window)))
            return;

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
                    var pip = new PipWindow(window, _windows);
                    pip.Problem += text => Notify(T("WinScreen couldn't do that"), text);
                    pip.ShowOnTop();
                    _dark.Ignore(pip.Handle);
                    if (_settings.PipAutoMove)
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
                    _dark.TurnAllOff();
                    _anchors.RemoveAll();
                    int failed = _windows.RestoreAll();
                    if (failed > 0)
                        Notify(T("Some windows couldn't be restored"), string.Format(T("{0} window(s) refused the change."), failed));
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
        if (!_settings.ShowNotifications)
            return;
        _tray.ShowBalloonTip(1500, title, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.None);
    }

    private static Icon LoadIcon()
    {
        string? path = Environment.ProcessPath;
        return (path is null ? null : Icon.ExtractAssociatedIcon(path)) ?? SystemIcons.Application;
    }
}
