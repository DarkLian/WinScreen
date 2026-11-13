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
public sealed partial class TrayApplication : ApplicationContext
{
    private const int MenuTextLimit = 60;
    private const int BalloonMilliseconds = 3000;
    private const int HookSilenceSeconds = 2;

    private readonly WindowController _windows;
    private readonly WindowPlacer _placer;
    private readonly CommandTable _commands;
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
    private readonly AutomaticAnchors _automaticAnchors;
    private readonly RecoveryWatcher _recoveryWatcher = new();
    private readonly Glass.ContentEffects _content;
    private readonly PinBorders _borders = new();
    private readonly ActionFlash _flash = new();
    private readonly SnapPreview _snapPreview = new();
    private readonly Dictionary<nint, ShrinkTile> _shrunk = [];
    private readonly Dictionary<nint, PipWindow> _pips = [];
    private readonly List<PipWindow> _viewsInOrder = [];
    private readonly System.Windows.Forms.Timer _menuGuard = new() { Interval = 100 };
    private readonly Dictionary<nint, WindowIdentity> _setupDone = [];
    private readonly Dictionary<nint, WindowIdentity> _setupNone = [];
    private readonly SavedViews _savedViews = new();
    private readonly Dictionary<nint, long> _setupRetryAt = [];
    private readonly ToolStripMenuItem _targetTitle = new();
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
    private bool _updatesWereOn;
    private SettingsWindow? _settingsWindow;
    private PipSourcePicker? _picker;
    private CropOverlay? _cropping;
    private WindowMenuHost? _windowMenu;

    public TrayApplication(WindowController windows, AppSettings settings, string settingsPath)
    {
        _windows = windows;
        _placer = new WindowPlacer(windows);
        _commands = new CommandTable(CommandAreas());
        _modifierDrag = new MouseGestures(CanDrag, _placer.Remember);
        _settings = settings;
        Pip.PipHoverApps.Preferences = settings.PipHover;
        Pip.PipOriginalApps.Programs = settings.PipOriginalPrograms;
        _settingsPath = settingsPath;
        // Update news stays out of Last result, where it could replace a failure someone needs to read.
        _updates = new AutomaticUpdates(VelopackUpdateSession.Create, message =>
        {
            if (_settings.ShowNotifications)
                Balloon(T("updates.title"), message, ToolTipIcon.None);
        });
        _toolbar = new WindowToolbar(CanUseToolbar, IsOn, IsAvailable, Run) { OpenMenu = ShowToolbarMenu };
        _ = _toolbar.Handle;
        _modifierDrag.MenuEligible = CanOpenWindowMenu;
        _modifierDrag.MenuRequested = (window, point) => _toolbar.BeginInvoke(() => ShowWindowMenu(window, point));
        _modifierDrag.OutsidePressed = point => _toolbar.BeginInvoke(() => _windowMenu?.OutsidePress(point));
        _dark.Ignore(_toolbar.Handle);
        _ = _flash.Handle;
        _dark.Ignore(_flash.Handle);
        _ = _snapPreview.Handle;
        _dark.Ignore(_snapPreview.Handle);
        _modifierDrag.SnapPreview = area => _toolbar.BeginInvoke(() => _snapPreview.ShowAt(area));
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
        _windows.ConcealmentChanged += _recoveryWatcher.Follow;
        _content = new Glass.ContentEffects(_windows);
        _content.Problem += text => Alert(T("errors.operation.title"), text);
        _content.Created += handle => _dark.Ignore(handle);
        _automaticAnchors = new AutomaticAnchors(_anchors, CanHaveAutomaticAnchor, () => Paused || FullScreen.IsActive());
        _anchors.Created += _dark.Ignore;
        _anchors.HasView = _pips.ContainsKey;
        _anchors.SetOffTaskbar = (window, off) =>
        {
            if (off) _windows.TakeOffTaskbar(window);
            else _windows.PutBackOnTaskbar(window);
        };
        _anchors.IsOffTaskbar = _windows.IsOffTaskbar;
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
        // A view being used directly has its original under it, ready for clicks. Opening WinScreen's own
        // menu or Settings ends that first, and they stay above every view.
        _tray.ContextMenuStrip.Opening += (_, _) => EndVisits();
        WinScreen.Windows.TopLayer.Above = AppWindowsAbove;
        // A backstop for anything lifted to the top while the menu is open, by WinScreen or anyone else.
        _menuGuard.Tick += (_, _) => WinScreen.Windows.TopLayer.KeepAbove();
        _tray.ContextMenuStrip.Opened += (_, _) => _menuGuard.Start();
        _tray.ContextMenuStrip.Closed += (_, _) => _menuGuard.Stop();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };
        _tray.Visible = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += FollowWindowsTheme;
        ShowStatus();

