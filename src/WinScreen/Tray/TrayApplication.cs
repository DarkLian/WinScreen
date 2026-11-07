using static WinScreen.Localization.UiText;
using System.Runtime.InteropServices;
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
    private const int MenuTextLimit = 60;
    private const int BalloonMilliseconds = 3000;
    private const int HookSilenceSeconds = 2;

    private readonly WindowController _windows;
    private readonly WindowPlacer _placer;
    private readonly MouseGestures _modifierDrag;
    private nint _hookForeground;
    private bool _hookForegroundExcluded;
    private Point _hookCursor;
    private bool _hookReported;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly HotkeyHost _hotkeys = new();
    private readonly NotifyIcon _tray = new();
    private readonly StartWithWindows _startup = new();
    private readonly DarkMode _dark = new();
    private readonly AnchorSet _anchors = new();
    private readonly PinBorders _borders = new();
    private readonly ActionFlash _flash = new();
    private readonly Dictionary<nint, ShrinkTile> _shrunk = [];
    private readonly Dictionary<nint, PipWindow> _pips = [];
    private readonly List<PipWindow> _viewsInOrder = [];
    private readonly Dictionary<nint, WindowIdentity> _setupDone = [];
    private readonly Dictionary<nint, long> _setupRetryAt = [];
    private readonly ToolStripMenuItem _targetTitle = new() { Enabled = false };
    private readonly ToolStripMenuItem _statusItem = new();
    private string _lastStatus = T("tray.noActions");
    private WindowIdentity? _lastIdentity;
    private nint _menuWindow;
    private WindowIdentity? _menuIdentity;
    private nint _croppingWindow;
    private readonly WindowToolbar _toolbar;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private readonly AutomaticUpdates _updates;
    private SettingsWindow? _settingsWindow;
    private PipSourcePicker? _picker;
    private CropOverlay? _cropping;
    private WindowMenuHost? _windowMenu;

    public TrayApplication(WindowController windows, AppSettings settings, string settingsPath)
    {
        _windows = windows;
        _placer = new WindowPlacer(windows);
        _modifierDrag = new MouseGestures(CanDrag, _placer.Remember);
        _settings = settings;
        _settingsPath = settingsPath;
        // Update news stays out of Last result, where it could replace a failure someone needs to read.
        _updates = new AutomaticUpdates(VelopackUpdateSession.Create, message =>
        {
            if (_settings.ShowNotifications)
                Balloon(T("updates.title"), message, ToolTipIcon.None);
        });
        _toolbar = new WindowToolbar(CanUseToolbar, IsOn, IsAvailable, Run);
        _ = _toolbar.Handle;
        _modifierDrag.MenuEligible = CanOpenWindowMenu;
        _modifierDrag.MenuRequested = (window, point) => _toolbar.BeginInvoke(() => ShowWindowMenu(window, point));
        _modifierDrag.OutsidePressed = point => _toolbar.BeginInvoke(() => _windowMenu?.OutsidePress(point));
        _dark.Ignore(_toolbar.Handle);
        _ = _flash.Handle;
        _dark.Ignore(_flash.Handle);
        _dark.Notice += text => Alert(T("window.dark"), text, ToolTipIcon.Info);
        _modifierDrag.OpacityEligible = CanChangeOpacity;
        _modifierDrag.OpacityRequested = (window, steps) =>
        {
            WindowIdentity identity;
            try { identity = WindowIdentity.Capture(window); }
            catch (WindowOperationException) { return; }
            _toolbar.BeginInvoke(() =>
            {
                if (!_settings.OpacityWheel || !identity.Matches() || !CanChangeOpacity(window)) return;
                try { ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(steps))); }
                catch (Exception failure) when (IsExpected(failure)) { Alert(T("errors.operation.title"), failure.Message); }
            });
        };
        _anchors.Created += _dark.Ignore;
        _anchors.ShowRequested += window =>
        {
            if (_pips.TryGetValue(window, out var pip)) pip.GoToSource();
            else if (!WindowInfo.IsConcealed(window))
            {
                if (User32.IsIconic(window)) User32.ShowWindow(window, User32.SW_RESTORE);
                User32.SetForegroundWindow(window);
            }
        };

        _hotkeys.Pressed += id => Run((AppCommand)id, TargetOf(User32.GetForegroundWindow()));
        _hotkeys.CloseRequested += ExitThread;

        _tray.Icon = LoadIcon();
        _tray.Text = "WinScreen";
        _tray.ContextMenuStrip = BuildMenu();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };
        _tray.Visible = true;
        ShowStatus();

        bool firstRun = !_settings.WelcomeShown;
        IReadOnlyList<HotkeyProblem> problems = StartUp();
        if (problems.Count > 0)
            Alert(T("hotkeys.someUnavailable"), Describe(problems));
        else if (firstRun)
            Balloon(T("app.running"), T("tray.welcome"),
                ToolTipIcon.None);
        if (_windows.RecoveryWarning is { } warning)
            Alert(T("recovery.attention"), warning);

        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;
            await _updates.Check();
        };
        _updateTimer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_targetTitle);
        menu.Items.Add(_statusItem);
        _statusItem.Click += (_, _) =>
            MessageBox.Show(_lastStatus, "WinScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(new ToolStripSeparator());
        var builder = CreateWindowMenuBuilder(() => _menuIdentity);
        builder.Populate(menu.Items, true);

        menu.Items.Add(BuildViewsMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("settings.title"), null, (_, _) => OpenSettings());
        menu.Items.Add(T("updates.check"), null, async (_, _) => await CheckForUpdates());
        menu.Items.Add(T("app.about"), null, (_, _) => MessageBox.Show(
            string.Format(T("app.version"), AppInfo.Version), "WinScreen", MessageBoxButtons.OK,
            MessageBoxIcon.Information));
        menu.Items.Add(T("app.exit"), null, (_, _) => ExitThread());

        menu.Opening += (_, _) =>
        {
            _menuIdentity = _lastIdentity?.IsSameWindow() == true ? _lastIdentity : null;
            _menuWindow = _menuIdentity?.Window ?? 0;
            _targetTitle.Text = T("tray.targetPrefix") +
                                (_menuWindow == 0 ? T("tray.noTarget") : MenuText(WindowInfo.Title(_menuWindow)));
            builder.Refresh(menu.Items);
        };
        return menu;
    }

    private WindowMenuBuilder CreateWindowMenuBuilder(Func<WindowIdentity?> target) => new(target,
        Run, (window, bounds) => RunOnDisplay(AppCommand.ToggleBorderlessFill, window, bounds), IsAvailable,
        (command, window) => command == AppCommand.RestoreWindow ||
                             FullScreenDecision(command, window) == FullScreenAction.Run && _windows.CanControl(window),
        IsOn, () => Alert(T("errors.operation.title"),
            T("errors.window.targetUnavailable")));

    private void ShowWindowMenu(nint window, Point point)
    {
        if (_windowMenu is { IsDisposed: false } || !CanOpenWindowMenu(window) ||
            FullScreenDecision(AppCommand.ShowWindowMenu, window) != FullScreenAction.Run) return;
        Attempt(() =>
        {
            WindowIdentity identity = WindowIdentity.Observe(window);
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(MenuText(WindowInfo.Title(window))) { Enabled = false });
            var builder = CreateWindowMenuBuilder(() => identity);
            builder.Populate(menu.Items, false);
            builder.Refresh(menu.Items);
            var host = new WindowMenuHost(menu, point);
            _windowMenu = host;
            // A keyboard-opened menu normally needs no mouse hook. If Windows refuses activation,
            // borrow the same shared hook only until that fallback menu closes.
            host.FallbackOpened = () => FollowModifierHook(User32.GetForegroundWindow());
            _modifierDrag.MenuVisible = true;
            host.FormClosed += (_, _) => { _windowMenu = null; _modifierDrag.MenuVisible = false; };
            host.Open();
        });
    }

    /// <summary>What can be opened, and the views that are open, which may have no original to act from.</summary>
    private ToolStripMenuItem BuildViewsMenu()
    {
        var views = new ToolStripMenuItem(T("pip.views"));
        views.DropDownItems.Add(new ToolStripSeparator()); // Windows only draws the arrow with something inside.
        views.DropDownOpening += (_, _) =>
        {
            views.DropDownItems.Clear();
            views.DropDownItems.Add(CommandCatalog.Name(AppCommand.PickPipSource) + "...", null,
                (_, _) => Run(AppCommand.PickPipSource, 0));
            if (_viewsInOrder.Count > 0)
                views.DropDownItems.Add(new ToolStripSeparator());
            foreach (PipWindow view in _viewsInOrder)
            {
                var item = new ToolStripMenuItem(MenuText(view.Text));
                if (view.IsSuspended)
                    item.DropDownItems.Add(T("pip.restoreView"), null, (_, _) => view.ResumeView());
                item.DropDownItems.Add(new ToolStripMenuItem(T("window.clickThrough"), null,
                    (_, _) => view.Overrides = view.Overrides with { ClickThrough = !view.Overrides.ClickThrough })
                    { Checked = view.Overrides.ClickThrough });
                item.DropDownItems.Add(T("common.close"), null, (_, _) => view.Close());
                views.DropDownItems.Add(item);
            }
        };
        return views;
    }

    /// <summary>
    /// Puts saved settings into effect when WinScreen starts. A hotkey that can't be used is reported and
    /// the rest of the app carries on, because at startup nothing is running that a refusal could protect.
    /// </summary>
    private IReadOnlyList<HotkeyProblem> StartUp()
    {
        List<HotkeyProblem> problems = ReadHotkeys(_settings, out var desired);
        foreach (AppCommand command in _hotkeys.RegisterAvailable(desired).Cast<AppCommand>())
            problems.Add(Taken(command, _settings));

        try
        {
            if (_settings.StartWithWindows is { } startup) _startup.IsOn = startup;
            _settings.WelcomeShown = true;
            _settings.Save(_settingsPath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException)
        {
            Alert(T("errors.settings.saveFailed"), failure.Message);
        }

        ApplyPreferences(_settings.ExperimentalFeatures);
        return problems;
    }

    /// <summary>Puts changes from Settings into effect and saves them, or changes nothing and says why.</summary>
    private IReadOnlyList<HotkeyProblem> Apply(AppSettings settings)
    {
        bool experimentalBefore = _settings.ExperimentalFeatures;
        List<HotkeyProblem> problems = ReadHotkeys(settings, out var desired);
        if (problems.Count > 0)
            return problems;

        bool startupBefore = _startup.IsOn;
        if (!_hotkeys.TryReplaceBindings(desired, () =>
            {
                try
                {
                    if (settings.Imported && File.Exists(_settingsPath))
                        File.Copy(_settingsPath, _settingsPath + ".before-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                    if (settings.StartWithWindows is { } startup) _startup.IsOn = startup;
                    settings.Save(_settingsPath);
                }
                catch
                {
                    _startup.IsOn = startupBefore;
                    throw;
                }
            }, out var conflicts))
            return conflicts.Cast<AppCommand>().Select(command => Taken(command, settings)).ToArray();

        _settings.CopyFrom(settings);
        ApplyPreferences(experimentalBefore);
        return [];
    }

    private void ApplyPreferences(bool experimentalBefore)
    {
        _hookForeground = 0;
        _windows.SetExcluded(_settings.ExcludedPrograms);
        _windows.SetIncluded(_settings.IncludedPrograms);
        _windows.DefaultOpacity = _settings.DefaultAlpha;
        _borders.Configure(_settings.PinBorder);
        _dark.DetectAlreadyDark = _settings.DetectAlreadyDark;
        _toolbar.Enable(_settings.ShowToolbar);
        _toolbar.Configure(_settings.Toolbar);
        _updates.SetEnabled(_settings.AutomaticUpdates);
        _updateTimer.Interval = 30_000;
        foreach (PipWindow pip in _viewsInOrder)
        {
            pip.ApplyPipPreferences(_settings.Pip);
            pip.ApplyViewPreferences(_settings.PipViews);
            pip.AllowClickForwarding = _settings.ExperimentalFeatures && _settings.PipClickForwarding;
            pip.AllowAutoMove = _settings.ExperimentalFeatures;
            pip.AllowSwitcherHiding = _settings.ExperimentalFeatures;
            if (!_settings.ExperimentalFeatures) pip.SetSwitcherHidden(false);
            if (!_settings.ExperimentalFeatures) pip.SetAutoMove(false);
        }

        if (experimentalBefore && !_settings.ExperimentalFeatures)
        {
            CancelCrop();
            _dark.TurnAllOff();
            foreach (var tile in _shrunk.Values.ToArray()) tile.Close();
            if (_windows.EndExperimentalEffects() > 0)
                Alert(T("recovery.attention"), T("errors.recovery.someFailed"));
        }
    }

    private static List<HotkeyProblem> ReadHotkeys(AppSettings settings,
        out Dictionary<int, (uint Modifiers, Keys Key)> desired)
    {
        desired = [];
        var problems = new List<HotkeyProblem>();
        var used = new Dictionary<(uint, Keys), AppCommand>();
        foreach (AppCommand command in CommandCatalog.All)
        {
            string value = settings.Hotkeys.GetValueOrDefault(command, "");
            if (value.Length == 0)
                continue;

            string name = CommandCatalog.Name(command);
            if (!HotkeyText.TryParse(value, out uint modifiers, out Keys key))
                problems.Add(new HotkeyProblem(command,
                    string.Format(T("errors.hotkeys.invalid"), name, value)));
            else if (used.TryGetValue((modifiers, key), out AppCommand first))
                problems.Add(new HotkeyProblem(command,
                    string.Format(T("errors.hotkeys.duplicate"), CommandCatalog.Name(first), name, value)));
            else if (HotkeyText.TypedText(modifiers, key) is { } typed)
                problems.Add(new HotkeyProblem(command,
                    string.Format(T("errors.hotkeys.typesText"), name, value,
                        typed)));
            else
            {
                used[(modifiers, key)] = command;
                desired[(int)command] = (modifiers, key);
            }
        }

        return problems;
    }

    private static HotkeyProblem Taken(AppCommand command, AppSettings settings) => new(command,
        T("hotkeys.conflictPrefix") + $"{settings.Hotkeys[command]} ({CommandCatalog.Name(command)})");

    private static string Describe(IEnumerable<HotkeyProblem> problems) =>
        string.Join(Environment.NewLine, problems.Select(problem => problem.Message));

    private bool Paused => _settings.PauseInFullScreen && FullScreen.IsActive();

    private FullScreenAction FullScreenDecision(AppCommand command, nint window)
    {
        bool targetHasView = command switch
        {
            AppCommand.OpenPip => _pips.ContainsKey(window),
            AppCommand.ToggleShrink => _shrunk.ContainsKey(window),
            AppCommand.ToggleAnchor => _anchors.Has(window),
            _ => false,
        };
        return FullScreenPolicy.Decide(command, FullScreen.Current(), _settings.PauseInFullScreen,
            _settings.AllowPipInFullScreen, targetHasView, _viewsInOrder.Count > 0, _windows.IsBorderless(window));
    }

    private bool CanUseToolbar(nint window)
    {
        return !Paused && _windows.CanControl(window);
    }

    private bool IsAvailable(AppCommand command) =>
        _settings.ExperimentalFeatures || !CommandCatalog.IsExperimental(command);

    private bool IsOn(AppCommand command, nint window)
    {
        return command switch
        {
            AppCommand.TogglePin => _windows.IsPinned(window),
            AppCommand.ToggleClickThrough => _windows.IsClickThrough(window),
            AppCommand.OpenPip => _pips.ContainsKey(window),
            AppCommand.ToggleDark => _dark.IsDark(window),
            AppCommand.ToggleGlass => _windows.IsGlass(window),
            AppCommand.ToggleAnchor => _anchors.Has(window),
            AppCommand.ToggleDefaultOpacity => _windows.OpacityOf(window) < 255,
            AppCommand.SaveWindowSetup => _settings.SetupForWindow(window) is not null,
            AppCommand.ToggleShrink => _shrunk.ContainsKey(window),
            AppCommand.ToggleCrop => _windows.IsCropped(window),
            AppCommand.ToggleRollUp => _windows.IsRolledUp(window),
            AppCommand.ToggleBorderless or AppCommand.ToggleBorderlessFill => _windows.IsBorderless(window),
            AppCommand.ToggleDarkTitleBar => _windows.IsDarkTitleBar(window),
            _ => false,
        };
    }

    /// <summary>A hotkey pressed on a PiP view or Shrink tile means the window that view stands for.</summary>
    private nint TargetOf(nint foreground)
    {
        foreach ((nint source, PipWindow view) in _pips)
            if (view.IsHandleCreated && view.Handle == foreground)
                return source;
        foreach ((nint source, ShrinkTile tile) in _shrunk)
            if (tile.IsHandleCreated && tile.Handle == foreground)
                return source;
        return foreground;
    }

    private void Watch()
    {
        nint foreground = User32.GetForegroundWindow();
        if (_windows.CanControl(foreground))
        {
            if (_lastIdentity is null || _lastIdentity.Window != foreground || !_lastIdentity.IsSameWindow())
            {
                try
                {
                    _lastIdentity = WindowIdentity.Observe(foreground);
                }
                catch (WindowOperationException)
                {
                    _lastIdentity = null;
                }
            }

            ApplySavedSetup(foreground);
        }

        FollowModifierHook(foreground);
        _windows.Refresh(Cursor.Position, _settings.OpaqueOnHover, _settings.PausePinWhenMaximized);
        _borders.Show(_settings.HighlightPinned ? _windows.VisiblePinnedWindows() : []);
    }

    /// <summary>
    /// Keeps the mouse hook only for as long as it could do anything. With the feature off this is one
    /// comparison, and the hook's absence is the point: nothing runs behind a game.
    /// </summary>
    private void FollowModifierHook(nint foreground)
    {
        _modifierDrag.OpacityWheelEnabled = _settings.OpacityWheel;
        bool fallbackMenu = _windowMenu is { IsDisposed: false, NeedsHook: true };
        if (!_settings.ModifierDrag && !_settings.WindowMenu && !_settings.OpacityWheel && !fallbackMenu)
        {
            _modifierDrag.SetActive(false);
            return;
        }

        if (foreground != _hookForeground)
        {
            _hookForeground = foreground;
            _hookForegroundExcluded = foreground != 0 && IsDragExcluded(foreground);
        }

        _modifierDrag.Key = _settings.ModifierKey;
        _modifierDrag.MoveEnabled = _settings.ModifierDrag;
        _modifierDrag.MenuEnabled = _settings.WindowMenu;
        _modifierDrag.MenuGesture = _settings.WindowMenuGesture;
        _modifierDrag.OpacityWheelEnabled = _settings.OpacityWheel;
        string program = WindowInfo.ProgramOf(foreground);
        bool programExcluded = _settings.ExcludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) ||
            (_settings.IncludedPrograms.Count > 0 && !_settings.IncludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase));
        bool menuExcluded = programExcluded ||
                            _settings.WindowMenuExclusions.Contains(program, StringComparer.OrdinalIgnoreCase);
        bool run = ModifierHookPolicy.ShouldRun(
            (_settings.ModifierDrag && !_hookForegroundExcluded) ||
            ((_settings.WindowMenu || fallbackMenu) && !menuExcluded) ||
            (_settings.OpacityWheel && !programExcluded),
            FullScreen.Current(), WindowInfo.FillsMonitor(foreground), false, FullScreen.IsAway());
        if (!run && _windowMenu is { IsDisposed: false }) _windowMenu.Close();
        Point cursor = Cursor.Position;
        // Windows drops a slow hook without saying so. Input with no callback means it has, so put it back.
        if (run && _modifierDrag.Active && cursor != _hookCursor &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_modifierDrag.LastCallback).TotalSeconds > HookSilenceSeconds)
            _modifierDrag.SetActive(false);
        _hookCursor = cursor;
        try
        {
            _modifierDrag.SetActive(run);
            _hookReported = false;
        }
        catch (System.ComponentModel.Win32Exception failure)
        {
            if (!_hookReported)
                Alert(T("errors.operation.title"), failure.Message);
            _hookReported = true;
        }
    }

    private bool IsDragExcluded(nint window)
    {
        string program = WindowInfo.ProgramOf(window);
        return _settings.ExcludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) ||
               (_settings.IncludedPrograms.Count > 0 && !_settings.IncludedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase)) ||
               _settings.ModifierExclusions.Contains(program, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asked inside the mouse hook, only for a press with the modifier held, so it can afford these calls.</summary>
    private bool CanDrag(nint window)
    {
        if (!CanGesture(window) ||
            User32.IsZoomed(window) || User32.IsIconic(window) || WindowInfo.FillsMonitor(window))
            return false;
        User32.GetWindowThreadProcessId(window, out uint process);
        return !ProcessInfo.IsElevated(process) && !_settings.ModifierExclusions.Contains(WindowInfo.ProcessName(process),
            StringComparer.OrdinalIgnoreCase);
    }

    private bool CanGesture(nint window)
    {
        if (_windows.Refusal(window) is not null || _windows.GestureBlocked(window)) return false;
        User32.GetWindowThreadProcessId(window, out uint process);
        return !ProcessInfo.IsElevated(process);
    }

    private bool CanOpenWindowMenu(nint window) => CanGesture(window) && !User32.IsIconic(window) &&
        !WindowInfo.FillsMonitor(window) && !_settings.WindowMenuExclusions.Contains(WindowInfo.ProgramOf(window),
            StringComparer.OrdinalIgnoreCase);

    private bool CanChangeOpacity(nint window) => FullScreen.Current() == FullScreenState.None &&
        !FullScreen.IsAway() && !User32.IsIconic(window) && !WindowInfo.FillsMonitor(window) && CanGesture(window);

    /// <summary>
    /// Gives a window its program's saved setup the first time it comes forward, and never again: pinning
    /// needs a window that is forward, and whatever the person changes after that must stay changed.
    /// </summary>
    private void ApplySavedSetup(nint window)
    {
        if (Paused || !WindowInfo.IsMainWindow(window) ||
            _setupRetryAt.GetValueOrDefault(window) > Environment.TickCount64)
            return;
        if (_setupDone.TryGetValue(window, out var prior) && prior.Matches())
            return;
        foreach (var stale in _setupDone.Where(pair => !pair.Value.Matches()).Select(pair => pair.Key).ToArray())
        {
            _setupDone.Remove(stale);
            _setupRetryAt.Remove(stale);
        }

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
        catch (Exception failure) when (IsExpected(failure))
        {
            _setupRetryAt[window] = Environment.TickCount64 + 5000;
            Alert(T("errors.operation.title"), failure.Message);
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

        List<ProgramSetup> before = _settings.SavedSetups.ToList();
        bool hadSetup = _settings.SavedSetups.RemoveAll(saved =>
            (!string.IsNullOrEmpty(setup.ExecutablePath) && string.Equals(saved.ExecutablePath, setup.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)) || (string.IsNullOrEmpty(saved.ExecutablePath) &&
                                                         string.Equals(saved.Program, program,
                                                             StringComparison.OrdinalIgnoreCase))) > 0;
        if (!setup.ChangesAnything && !hadSetup)
        {
            Alert(T("setups.nothingToRemember"), T("setups.changeWindowFirst"), ToolTipIcon.Info);
            return;
        }

        if (setup.ChangesAnything)
            _settings.SavedSetups.Add(setup);
        try
        {
            _settings.Save(_settingsPath);
        }
        catch
        {
            _settings.SavedSetups = before;
            throw;
        }

        _setupDone[window] = WindowIdentity.Capture(window);
        Confirm(setup.ChangesAnything ? T("setups.saved") : T("setups.removed"),
            setup.ChangesAnything ? setup.ToString() : program);
    }

    private void Shrink(nint window)
    {
        if (_shrunk.TryGetValue(window, out var existing))
        {
            existing.Close();
            return;
        }

        if (_pips.Remove(window, out var preview)) preview.Close();
        var tile = new ShrinkTile(window, _windows);
        _shrunk[window] = tile;
        tile.FormClosed += (_, _) => _shrunk.Remove(window);
        tile.Problem += text => Alert(T("errors.operation.title"), text);
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
            // The overlay has already closed by now; only a cancelled selection has stopped being current.
            if (_cropping != overlay) return;
            _cropping = null;
            _croppingWindow = 0;
            try
            {
                identity.Require();
                _windows.Crop(window, part);
                User32.SetForegroundWindow(window);
            }
            catch (Exception failure) when (IsExpected(failure))
            {
                Alert(T("errors.operation.title"), failure.Message);
            }
        };
        _cropping = overlay;
        _croppingWindow = window;
        try
        {
            overlay.ShowOnTop();
        }
        catch
        {
            CancelCrop();
            throw;
        }
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

    private async Task CheckForUpdates()
    {
        string result = !VelopackUpdateSession.IsInstalled
            ? T("updates.installedOnly")
            : !_settings.AutomaticUpdates
                ? T("updates.enableFirst")
                : await _updates.CheckNow();
        Balloon(T("updates.title"), result, ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        // Windows go back first: if that fails WinScreen stays running, with its views still in place.
        CancelCrop();
        if (_windows.RestoreAll() != 0)
        {
            Alert(T("recovery.attention"),
                T(
                    "errors.app.exitRestoreFailed") +
                Environment.NewLine + string.Join(Environment.NewLine, _windows.RestoreFailures));
            return;
        }

        foreach (PipWindow preview in _viewsInOrder.ToArray()) preview.Close();
        foreach (var tile in _shrunk.Values.ToArray()) tile.Close();
        _dark.TurnAllOff();
        _anchors.RemoveAll();
        _updateTimer.Dispose();
        _watch.Dispose();
        _borders.Dispose();
        _dark.Dispose();
        _anchors.Dispose();
        _windowMenu?.Close();
        _modifierDrag.Dispose();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
            form.Close();
        _toolbar.Dispose();
        _flash.Dispose();
        Task.Run(_updates.ApplyOnExit).GetAwaiter().GetResult();
        _updates.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private void Run(AppCommand command, nint window)
        => RunOnDisplay(command, window, null);

    private void RunOnDisplay(AppCommand command, nint window, Rectangle? display)
    {
        if (command == AppCommand.ShowWindowMenu)
        {
            Point point = Cursor.Position;
            nint underPointer = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);
            ShowWindowMenu(underPointer, point);
            return;
        }
        if (command == AppCommand.RestoreWindow &&
            (window == 0 || !_windows.CanControl(window)) && _lastIdentity?.IsSameWindow() == true)
        {
            User32.GetWindowThreadProcessId(window, out uint pid);
            if (window == 0 || pid == Environment.ProcessId) window = _lastIdentity.Window;
        }

        bool recovery = command is AppCommand.RestoreAll or AppCommand.RestoreWindow;
        switch (recovery ? FullScreenAction.Run : FullScreenDecision(command, window))
        {
            case FullScreenAction.CloseLatestView:
                _viewsInOrder[^1].Close();
                return;
            case FullScreenAction.ExplainExclusive:
                Alert(T("errors.operation.title"),
                    T("errors.fullScreen.exclusiveView"));
                return;
            case FullScreenAction.Refuse:
                Alert(T("errors.operation.title"), T("errors.fullScreen.paused"));
                return;
        }

        if (command == AppCommand.PickPipSource)
        {
            ShowPicker();
            return;
        }

        if (!recovery && _windows.Refusal(window) is { } refusal)
        {
            Alert(T("errors.operation.title"), refusal);
            return;
        }

        if (CommandCatalog.AnchorOf(command) is { } anchor)
        {
            Attempt(() => _placer.Align(window, anchor));
            return;
        }

        if (!recovery && !IsAvailable(command))
        {
            Alert(T("errors.operation.title"), T("errors.experimental.enableFirst"));
            return;
        }

        try
        {
            switch (command)
            {
                case AppCommand.TogglePin:
                    Confirm(_windows.TogglePin(window) ? T("status.pinned") : T("status.unpinned"), WindowInfo.Title(window));
                    break;
                case AppCommand.OpacityUp:
                    ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(1)));
                    break;
                case AppCommand.OpacityDown:
                    ShowOpacity(window, _windows.ChangeOpacity(window, OpacityDelta(-1)));
                    break;
                case AppCommand.ToggleDefaultOpacity:
                    ShowOpacity(window, _windows.ToggleDefaultOpacity(window));
                    break;
                case AppCommand.ToggleClickThrough:
                    if (_windows.ToggleClickThrough(window))
                        Alert(T("status.clickThroughOn"),
                            T("window.clickThrough.recoveryHelp"),
                            ToolTipIcon.Info);
                    else
                        Confirm(T("status.clickThroughOff"), WindowInfo.Title(window));
                    break;
                case AppCommand.OpenPip:
                    if (_pips.TryGetValue(window, out var existing))
                    {
                        existing.Close();
                        break;
                    }

                    if (_shrunk.Remove(window, out var tile)) tile.Close();
                    var pip = new PipWindow(window, _windows);
                    _pips[window] = pip;
                    pip.FormClosed += (_, _) => _pips.Remove(window);
                    OpenView(pip);
                    break;
                case AppCommand.Arrange:
                    ShowArrangeMenu(window);
                    break;
                case AppCommand.MoveToNextDisplay:
                    _placer.MoveToNextDisplay(window);
                    break;
                case AppCommand.PlaceExactly:
                    PlaceExactly(window);
                    break;
                case AppCommand.RestorePlacement:
                    if (!_placer.RestorePrior(window))
                        Alert(CommandCatalog.Name(command), T("errors.placement.unchanged"), ToolTipIcon.Info);
                    break;
                case AppCommand.SendToBottom:
                    _windows.SendToBottom(window);
                    break;
                case AppCommand.ShowWindowInfo:
                    ShowWindowInfo(window);
                    break;
                case AppCommand.ToggleRollUp:
                    _windows.ToggleRollUp(window);
                    break;
                case AppCommand.ToggleBorderless:
                case AppCommand.ToggleBorderlessFill:
                    _windows.ToggleBorderless(window, command == AppCommand.ToggleBorderlessFill, display);
                    break;
                case AppCommand.ToggleDarkTitleBar:
                    _windows.ToggleDarkTitleBar(window);
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
                    foreach (PipWindow preview in _viewsInOrder.ToArray()) preview.Close();
                    foreach (var shrunk in _shrunk.Values.ToArray()) shrunk.Close();
                    _dark.TurnAllOff();
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
                    break;
                case AppCommand.RestoreWindow:
                    if (!HasAnythingToRestore(window))
                    {
                        Alert(T("window.restore"), T("errors.window.unchanged"), ToolTipIcon.Info);
                        break;
                    }

                    if (_croppingWindow == window) CancelCrop();
                    if (_pips.Remove(window, out var view)) view.Close();
                    if (_shrunk.Remove(window, out var shrunkTile)) shrunkTile.Close();
                    if (_dark.IsDark(window)) _dark.Toggle(window);
                    if (_anchors.Has(window)) _anchors.Toggle(window);
                    if (_windows.RestoreWindow(window))
                        Confirm(T("window.restore"), T("recovery.restored"));
                    else
                        Alert(T("window.restore"), string.Join(Environment.NewLine, _windows.RestoreFailures));
                    break;
            }
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    private void Attempt(Action action)
    {
        try
        {
            action();
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    /// <summary>Everything that moves a window, from one button, so the toolbar doesn't need a button for each.</summary>
    private void ShowArrangeMenu(nint window)
    {
        var menu = new ContextMenuStrip();
        foreach (AppCommand? entry in CommandCatalog.ArrangeMenu)
        {
            if (entry is not { } command)
                menu.Items.Add(new ToolStripSeparator());
            else if (IsAvailable(command))
                menu.Items.Add(CommandCatalog.Name(command), null, (_, _) => Run(command, window));
        }

        // A menu only closes on an outside click while its program has the focus.
        menu.Closed += (_, _) =>
        {
            User32.SetForegroundWindow(window);
            BeginInvokeDispose(menu);
        };
        User32.SetForegroundWindow(_toolbar.Handle);
        menu.Show(Cursor.Position);
    }

    private void BeginInvokeDispose(IDisposable item) => _toolbar.BeginInvoke(item.Dispose);

    private void PlaceExactly(nint window)
    {
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _))
            return;
        using var dialog = new PlacementDialog(CommandCatalog.Name(AppCommand.PlaceExactly), frame);
        if (dialog.ShowDialog() == DialogResult.OK)
            _placer.SetFrame(window, dialog.Frame);
    }

    private static void ShowWindowInfo(nint window)
    {
        string text = WindowUtilities.Information(window);
        using var dialog = new Form
        {
            Text = CommandCatalog.Name(AppCommand.ShowWindowInfo), Size = new Size(620, 340),
            StartPosition = FormStartPosition.CenterScreen, TopMost = true,
        };
        var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Text = text, ScrollBars = ScrollBars.Both, WordWrap = false };
        var copy = new Button { Text = T("common.copy"), AutoSize = true };
        copy.Click += (_, _) => WindowUtilities.CopyText(text);
        string path = WindowInfo.ExecutablePath(window);
        var folder = new Button { Text = T("window.openLocation"), AutoSize = true, Enabled = path.Length > 0 };
        folder.Click += (_, _) => WindowUtilities.OpenLocation(window);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange([copy, folder]);
        dialog.Controls.Add(box);
        dialog.Controls.Add(buttons);
        dialog.ShowDialog();
    }

    private void OpenView(PipWindow pip)
    {
        pip.AllowClickForwarding = _settings.ExperimentalFeatures && _settings.PipClickForwarding;
        pip.AllowAutoMove = _settings.ExperimentalFeatures;
        pip.AllowSwitcherHiding = _settings.ExperimentalFeatures;
        _viewsInOrder.Add(pip);
        pip.FormClosed += (_, _) => _viewsInOrder.Remove(pip);
        pip.Problem += text => Alert(T("errors.operation.title"), text);
        pip.Notice += text => Alert(T("pip.open"), text, ToolTipIcon.Info);
        pip.Presets = () => _settings.PipPresets;
        pip.PresetSaved += preset => ChangePresets(presets =>
        {
            presets.RemoveAll(saved => saved.Name == preset.Name);
            presets.Add(preset);
        });
        pip.PresetRemoved += name => ChangePresets(presets => presets.RemoveAll(saved => saved.Name == name));
        pip.ApplyPipPreferences(_settings.Pip);
        pip.ApplyViewPreferences(_settings.PipViews);
        pip.ShowOnTop();
        if (pip.IsDisposed) return;
        _dark.Ignore(pip.Handle);
        if (_settings.ExperimentalFeatures && _settings.PipAutoMove)
            pip.SetAutoMove(true);
    }

    private void ChangePresets(Action<List<PipPreset>> change)
    {
        List<PipPreset> before = _settings.PipPresets.ToList();
        change(_settings.PipPresets);
        try
        {
            _settings.Save(_settingsPath);
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            _settings.PipPresets = before;
            Alert(T("errors.settings.saveFailed"), failure.Message);
        }
    }

    private void OpenScreenView(ScreenPipSource source)
    {
        try
        {
            OpenView(new PipWindow(source, _windows));
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    private void ShowPicker()
    {
        if (_picker is { IsDisposed: false })
        {
            _picker.Activate();
            return;
        }

        var picker = new PipSourcePicker(window => _windows.CanControl(window) && !_pips.ContainsKey(window));
        picker.WindowChosen += window => Run(AppCommand.OpenPip, window);
        picker.ScreenChosen += device => OpenScreenView(new ScreenPipSource(device, null));
        picker.RegionWanted += device => Attempt(() => SelectRegion(device));
        _picker = picker;
        picker.FormClosed += (_, _) => _picker = null;
        picker.Show();
        picker.Activate();
    }

    private void SelectRegion(string device)
    {
        if (_cropping is { IsDisposed: false })
            return;
        var source = new ScreenPipSource(device, null);
        var overlay = new CropOverlay(source.Screen.Bounds);
        overlay.Chosen += part =>
        {
            if (_cropping != overlay) return;
            _cropping = null;
            OpenScreenView(new ScreenPipSource(device, part));
        };
        _cropping = overlay;
        try
        {
            overlay.ShowOnTop();
        }
        catch
        {
            CancelCrop();
            throw;
        }
    }

    /// <summary>Failures a command can meet in normal use, such as a locked settings file or a refused screen capture.</summary>
    private static bool IsExpected(Exception failure) => failure is WindowOperationException or IOException
        or UnauthorizedAccessException or System.Security.SecurityException or ExternalException;

    private bool HasAnythingToRestore(nint window) =>
        _windows.HasChanges(window) || _pips.ContainsKey(window) || _shrunk.ContainsKey(window) ||
        _dark.IsDark(window) || _anchors.Has(window) || _croppingWindow == window;

    private int OpacityDelta(int steps) => steps * (int)Math.Round(Math.Clamp(_settings.OpacityStepPercent, 1, 25) * 255 / 100.0);

    private void ShowOpacity(nint window, byte alpha)
    {
        if (WindowInfo.TryGetVisibleBounds(window, out Rectangle bounds))
            _flash.ShowOver(bounds, string.Format(T("window.opacity.percent"), (int)Math.Round(alpha * 100.0 / 255)));
    }

    /// <summary>Reports a routine success, which someone can choose not to be told about.</summary>
    private void Confirm(string title, string text)
    {
        Remember(title, text);
        if (_settings.ShowNotifications)
            Balloon(title, text, ToolTipIcon.None);
    }

    /// <summary>Reports something that didn't happen or needs a decision. It's always shown, or it would look like nothing happened.</summary>
    private void Alert(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        Remember(title, text);
        Balloon(title, text, icon);
    }

    private void Remember(string title, string text)
    {
        _lastStatus = title + ": " + text;
        ShowStatus();
    }

    private void ShowStatus() => _statusItem.Text = T("tray.resultPrefix") + MenuText(_lastStatus);

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(BalloonMilliseconds, title, string.IsNullOrEmpty(text) ? " " : text, icon);

    /// <summary>Keeps the tray menu a sensible width whatever a window calls itself. "&amp;" would otherwise vanish as a shortcut mark.</summary>
    private static string MenuText(string text)
    {
        string flat = text.ReplaceLineEndings(" ");
        if (flat.Length > MenuTextLimit)
        {
            int end = char.IsHighSurrogate(flat[MenuTextLimit - 1]) ? MenuTextLimit - 1 : MenuTextLimit;
            flat = flat[..end] + "…";
        }

        return flat.Replace("&", "&&");
    }

    private void CancelCrop()
    {
        var overlay = _cropping;
        _cropping = null;
        _croppingWindow = 0;
        overlay?.Close();
    }

    internal static Icon LoadIcon()
    {
        string? path = Environment.ProcessPath;
        return (path is null ? null : Icon.ExtractAssociatedIcon(path)) ?? SystemIcons.Application;
    }
}