        bool firstRun = !_settings.WelcomeShown;
        IReadOnlyList<HotkeyProblem> problems = StartUp();
        // A built-in shortcut that types a letter on this keyboard is explained once, not at every sign-in.
        if (!firstRun)
            problems = problems.Where(problem => !TypesTextByDefault(problem.Command)).ToArray();
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
        var newView = new ToolStripMenuItem(T("menu.tray.newView"), MenuParts.Icon(MenuParts.NewWindow),
            (_, _) => Run(AppCommand.PickPipSource, 0));
        menu.Items.Add(newView);
        // Open views are listed here directly, between these two headings, each time the menu opens.
        ToolStripLabel viewsStart = MenuParts.Header(T("tray.openViews")), viewsEnd = MenuParts.Header(T("tray.lastWindow"));
        menu.Items.Add(viewsStart);
        menu.Items.Add(viewsEnd);
        _targetTitle.Image = MenuParts.Icon(MenuParts.Page);
        menu.Items.Add(_targetTitle);
        var builder = CreateWindowMenuBuilder(() => _menuIdentity);
        builder.Populate(_targetTitle.DropDownItems, true);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(T("window.restoreAll"), MenuParts.Icon(MenuParts.Undo), (_, _) => Run(AppCommand.RestoreAll, 0)));
        var everywhere = CommandCatalog.Everywhere.ToDictionary(command => command, command =>
            new ToolStripMenuItem(CommandCatalog.Name(command), MenuParts.IconOf(command) is { } glyph ? MenuParts.Icon(glyph) : null,
                (_, _) => Run(command, 0)));
        foreach (ToolStripMenuItem item in everywhere.Values) menu.Items.Add(item);
        _statusItem.Image = MenuParts.Icon(MenuParts.Warning);
        menu.Items.Add(_statusItem);
        _statusItem.Click += (_, _) =>
            MessageBox.Show(_lastStatus, "WinScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(T("settings.title"), MenuParts.Icon(MenuParts.Settings), (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem(T("updates.check"), MenuParts.Icon(MenuParts.Sync), async (_, _) => await CheckForUpdates()));
        menu.Items.Add(new ToolStripMenuItem(T("app.saveDiagnostics"), MenuParts.Icon(MenuParts.Save), (_, _) => SaveDiagnostics()));
        menu.Items.Add(new ToolStripMenuItem(T("app.about"), MenuParts.Icon(MenuParts.Info), (_, _) => MessageBox.Show(
            string.Format(T("app.version"), AppInfo.Version), "WinScreen", MessageBoxButtons.OK,
            MessageBoxIcon.Information)));
        menu.Items.Add(new ToolStripMenuItem(T("app.exit"), MenuParts.Icon(MenuParts.Power), (_, _) => ExitThread()));

        void Ticks()
        {
            builder.Refresh(_targetTitle.DropDownItems);
            everywhere[AppCommand.ClickThroughOffEverywhere].Available = _windows.ClickThroughCount > 0;
            everywhere[AppCommand.UnpinEverywhere].Available = _windows.PinnedCount > 0;
            everywhere[AppCommand.HideAnchors].Checked = _anchors.Hidden;
            everywhere[AppCommand.HideToolbar].Checked = _toolbarHidden;
        }
        StayingOpen.Follow(menu, Ticks);
        menu.Opening += (_, _) =>
        {
            _menuIdentity = _lastIdentity?.IsSameWindow() == true ? _lastIdentity : null;
            if (_menuIdentity is { } selected)
            {
                try { _menuIdentity = WindowIdentity.Capture(selected.Window); }
                catch (WindowOperationException) { _menuIdentity = null; }
            }
            _menuWindow = _menuIdentity?.Window ?? 0;
            _targetTitle.Text = _menuWindow == 0 ? T("tray.noTarget") : MenuText(WindowInfo.Title(_menuWindow));
            _targetTitle.Enabled = _menuWindow != 0;
            builder.Refresh(_targetTitle.DropDownItems);
            newView.ShortcutKeyDisplayString = MenuHotkey(AppCommand.PickPipSource);
            foreach ((AppCommand command, ToolStripMenuItem item) in everywhere)
                item.ShortcutKeyDisplayString = MenuHotkey(command);
            // Only offered while there is something to take away; the two that hide stay, ticked while hidden.
            everywhere[AppCommand.ClickThroughOffEverywhere].Available = _windows.ClickThroughCount > 0;
            everywhere[AppCommand.UnpinEverywhere].Available = _windows.PinnedCount > 0;
            everywhere[AppCommand.HideAnchors].Checked = _anchors.Hidden;
            everywhere[AppCommand.HideToolbar].Checked = _toolbarHidden;
            // Nothing to read until something has been reported.
            _statusItem.Available = _lastStatus != T("tray.noActions");
            for (int index = menu.Items.IndexOf(viewsEnd) - 1; index > menu.Items.IndexOf(viewsStart); index--)
            {
                ToolStripItem old = menu.Items[index];
                menu.Items.RemoveAt(index);
                old.Dispose();
            }
            int at = menu.Items.IndexOf(viewsEnd);
            foreach (ToolStripItem item in ViewItems()) menu.Items.Insert(at++, item);
        };
        MenuRenderer.Apply(menu);
        return menu;
    }

    /// <summary>The hotkey to show beside a command in a menu: nothing when it has none.</summary>
    private string? MenuHotkey(AppCommand command) =>
        _settings.Hotkeys.TryGetValue(command, out string? keys) && !string.IsNullOrWhiteSpace(keys) ? keys : null;

    private WindowMenuBuilder CreateWindowMenuBuilder(Func<WindowIdentity?> target) => new(target,
        Run, (window, bounds) => RunOnDisplay(AppCommand.ToggleBorderlessFill, window, bounds), IsAvailable,
        (command, window) => command == AppCommand.RestoreWindow ||
                             FullScreenDecision(command, window) == FullScreenAction.Run && _windows.CanControl(window),
        IsOn, () => Alert(T("errors.operation.title"),
            T("errors.window.targetUnavailable")))
        {
            MoveToDisplay = (window, device) => Attempt(() => _placer.MoveToDisplay(window, device)),
            Hotkey = command => MenuHotkey(command) ?? "",
            OpacityPercent = window => (int)Math.Round(_windows.OpacityOf(window) * 100.0 / 255),
            SetOpacityPercent = (window, percent) => Attempt(() =>
            {
                if (FullScreenDecision(AppCommand.OpacityUp, window) != FullScreenAction.Run || !_windows.CanControl(window)) return;
                _windows.SetOpacity(window, (byte)Math.Round(percent * 255 / 100.0));
            }),
        };

    private void ShowWindowMenu(nint window, Point point)
    {
        if (!CanOpenWindowMenu(window)) return;
        ShowHostedMenu(window, point, AppCommand.ShowWindowMenu, (menu, builder) =>
        {
            menu.Items.Add(new ToolStripMenuItem(MenuText(WindowInfo.Title(window))) { Enabled = false, Name = MenuRenderer.TitleName });
            menu.Items.Add(new ToolStripSeparator());
            builder.Populate(menu.Items, false);
        });
    }

    /// <summary>
    /// Opens a window's menu where it is asked for, from a gesture, a hotkey or a toolbar button. The menu is held by
    /// a window of its own that takes the keyboard, so it stays open whatever happens to the toolbar meanwhile.
    /// </summary>
    private void ShowHostedMenu(nint window, Point point, AppCommand asked, Action<ContextMenuStrip, WindowMenuBuilder> fill)
    {
        if (_windowMenu is { IsDisposed: false } || FullScreenDecision(asked, window) != FullScreenAction.Run) return;
        Attempt(() =>
        {
            WindowIdentity identity = WindowIdentity.Capture(window);
            var menu = new ContextMenuStrip();
            var builder = CreateWindowMenuBuilder(() => identity);
            fill(menu, builder);
            builder.Refresh(menu.Items);
            MenuRenderer.Apply(menu);
            StayingOpen.Follow(menu, () => builder.Refresh(menu.Items));
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

    /// <summary>
    /// The toolbar's two menu buttons. Arrange window lists everything that moves the window; Window menu lists what
    /// the window menu offers that isn't a button on this bar already, so nothing shows twice.
    /// </summary>
    private void ShowToolbarMenu(AppCommand command, nint window, Point point)
    {
        if (command == AppCommand.Arrange)
        {
            ShowHostedMenu(window, point, AppCommand.Arrange, (menu, builder) => builder.PopulateArrange(menu.Items));
            return;
        }
        IReadOnlySet<AppCommand> onBar = _toolbar.ShownButtons;
        ShowHostedMenu(window, point, AppCommand.ShowWindowMenu, (menu, builder) =>
        {
            builder.Populate(menu.Items, false);
            WindowMenuBuilder.LeaveOut(menu.Items, onBar);
        });
    }

    /// <summary>The views that are open, which may have no original to act from, and what can be opened again.</summary>
    private List<ToolStripItem> ViewItems()
    {
        var items = new List<ToolStripItem>();
        foreach (PipWindow view in _viewsInOrder)
        {
            var item = new ToolStripMenuItem(view.MenuTitle).WithIcon(MenuParts.Monitor);
            if (view.IsSuspended)
                item.DropDownItems.Add(T("pip.restoreView"), null, (_, _) => view.ResumeView());
            if (view.SourceIsWindow)
                item.DropDownItems.Add(T("pip.goToOriginal"), null, (_, _) => view.GoToSource());
            if (view.Overrides.ClickThrough)
                item.DropDownItems.Add(T("pip.reenableClicks"), null,
                    (_, _) => view.Overrides = view.Overrides with { ClickThrough = false });
            if (view.CanRetryCapture)
                item.DropDownItems.Add(T("pip.backend.retry"), null, (_, _) => view.RetryCapture());
            item.DropDownItems.Add(T("common.close"), null, (_, _) => view.Close());
            items.Add(item);
        }
        foreach (var (source, tile) in _shrunk)
        {
            var item = new ToolStripMenuItem(MenuText(tile.Text) + " - " + T("pip.state.originalHidden"));
            item.DropDownItems.Add(T("window.show"), null, (_, _) => tile.Close());
            items.Add(item);
        }
        foreach (nint source in _content.Sources)
        {
            var item = new ToolStripMenuItem(MenuText(WindowInfo.Title(source)) + " - " + T("settings.effects.title")).Experimental();
            item.DropDownItems.Add(T("window.restore"), null, (_, _) => Run(AppCommand.RestoreWindow, source));
            items.Add(item);
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem(T("pip.views.empty")) { Enabled = false });
        if (_settings.PipBookmarks.Count > 0)
        {
            var saved = new ToolStripMenuItem(T("pip.bookmarks.title")).WithIcon(MenuParts.Star);
            foreach (PipSourceBookmark bookmark in _settings.PipBookmarks)
                saved.DropDownItems.Add(bookmark.Name.Replace("&", "&&"), null, (_, _) => OpenBookmark(bookmark));
            items.Add(saved);
        }
        return items;
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
            _startup.AsAdministrator = _settings.RunAsAdministrator;
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
        string themeBefore = _settings.Theme;
        List<HotkeyProblem> problems = ReadHotkeys(settings, out var desired);
        if (problems.Count > 0)
            return problems;

        object? startupValue = _startup.ReadValue();
        bool adminChanged = settings.RunAsAdministrator != _settings.RunAsAdministrator;
        bool startupChanged = settings.StartWithWindows is { } requestedStartup &&
            (adminChanged || requestedStartup != (startupValue is not null));
        if (!_hotkeys.TryReplaceBindings(desired, () =>
            {
                try
                {
                    if (settings.Imported && File.Exists(_settingsPath))
                    {
                        File.Copy(_settingsPath, _settingsPath + ".before-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                        BackupFiles.Prune(_settingsPath, ".before-import-");
                    }
                    _startup.AsAdministrator = settings.RunAsAdministrator;
                    if (startupChanged) _startup.IsOn = settings.StartWithWindows!.Value;
                    settings.Save(_settingsPath);
                }
                catch
                {
                    _startup.AsAdministrator = _settings.RunAsAdministrator;
                    if (startupChanged) _startup.RestoreValue(startupValue);
                    throw;
                }
            }, out var conflicts))
            return conflicts.Cast<AppCommand>().Select(command => Taken(command, settings)).ToArray();

        AppSettings before = _settings.Clone();
        _settings.CopyFrom(settings);
        _setupNone.Clear();
        ApplyPreferences(experimentalBefore, before);
        Native.Translucency.Allowed = _settings.MenuTransparency;
        if (_settings.Theme != themeBefore) ChangeTheme();
        if (adminChanged && _settings.RunAsAdministrator && !ProcessInfo.SelfElevated)
            _toolbar.BeginInvoke(RestartAsAdministrator); // After Settings has finished saving.
        return [];
    }

    /// <summary>
    /// Hands over to a copy running as administrator, which Windows asks about first. If the person says no, the
    /// choice goes back to off, since it can't be had without that.
    /// </summary>
    private void RestartAsAdministrator()
    {
        if (Elevation.StartElevatedCopy())
        {
            ExitThread();
            return;
        }
        _settings.RunAsAdministrator = false;
        _startup.AsAdministrator = false;
        try { _settings.Save(_settingsPath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(failure); }
        if (_settingsWindow is { IsDisposed: false } open) open.LoadValues(_settings);
        Alert(T("settings.admin"), T("settings.admin.declined"), ToolTipIcon.Info);
    }

    /// <summary>
    /// Changes between light and dark without restarting. Menus follow the next time they open; Settings sets its
    /// colours as it is made, so it is opened again on the same page, once the save that changed the look is done.
    /// </summary>
    private void ChangeTheme()
    {
        bool darkBefore = MenuStyle.Dark;
        MenuStyle.UseTheme(_settings.Theme);
        if (MenuStyle.Dark != darkBefore) ReopenSettings();
    }

    private void ReopenSettings()
    {
        if (_settingsWindow is not { IsDisposed: false, IsHandleCreated: true } old) return;
        old.BeginInvoke(() =>
        {
            if (old.IsDisposed || old.MadeDark == MenuStyle.Dark) return;
            bool active = Form.ActiveForm == old;
            // Showing a minimized window, or a maximized one, always takes the keyboard. So one minimized, or filling
            // the screen behind what is being used, is made again when it is next brought back instead.
            if (old.WindowState == FormWindowState.Minimized || old.FillsScreen && !active)
            {
                ReopenWhenBack(old);
                return;
            }
            int page = old.PageIndex;
            FormWindowState state = old.WindowState;
            Rectangle bounds = state == FormWindowState.Normal ? old.Bounds : old.RestoreBounds;
            SettingsWindow.UnsavedEdits? unsaved = old.Unsaved(); // Windows can change its look while changes wait to be saved.
            // Made again behind whatever is being used when Windows changed its look, rather than taking the keyboard.
            old.Close();
            OpenSettings(bounds, page, reopening: true, unsaved, quietly: !active, state);
        });
    }

    private bool _reopenWhenBack;

    private void ReopenWhenBack(SettingsWindow old)
    {
        if (_reopenWhenBack) return;
        _reopenWhenBack = true;
        void Back(object? sender, EventArgs e)
        {
            // Back from the taskbar or brought forward; one still in the background waits again if it fills the screen.
            if (old.IsDisposed || old.WindowState == FormWindowState.Minimized) return;
            old.Activated -= Back;
            old.SizeChanged -= Back;
            _reopenWhenBack = false;
            ReopenSettings();
        }
        old.Activated += Back;
        old.SizeChanged += Back;
        old.FormClosed += (_, _) => _reopenWhenBack = false;
    }

    /// <summary>Keeps where Settings was and whether it was maximized, for the next time it opens.</summary>
    private void RememberPlacement(SettingsWindow window)
    {
        if (!window.IsHandleCreated) return;
        Rectangle normal = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
        var placement = new SettingsPlacement(normal.X, normal.Y, normal.Width, normal.Height, window.FillsScreen);
        if (placement == _settings.SettingsWindowPlacement) return;
        _settings.SettingsWindowPlacement = placement;
        try { _settings.Save(_settingsPath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(failure); } // Opens centred next time instead.
    }

    /// <summary>With the look set to follow Windows, Settings is made again when Windows changes between light and dark.</summary>
    private void FollowWindowsTheme(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || _settings.Theme is "light" or "dark") return;
        if (MenuStyle.Dark == _wasDark) return;
        _wasDark = MenuStyle.Dark;
        ReopenSettings();
    }

    private bool _wasDark = MenuStyle.Dark;

    /// <summary>
    /// Puts the settings into effect in every part of WinScreen and on every open view. With the settings that were in
    /// effect before, only the parts whose settings changed are touched: Settings applies each change as it is made, and
    /// resetting the toolbar or every view for an unrelated switch would be felt. Each part is put into effect on its
    /// own, so one that fails doesn't leave the others as they were.
    /// </summary>
    private void ApplyPreferences(bool experimentalBefore, AppSettings? before = null)
    {
        bool Changed<T>(Func<AppSettings, T> part) => before is null ||
            System.Text.Json.JsonSerializer.Serialize(part(before)) != System.Text.Json.JsonSerializer.Serialize(part(_settings));
        void Part(Action apply)
        {
            try { apply(); }
            catch (Exception failure) when (IsExpected(failure)) { Alert(T("errors.operation.title"), failure.Message); }
        }
        bool experimentalChanged = before is null || experimentalBefore != _settings.ExperimentalFeatures;
        _hookForeground = 0;
        Part(() =>
        {
            _windows.SetExcluded(_settings.ExcludedPrograms);
            _windows.SetIncluded(_settings.IncludedPrograms);
            _windows.DefaultOpacity = _settings.DefaultAlpha;
            _dark.DetectAlreadyDark = _settings.DetectAlreadyDark;
        });
        if (Changed(settings => settings.PinBorder)) Part(() => _borders.Configure(_settings.PinBorder));
        Part(() => _toolbar.Enable(_settings.ShowToolbar && !_toolbarHidden));
        if (Changed(settings => settings.Toolbar) || experimentalChanged) Part(() => _toolbar.Configure(_settings.Toolbar));
        if (Changed(settings => settings.Anchors)) Part(() =>
        {
            _anchors.Configure(_settings.Anchors);
            _automaticAnchors.Configure(_settings.Anchors.Automatic, _settings.Anchors.AvoidContent);
        });
        if (Changed(settings => settings.Effects)) Part(() => _content.SetLevels(_settings.Effects.Levels));
        if (Changed(settings => settings.HideCaptureBorder)) Part(() =>
        {
            if (_settings.HideCaptureBorder && !Pip.CaptureBorder.Wanted) Pip.CaptureBorder.Forget(); // Ask again: the Windows setting may have changed.
            Pip.CaptureBorder.Wanted = _settings.HideCaptureBorder;
            if (Pip.CaptureBorder.Wanted && Pip.CaptureBorder.Supported()) _ = Pip.CaptureBorder.Prepare();
        });
        Part(() =>
        {
            if (_settings.AutomaticUpdates && !_updatesWereOn)
                _updateTimer.Interval = 30_000; // Check soon after it's turned on, then every few hours.
            _updatesWereOn = _settings.AutomaticUpdates;
            _updates.SetEnabled(_settings.AutomaticUpdates);
        });
        Pip.PipHoverApps.Preferences = _settings.PipHover;
        Pip.PipOriginalApps.Programs = _settings.PipOriginalPrograms;
        bool pipChanged = Changed(settings => settings.Pip), viewsChanged = Changed(settings => settings.PipViews);
        foreach (PipWindow pip in _viewsInOrder.ToArray())
        {
            if (pipChanged) Part(() => pip.ApplyPipPreferences(_settings.Pip));
            if (viewsChanged) Part(() => pip.ApplyViewPreferences(_settings.PipViews));
            if (!experimentalChanged) continue;
            Part(() =>
            {
                pip.AllowAutoMove = _settings.ExperimentalFeatures;
                // Ends direct use, unless this kind of app may be used without the switch; then it only loses
                // Enhanced hover and middle-click scrolling.
                pip.AllowInteractive = _settings.ExperimentalFeatures;
                pip.AllowSwitcherHiding = _settings.ExperimentalFeatures;
                if (!_settings.ExperimentalFeatures) pip.SetAutoMove(false);
            });
        }

        if (experimentalBefore && !_settings.ExperimentalFeatures)
        {
            CancelCrop();
            _dark.TurnAllOff();
            _content.TurnAllOff();
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
            if (CommandCatalog.IsExperimental(command) && !settings.ExperimentalFeatures)
                continue; // Keep the saved choice without reserving a disabled command's keys.
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

    private bool TypesTextByDefault(AppCommand command)
    {
        string value = _settings.Hotkeys.GetValueOrDefault(command, "");
        return value.Length > 0 && value == new AppSettings().Hotkeys.GetValueOrDefault(command) &&
               HotkeyText.TryParse(value, out uint modifiers, out Keys key) && HotkeyText.TypedText(modifiers, key) is not null;
    }

    private static HotkeyProblem Taken(AppCommand command, AppSettings settings) => new(command,
        T("hotkeys.conflictPrefix") + $"{settings.Hotkeys[command]} ({CommandCatalog.Name(command)})");

    private static string Describe(IEnumerable<HotkeyProblem> problems) =>
        string.Join(Environment.NewLine, problems.Select(problem => problem.Message));

    /// <summary>Only windows a person would look for, that are really out of sight and allowed by Settings.</summary>
    private bool CanHaveAutomaticAnchor(nint window) =>
        WindowInfo.IsMainWindow(window) && _windows.CanControl(window) && !WindowInfo.IsConcealed(window) &&
        !_windows.IsHidden(window) && _settings.Anchors.Allows(WindowInfo.ProgramOf(window)) && CanReach(window);

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
        // An administrator app's window would refuse every button, so the bar doesn't offer them.
        return !Paused && _windows.CanControl(window) && CanReach(window) &&
               !(_ruleOf.TryGetValue(window, out var rule) && rule.NoToolbar && _setupDone.GetValueOrDefault(window)?.Matches() == true);
    }

    private bool IsAvailable(AppCommand command) =>
        _settings.ExperimentalFeatures || !CommandCatalog.IsExperimental(command);

    private bool IsOn(AppCommand command, nint window) => _commands.IsOn(command, window);

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
        _recoveryWatcher.Follow(_windows.NeedsRecoveryWatcher);
        nint foreground = User32.GetForegroundWindow();
        _savedViews.ConcealIfLeft(foreground);
        if (_content.Count > 0) _content.DropClosed();
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
        _modifierDrag.SnapEnabled = _settings.SnapWhileDragging;
        _modifierDrag.MenuEnabled = _settings.WindowMenu;
        _modifierDrag.MenuGesture = _settings.WindowMenuGesture;
        _modifierDrag.OpacityWheelEnabled = _settings.OpacityWheel;
        string program = WindowInfo.ProgramOf(foreground);
        bool programExcluded = !_settings.Allows(program);
        bool menuExcluded = programExcluded ||
                            _settings.WindowMenuExclusions.Contains(program, StringComparer.OrdinalIgnoreCase);
        bool run = ModifierHookPolicy.ShouldRun(
            (_settings.ModifierDrag && !_hookForegroundExcluded) ||
            ((_settings.WindowMenu || fallbackMenu) && !menuExcluded) ||
            (_settings.OpacityWheel && !programExcluded),
            FullScreen.Current(), !User32.IsZoomed(foreground) && WindowInfo.FillsMonitor(foreground), false, FullScreen.IsAway());
        if (!run && _windowMenu is { IsDisposed: false }) _windowMenu.Close();
        Point cursor = Cursor.Position;
        // Windows drops a slow hook without saying so. Input with no callback means it has, so put it back, ending
        // any drag it was in the middle of (its release will never be seen).
        bool silent = _modifierDrag.Active && cursor != _hookCursor &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_modifierDrag.LastCallback).TotalSeconds > HookSilenceSeconds;
        _hookCursor = cursor;
        try
        {
            if (silent) _modifierDrag.Reinstall();
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
        return !_settings.Allows(program) ||
               _settings.ModifierExclusions.Contains(program, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asked inside the mouse hook, only for a press with the modifier held, so it can afford these calls.</summary>
    private bool CanDrag(nint window)
    {
        // A maximized window can be dragged out of maximized; one that fills the screen without being maximized is
        // a game or a full-screen app.
        if (!CanGesture(window) || User32.IsIconic(window) || (!User32.IsZoomed(window) && WindowInfo.FillsMonitor(window)))
            return false;
        // CanGesture has already turned away administrator apps.
        return !_settings.ModifierExclusions.Contains(WindowInfo.ProgramOf(window), StringComparer.OrdinalIgnoreCase);
    }

    private bool CanGesture(nint window)
    {
        if (_windows.Refusal(window) is not null || _windows.GestureBlocked(window)) return false;
        return CanReach(window);
    }

    /// <summary>Windows only lets an app reach an administrator app's windows when it runs as administrator too.</summary>
    private static bool CanReach(nint window)
    {
        if (ProcessInfo.SelfElevated) return true;
        User32.GetWindowThreadProcessId(window, out uint process);
        return !ProcessInfo.IsElevated(process);
    }

    private bool CanOpenWindowMenu(nint window) => CanGesture(window) && !User32.IsIconic(window) &&
        !WindowInfo.FillsMonitor(window) && !_settings.WindowMenuExclusions.Contains(WindowInfo.ProgramOf(window),
            StringComparer.OrdinalIgnoreCase);

    private bool CanChangeOpacity(nint window) => FullScreen.Current() == FullScreenState.None &&
        !FullScreen.IsAway() && !User32.IsIconic(window) && !WindowInfo.FillsMonitor(window) && CanGesture(window);

    /// <summary>
    /// Gives a window its rule (a saved setup, roadmap P1-5) the first time it comes forward, and never again unless
    /// the rule says every time: pinning needs a window that is forward, and whatever the person changes after that
    /// must stay changed. A window no rule is for is looked at again when its title changes, since rules can look at
    /// the title.
    /// </summary>
    private void ApplySavedSetup(nint window)
    {
        // Runs four times a second, so a window already looked at, or no saved setups at all, costs nothing.
        bool cameForward = window != _setupForeground;
        _setupForeground = window;
        if (_settings.SavedSetups.Count == 0 || Paused || !WindowInfo.IsMainWindow(window) ||
            _setupRetryAt.GetValueOrDefault(window) > Environment.TickCount64)
            return;
        if (_setupDone.TryGetValue(window, out var done) && done.Matches() &&
            !(cameForward && _ruleOf.TryGetValue(window, out var applied) && applied.EveryTime))
            return;
        if (_setupNone.TryGetValue(window, out var none) && none.Matches() &&
            _setupNoneTitle.GetValueOrDefault(window) == WindowInfo.Title(window))
            return;
        foreach (var stale in _setupDone.Where(pair => !pair.Value.Matches()).Select(pair => pair.Key).ToArray())
        {
            _setupDone.Remove(stale);
            _setupRetryAt.Remove(stale);
            _ruleOf.Remove(stale);
        }
        foreach (var stale in _setupNone.Where(pair => !pair.Value.Matches()).Select(pair => pair.Key).ToArray())
        {
            _setupNone.Remove(stale);
            _setupNoneTitle.Remove(stale);
        }

        if (_settings.SetupForWindow(window) is not { } setup)
        {
            // No rule for this window. Looked at again when its title or the saved setups change.
            try
            {
                _setupNone[window] = WindowIdentity.Observe(window);
                _setupNoneTitle[window] = WindowInfo.Title(window);
            }
            catch (WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
            return;
        }
        try
        {
            var identity = WindowIdentity.Capture(window);
            if (setup.Pinned && !_windows.IsPinned(window)) _windows.TogglePin(window);
            identity.Require();
            if (setup.Alpha < 255) _windows.SetOpacity(window, setup.Alpha);
            identity.Require();
            if (_settings.ExperimentalFeatures && setup.Dark && DarkLook(window) == Look.None) ToggleDark(window);
            identity.Require();
            // Borderless first: it keeps the inside of the window where it is, so the frame moves when it goes.
            if (setup.Borderless && !_windows.IsBorderless(window)) _windows.ToggleBorderless(window, false, null);
            identity.Require();
            if (!string.IsNullOrEmpty(setup.Display) &&
                Screen.AllScreens.Any(screen => string.Equals(screen.DeviceName, setup.Display, StringComparison.OrdinalIgnoreCase)))
                _placer.MoveToDisplay(window, setup.Display);
            identity.Require();
            if (setup.Place is { } place) _placer.Align(window, place);
            identity.Require();
            if (setup.Anchor && !_anchors.Has(window)) _anchors.Toggle(window);
            identity.Require();
            RestoreSavedView(window, setup);
            _setupDone[window] = identity;
            _ruleOf[window] = setup;
            _setupRetryAt.Remove(window);
            Diagnostics.Log.Event("rule.applied", ("program", Diagnostics.LogValue.ProgramOf(window)), ("narrowed", setup.Narrowed),
                ("everyTime", setup.EveryTime));
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            _setupRetryAt[window] = Environment.TickCount64 + 5000;
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    /// <summary>The rule last applied to each window, for what it asks of the toolbar.</summary>
    private readonly Dictionary<nint, ProgramSetup> _ruleOf = [];
    private readonly Dictionary<nint, string> _setupNoneTitle = [];
    private nint _setupForeground;

    /// <summary>
    /// Brings back a saved view, for the first window of its program in this session. A view that keeps
    /// updating doesn't hide its source yet: this window just opened and the person is about to use it.
    /// </summary>
    private void RestoreSavedView(nint window, ProgramSetup setup)
    {
        if (!_savedViews.ShouldOpen(setup, _pips.ContainsKey(window)))
            return;
        if (OpenViewOf(window) is not { IsDisposed: false } pip)
            return;
        pip.ApplyPreset(setup.View!);
        _savedViews.Opened(setup);
        if (setup.ViewKeepsUpdating)
            _savedViews.ConcealOnLeaving(window, pip);
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
            Dark = _dark.IsDark(window) || _content.Has(window, Glass.ContentLook.Dark),
        };
        if (_pips.TryGetValue(window, out PipWindow? view))
        {
            setup.View = view.CurrentPreset(program).Validated();
            setup.ViewKeepsUpdating = view.KeepsUpdating;
        }

        List<ProgramSetup> before = _settings.SavedSetups.ToList();
        // The program-wide rule is replaced; rules for some of its windows (by title or class) are left alone, and
        // what only a rule can say (place, display, Borderless, Anchor, toolbar, every time) carries over.
        bool Replaced(ProgramSetup saved) => !saved.Narrowed &&
            ((!string.IsNullOrEmpty(setup.ExecutablePath) && string.Equals(saved.ExecutablePath, setup.ExecutablePath,
                 StringComparison.OrdinalIgnoreCase)) ||
             (string.IsNullOrEmpty(saved.ExecutablePath) && string.Equals(saved.Program, program, StringComparison.OrdinalIgnoreCase)));
        if (_settings.SavedSetups.FirstOrDefault(Replaced) is { } old)
        {
            (setup.Place, setup.Display, setup.Borderless, setup.Anchor, setup.NoToolbar, setup.EveryTime, setup.Off) =
                (old.Place, old.Display, old.Borderless, old.Anchor, old.NoToolbar, old.EveryTime, old.Off);
        }
        bool hadSetup = _settings.SavedSetups.RemoveAll(Replaced) > 0;
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

        _setupNone.Clear();
        _setupDone[window] = WindowIdentity.Capture(window);
        if (_settingsWindow is { IsDisposed: false } open) open.SetupsChanged();
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

        if (_pips.TryGetValue(window, out var preview))
        {
            preview.Close();
            if (!preview.IsDisposed) return;
        }
        _content.TurnOff(window);
        if (_content.Has(window)) return;
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
        if (_windows.IsHidden(window) || _windows.IsPeeking(window))
            throw new WindowOperationException(T("errors.window.showFirst"));
        // A part is chosen over the window as it is on screen, which a minimized window isn't, so it comes back first.
        if (User32.IsIconic(window))
            User32.ShowWindow(window, User32.SW_RESTORE);

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

    private void EndVisits()
    {
        foreach (PipWindow view in _viewsInOrder.ToArray())
            if (!view.IsDisposed) view.EndVisit();
    }

    /// <summary>
    /// WinScreen's own windows that stay above views, lowest first: Settings, then a view's own menu, the
    /// window menu, and the tray menu with whichever of their submenus are open.
    /// </summary>
    private IEnumerable<nint> AppWindowsAbove()
    {
        // Maximized it fills the screen, so it stays with ordinary windows instead of over every view.
        if (_settingsWindow is { IsDisposed: false, Visible: true, IsHandleCreated: true, FillsScreen: false } settings)
        {
            yield return settings.Handle;
            foreach (Form owned in settings.OwnedForms)
                if (owned.Visible && owned.IsHandleCreated) yield return owned.Handle;
        }
        foreach (PipWindow view in _viewsInOrder)
            if (!view.IsDisposed)
                foreach (nint window in WinScreen.Windows.TopLayer.OpenDropDowns(view.ContextMenuStrip))
                    yield return window;
        if (_windowMenu is { IsDisposed: false })
            foreach (nint window in WinScreen.Windows.TopLayer.OpenDropDowns(_windowMenu.Popup))
                yield return window;
        foreach (nint window in WinScreen.Windows.TopLayer.OpenDropDowns(_tray.ContextMenuStrip))
            yield return window;
    }

    /// <summary>Opens Settings, or brings it forward; <paramref name="bounds"/> and <paramref name="page"/> reopen it where it was.</summary>
    /// <param name="reopening">True when Settings is only made again in the other colours, replacing the one open.</param>
    /// <param name="unsaved">Changes the window it replaces had not saved.</param>
    /// <param name="quietly">Shown without taking the keyboard from what has it.</param>
    private void OpenSettings(Rectangle? bounds = null, int page = 0, bool reopening = false, SettingsWindow.UnsavedEdits? unsaved = null,
        bool quietly = false, FormWindowState state = FormWindowState.Normal)
    {
        if (_settingsWindow is { IsDisposed: false })
        {
            // Back from the taskbar as it was, maximized or not.
            if (_settingsWindow.IsHandleCreated && _settingsWindow.WindowState == FormWindowState.Minimized)
                User32.ShowWindow(_settingsWindow.Handle, User32.SW_RESTORE);
            _settingsWindow.Activate();
            return;
        }

        if (bounds is null && !reopening &&
            _settings.SettingsWindowPlacement?.OnScreen(Screen.AllScreens.Select(screen => screen.WorkingArea)) is { } last)
        {
            bounds = last.Bounds;
            if (last.Maximized) state = FormWindowState.Maximized;
        }

        // A view being used is put back to watching when Settings opens, but not when Settings is only made again in
        // the other colours: nothing was asked of the view.
        if (!reopening) EndVisits();
        _settingsWindow = new SettingsWindow(_settings, _startup, Apply, command => _hotkeys.IsRegistered((int)command))
            { TopMost = !quietly, PageIndex = page, Quiet = quietly };
        _settingsWindow.TakeUnsaved(unsaved);
        if (bounds is { } where) { _settingsWindow.StartPosition = FormStartPosition.Manual; _settingsWindow.Bounds = where; }
        SettingsWindow opened = _settingsWindow;
        opened.FormClosing += (_, _) => RememberPlacement(opened);
        _settingsWindow.Show();
        // Only once shown: a state set before is lost as Settings is shown. Never reached quietly (see ReopenSettings).
        if (state == FormWindowState.Maximized) _settingsWindow.WindowState = FormWindowState.Maximized;
        if (!quietly) _settingsWindow.Activate();
    }

    /// <summary>Writes the diagnostics file and shows it in File Explorer, for the person to attach to a report.</summary>
    private void SaveDiagnostics()
    {
        try
        {
            string path = Diagnostics.DiagnosticsFile.Save(Path.GetDirectoryName(_settingsPath)!, _settings);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Log.Error("diagnostics.saveFailed", failure);
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    private async Task CheckForUpdates()
    {
        string result = !VelopackUpdateSession.IsInstalled
            ? T("updates.installedOnly")
            : await _updates.CheckNow();
        Balloon(T("updates.title"), result, ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        // Settings first, while everything it puts into effect is still there: closing it applies a change still settling.
        if (_settingsWindow is { IsDisposed: false } settings) settings.Close();
        // Windows go back first: if that fails WinScreen stays running, with its views still in place.
        if (!TakeEverythingAwayForExit())
        {
            Alert(T("recovery.attention"),
                T(
                    "errors.app.exitRestoreFailed") +
                Environment.NewLine + string.Join(Environment.NewLine, _windows.RestoreFailures));
            return;
        }

        _updateTimer.Dispose();
        _menuGuard.Dispose();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= FollowWindowsTheme;
        _watch.Dispose();
        _borders.Dispose();
        _dark.Dispose();
        _automaticAnchors.Dispose();
        _recoveryWatcher.Dispose();
        _anchors.Dispose();
        _windowMenu?.Close();
        _modifierDrag.Dispose();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
            form.Close();
        _toolbar.Dispose();
        _flash.Dispose();
        _snapPreview.Dispose();
        Task.Run(_updates.ApplyOnExit).GetAwaiter().GetResult();
        _updates.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private void Run(AppCommand command, nint window)
        => RunOnDisplay(command, window, null);

    private static void LogCommand(AppCommand command, nint window, CommandOutcome outcome) =>
        Diagnostics.Log.Event("command", ("name", command), ("outcome", outcome),
            ("program", window == 0 ? Diagnostics.LogValue.Program("") : Diagnostics.LogValue.ProgramOf(window)));

    /// <summary>
    /// The one way every command runs, from hotkeys, menus and the toolbar: the checks in a fixed order, then the
    /// command's definition (<see cref="CommandTable"/>). Says how it went.
    /// </summary>
    private CommandOutcome RunOnDisplay(AppCommand command, nint window, Rectangle? display)
    {
        if (!_commands.TryGet(command, out CommandDefinition definition))
            return CommandOutcome.Refused;
        switch (definition.Target)
        {
            case CommandTarget.EveryWindow:
                LogCommand(command, 0, CommandOutcome.Started);
                definition.Run(0, null);
                return CommandOutcome.Started;
            case CommandTarget.UnderPointer:
                definition.Run(0, null);
                return CommandOutcome.Started;
        }
        if (command == AppCommand.RestoreWindow &&
            (window == 0 || !_windows.CanControl(window)) && _lastIdentity?.IsSameWindow() == true)
        {
            User32.GetWindowThreadProcessId(window, out uint pid);
            if (window == 0 || pid == Environment.ProcessId) window = _lastIdentity.Window;
        }

        bool recovery = definition.Recovers;
        switch (recovery ? FullScreenAction.Run : FullScreenDecision(command, window))
        {
            case FullScreenAction.CloseLatestView:
                LogCommand(command, window, CommandOutcome.ClosedLatestView);
                _viewsInOrder[^1].Close();
                return CommandOutcome.ClosedLatestView;
            case FullScreenAction.ExplainExclusive:
                LogCommand(command, window, CommandOutcome.ExclusiveFullScreen);
                Alert(T("errors.operation.title"), T("errors.fullScreen.exclusiveView"));
                return CommandOutcome.ExclusiveFullScreen;
            case FullScreenAction.Refuse:
                LogCommand(command, window, CommandOutcome.PausedInFullScreen);
                Alert(T("errors.operation.title"), T("errors.fullScreen.paused"));
                return CommandOutcome.PausedInFullScreen;
        }

        if (definition.Target == CommandTarget.NoWindow) window = 0;
        else if (!recovery && _windows.Refusal(window) is { } refusal)
        {
            LogCommand(command, window, CommandOutcome.Refused);
            Alert(T("errors.operation.title"), refusal);
            return CommandOutcome.Refused;
        }

        if (!recovery && !IsAvailable(command))
        {
            LogCommand(command, window, CommandOutcome.NotTurnedOn);
            Alert(T("errors.operation.title"), T("errors.experimental.enableFirst"));
            return CommandOutcome.NotTurnedOn;
        }

        LogCommand(command, window, CommandOutcome.Started);
        try
        {
            definition.Run(window, display);
            return CommandOutcome.Started;
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            LogCommand(command, window, CommandOutcome.Failed);
            Diagnostics.Log.Error("command.failed", failure);
            Alert(T("errors.operation.title"), failure.Message);
            return CommandOutcome.Failed;
        }
    }

    /// <summary>The commands for every window at once. They only take things away, so they work at any time.</summary>
    private void RunEverywhere(AppCommand command)
    {
        string name = CommandCatalog.Name(command);
        switch (command)
        {
            case AppCommand.ClickThroughOffEverywhere:
                Report(_windows.TurnOffClickThroughEverywhere(), T("everywhere.clickThroughOff.done"));
                break;
            case AppCommand.UnpinEverywhere:
                Report(_windows.UnpinEverywhere(), T("everywhere.unpin.done"));
                break;
            case AppCommand.HideAnchors:
                _anchors.Hidden = !_anchors.Hidden;
                Confirm(name, _anchors.Hidden ? T("everywhere.anchors.hidden") : T("everywhere.anchors.shown"));
                break;
            case AppCommand.HideToolbar:
                _toolbarHidden = !_toolbarHidden;
                _toolbar.Enable(_settings.ShowToolbar && !_toolbarHidden);
                Confirm(name, _toolbarHidden ? T("everywhere.toolbar.hidden") : T("everywhere.toolbar.shown"));
                break;
        }

        void Report(int failed, string done)
        {
            if (failed > 0)
                Alert(T("errors.recovery.someFailed"), string.Format(T("errors.window.changeRefusedCount"), failed) +
                    Environment.NewLine + string.Join(Environment.NewLine, _windows.RestoreFailures));
            else
                Confirm(name, done);
        }
    }

    /// <summary>True while the toolbar is put away until it's asked for again or WinScreen starts again.</summary>
    private bool _toolbarHidden;

    private void Attempt(Action action)
    {
        try
        {
            action();
        }
        catch (Exception failure) when (IsExpected(failure))
        {
            Diagnostics.Log.Error("action.failed", failure);
            Alert(T("errors.operation.title"), failure.Message);
        }
    }

    /// <summary>Everything that moves a window, from one button, so the toolbar doesn't need a button for each.</summary>
    private void ShowArrangeMenu(nint window) =>
        ShowHostedMenu(window, Cursor.Position, AppCommand.Arrange, (menu, builder) => builder.PopulateArrange(menu.Items));

    private void PlaceExactly(nint window)
    {
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _))
            return;
        var identity = WindowIdentity.Capture(window);
        using var dialog = new PlacementDialog(CommandCatalog.Name(AppCommand.PlaceExactly), frame);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            identity.Require();
            _placer.SetFrame(window, dialog.Frame);
        }
    }

    private static void ShowWindowInfo(nint window)
    {
        string text = WindowUtilities.Information(window);
        string path = WindowInfo.ExecutablePath(window);
        new InfoWindow(CommandCatalog.Name(AppCommand.ShowWindowInfo), text,
            (T("window.openLocation"), () => WindowUtilities.OpenLocation(window), path.Length > 0),
            (T("common.copy"), () => WindowUtilities.CopyText(text), true)).ShowInFront();
    }

    /// <summary>Opens a view of a window, or returns null when the person chose not to restore a minimized one.</summary>
    private PipWindow? OpenViewOf(nint window)
    {
        if (_shrunk.TryGetValue(window, out var tile))
        {
            tile.Close();
            if (!tile.IsDisposed) throw new WindowOperationException(T("errors.window.showFirst"));
        }
        _content.TurnOff(window);
        if (_content.Has(window)) throw new WindowOperationException(T("errors.window.showFirst"));
        WindowIdentity identity = WindowIdentity.Capture(window);
        if (User32.IsIconic(window) && MessageBox.Show(string.Format(T("pip.sources.restoreWarning"), WindowInfo.Title(window)),
            T("pip.open"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return null;
        identity.Require();
        var pip = new PipWindow(window, _windows);
        _pips[window] = pip;
        pip.FormClosed += (_, _) => _pips.Remove(window);
        OpenView(pip);
        return pip;
    }

    private void OpenView(PipWindow pip)
    {
        pip.AllowAutoMove = _settings.ExperimentalFeatures;
        pip.AllowInteractive = _settings.ExperimentalFeatures;
        pip.AllowSwitcherHiding = _settings.ExperimentalFeatures;
        _viewsInOrder.Add(pip);
        pip.FormClosed += (_, _) => _viewsInOrder.Remove(pip);
        pip.Problem += text => Alert(T("errors.operation.title"), text);
        pip.Notice += text => Alert(T("pip.open"), text, ToolTipIcon.Info);
        pip.AlwaysShowOriginal += ShowOriginalFor;
        pip.Presets = () => _settings.PipPresets;
        pip.PresetSaved += preset => ChangePresets(presets =>
        {
            presets.RemoveAll(saved => string.Equals(saved.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase));
            presets.Add(preset);
        });
        pip.PresetRemoved += name => ChangePresets(presets =>
            presets.RemoveAll(saved => string.Equals(saved.Name, name, StringComparison.CurrentCultureIgnoreCase)));
        pip.SourceBookmarkSaved += bookmark => ChangeBookmarks(bookmarks =>
        {
            bookmarks.RemoveAll(saved => string.Equals(saved.Name, bookmark.Name, StringComparison.CurrentCultureIgnoreCase));
            bookmarks.Add(bookmark);
        });
        pip.ApplyPipPreferences(_settings.Pip);
        pip.ApplyViewPreferences(_settings.PipViews);
        pip.ShowOnTop();
        if (pip.IsDisposed) return;
        _dark.Ignore(pip.Handle);
        if (_settings.ExperimentalFeatures && _settings.PipAutoMove)
            pip.SetAutoMove(true);
        if (_settings.ExperimentalFeatures && _settings.PipMiddleClickScroll)
            pip.SetMiddleClickScroll(true); // Takes effect once the view is used directly.
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

    /// <summary>Adds a program to Settings' Show original list, chosen from a view's chip.</summary>
    private void ShowOriginalFor(string program)
    {
        if (_settings.PipOriginalPrograms.Contains(program, StringComparer.OrdinalIgnoreCase)) return;
        var draft = _settings.Clone();
        draft.PipOriginalPrograms.Add(program);
        // One or the other, as in Settings: nothing is sent to a program whose original is shown.
        draft.PipHover.Programs.RemoveAll(name => name.Equals(program, StringComparison.OrdinalIgnoreCase));
        try { draft.Save(_settingsPath); _settings.CopyFrom(draft); }
        catch (Exception failure) when (IsExpected(failure)) { Alert(T("errors.settings.saveFailed"), failure.Message); return; }
        Pip.PipOriginalApps.Programs = _settings.PipOriginalPrograms;
        Pip.PipHoverApps.Preferences = _settings.PipHover;
        // An open Settings window saves its own list with its next change, so it must show this one too.
        if (_settingsWindow is { IsDisposed: false } open) open.AddOriginalProgram(program);
    }

    private void ChangeBookmarks(Action<List<PipSourceBookmark>> change)
    {
        var draft = _settings.Clone();
        change(draft.PipBookmarks);
        try { draft.Save(_settingsPath); _settings.CopyFrom(draft); }
        catch (Exception failure) when (IsExpected(failure)) { Alert(T("errors.settings.saveFailed"), failure.Message); }
    }

    private void OpenBookmark(PipSourceBookmark bookmark, bool previewOnly = false)
    {
        if (FullScreenDecision(AppCommand.PickPipSource, 0) != FullScreenAction.Run)
        {
            Alert(T("errors.operation.title"), T("errors.fullScreen.paused"));
            return;
        }
        Attempt(() =>
        {
            ResolvedBookmark resolved = bookmark.Resolve(Screen.AllScreens.Select(screen =>
                new BookmarkDisplay(screen.DeviceName, screen.Bounds, screen.Primary)).ToArray())
                ?? throw new WindowOperationException(T("errors.pip.displayUnavailable"));
            if (resolved.NeedsPreview || previewOnly)
            {
                using var preview = new PipBookmarkPreview(bookmark);
                if (preview.ShowDialog() != DialogResult.OK || preview.Selection is not { } selected) return;
                resolved = selected;
            }
            var source = new ScreenPipSource(resolved.Display.Device, resolved.Region);
            source.Require();
            if (source.Screen.Bounds != resolved.Display.Bounds)
                throw new WindowOperationException(T("errors.pip.displayUnavailable"));
            var view = new PipWindow(source, _windows);
            OpenView(view);
            if (bookmark.View is { } preset && !view.IsDisposed) view.ApplyPreset(preset);
        });
    }

    private void ShowPicker()
    {
        if (_picker is { IsDisposed: false })
        {
            _picker.Activate();
            return;
        }

        var picker = new PipSourcePicker(window => _windows.CanControl(window) && !_pips.ContainsKey(window))
            { Bookmarks = () => _settings.PipBookmarks };
        picker.WindowChosen += window =>
        {
            if (picker.ChosenWindowMatches) Run(AppCommand.OpenPip, window);
            else Alert(T("errors.operation.title"), T("errors.window.targetUnavailable"));
        };
        picker.ScreenChosen += device => OpenScreenView(new ScreenPipSource(device, null));
        picker.RegionWanted += device => Attempt(() => SelectRegion(device));
        picker.RegionChosen += (device, bounds) => OpenScreenView(new ScreenPipSource(device, bounds));
        picker.BookmarkChosen += bookmark => OpenBookmark(bookmark);
        picker.BookmarkPreviewRequested += bookmark => OpenBookmark(bookmark, previewOnly: true);
        picker.BookmarkRemoved += name => ChangeBookmarks(bookmarks =>
            bookmarks.RemoveAll(bookmark => string.Equals(bookmark.Name, name, StringComparison.CurrentCultureIgnoreCase)));
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
        source.Require();
        Rectangle selectedDisplay = source.Screen.Bounds;
        var overlay = new CropOverlay(source.Screen.Bounds);
        overlay.Chosen += part =>
        {
            if (_cropping != overlay) return;
            _cropping = null;
            Attempt(() =>
            {
                source.Require();
                if (source.Screen.Bounds != selectedDisplay) throw new WindowOperationException(T("errors.pip.displayUnavailable"));
                OpenScreenView(new ScreenPipSource(device, part));
            });
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

    /// <summary>Picture-safe Dark when it's chosen in Settings, otherwise the simple filter. Only one at a time.</summary>
    /// <summary>The ways a window's colours can be changed. A window has at most one of them at a time.</summary>
    private enum Look { None, FilterDark, PictureDark, ColorGlass, LayeredGlass }

    private Look DarkLook(nint window) =>
        _dark.IsDark(window) ? Look.FilterDark : _content.Has(window, Glass.ContentLook.Dark) ? Look.PictureDark : Look.None;

    private Look GlassLook(nint window) =>
        _windows.IsGlass(window) ? Look.ColorGlass : _content.Has(window, Glass.ContentLook.Glass) ? Look.LayeredGlass : Look.None;

    /// <summary>Dark mode, or picture-keeping Dark when it's chosen in Settings. Glass goes first: the two can't share a window.</summary>
    private void ToggleDark(nint window) => SwitchLook(window, DarkLook(window), GlassLook(window),
        _settings.Effects.PictureSafeDark ? Look.PictureDark : Look.FilterDark);

    /// <summary>Layered Glass when it's chosen in Settings, otherwise the colour-key Glass. Dark goes first.</summary>
    private void ToggleGlass(nint window) => SwitchLook(window, GlassLook(window), DarkLook(window),
        _settings.Effects.LayeredGlass ? Look.LayeredGlass : Look.ColorGlass);

    /// <summary>
    /// Turns <paramref name="current"/> off when the window has it; otherwise turns <paramref name="other"/> off and
    /// <paramref name="wanted"/> on. When the new look fails or is refused, the old one is put back, so a window never
    /// ends up with both, and never loses one for nothing.
    /// </summary>
    private void SwitchLook(nint window, Look current, Look other, Look wanted)
    {
        if (current != Look.None)
        {
            SetLook(window, current, false);
            return;
        }
        // Hidden behind a view or tile: both looks work from what is on screen, where it isn't.
        if (_windows.IsHidden(window) && !_content.Has(window))
            throw new WindowOperationException(T("errors.window.showFirst"));
        if (other != Look.None)
        {
            SetLook(window, other, false);
            if (Has(other)) throw new WindowOperationException(T("errors.effects.otherStayed"));
        }
        try
        {
            SetLook(window, wanted, true);
            if (Has(wanted)) return;
        }
        catch (Exception failure) when (IsExpected(failure) && other != Look.None)
        {
            PutBack(failure.Message);
            throw;
        }
        if (other != Look.None) PutBack(null); // Refused without an error, as Dark is for a window that is dark already.

        bool Has(Look look) => look is Look.FilterDark or Look.PictureDark ? DarkLook(window) == look : GlassLook(window) == look;

        void PutBack(string? problem)
        {
            try { SetLook(window, other, true); }
            catch (Exception failure) when (IsExpected(failure))
            {
                throw new WindowOperationException(problem is null ? failure.Message : problem + Environment.NewLine + failure.Message);
            }
        }
    }

    private void SetLook(nint window, Look look, bool on)
    {
        switch (look)
        {
            case Look.FilterDark:
                if (_dark.IsDark(window) != on) _dark.Toggle(window);
                break;
            case Look.PictureDark:
                if (on != _content.Has(window, Glass.ContentLook.Dark)) _content.Toggle(window, Glass.ContentLook.Dark);
                break;
            case Look.LayeredGlass:
                if (on != _content.Has(window, Glass.ContentLook.Glass)) _content.Toggle(window, Glass.ContentLook.Glass);
                break;
            case Look.ColorGlass:
                if (_windows.IsGlass(window) != on) _windows.ToggleGlass(window);
                break;
        }
    }

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
