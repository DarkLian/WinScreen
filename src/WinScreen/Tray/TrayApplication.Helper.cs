using static WinScreen.Localization.UiText;
using System.Text.Json;
using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Tray;

public sealed partial class TrayApplication
{
    private const int HelperCheckMilliseconds = 2000, HelperStartsAllowed = 3;
    private static readonly TimeSpan OrdinaryCopyWait = TimeSpan.FromSeconds(60);

    private bool HelperWanted =>
        AppRoles.WantsHelper(_role, _settings.RunAsAdministrator, _settings.AdministratorHelper, ProcessInfo.CanElevate);

    private bool HelperChosen => _role == AppRole.Helper || HelperWanted;

    internal bool HelperConnected => _helper?.Connected == true;

    internal uint HelperProcess => _helper?.Peer ?? 0;

    private bool _forOrdinaryCopy;
    private bool? _wantedOn; // A hide everywhere from the ordinary copy: where it ended there.

    // The ordinary copy: what the helper last said it holds. The helper: what it last sent, and its timer.
    private HelperLink.Message? _helperState;
    private string? _stateSent;
    private System.Windows.Forms.Timer? _stateTimer;
    private const int StateMilliseconds = 300;

    private nint _watched; // The helper: the window the ordinary copy's tray shows.
    private nint _watchSent = -1; // The ordinary copy: what it last asked the helper to watch.

    private HelperLink.HelperView[] HelperViews => _helperState?.Views ?? [];

    // The ordinary copy's tray shows the window last in front; one of an administrator app is the helper's to say about.
    private void WatchForHelper(nint last)
    {
        if (_helper is not { Connected: true } link) return;
        try
        {
            nint wanted = last != 0 && User32.IsWindow(last) && !CanReach(last) ? last : 0;
            if (wanted != _watchSent && link.Send(new HelperLink.Message(HelperLink.Watch, Window: wanted))) _watchSent = wanted;
        }
        catch (Exception failure) when (IsExpected(failure) || failure is InvalidOperationException or ArgumentException)
        {
            Diagnostics.Log.Swallowed(failure);
        }
    }

    private bool? HelperSaysOn(AppCommand command, nint window) =>
        window != 0 && _helperState is { On: { } on } state && (nint)state.Window == window ? on.Contains((int)command) : null;

    private int HelperCount(int index) => _helperState?.Counts is { } counts && counts.Length > index ? counts[index] : 0;

    private void SendState()
    {
        if (_ordinary is not { Connected: true } ordinary)
        {
            _stateSent = null;
            return;
        }
        // Every 300 ms for as long as the helper runs: a window that went meanwhile is skipped, never the helper's end.
        try
        {
            HelperLink.HelperView[] views = _viewsInOrder.Where(view => view.IsHandleCreated && !view.IsDisposed)
                .Select(view => new HelperLink.HelperView(view.Handle, _pips.FirstOrDefault(pair => pair.Value == view).Key, view.MenuTitle,
                    view.IsSuspended, view.SourceIsWindow, view.Overrides.ClickThrough, view.CanRetryCapture, view.PointedAt, view.SuspendedAt))
                .ToArray();
            nint watched = _watched != 0 && User32.IsWindow(_watched) ? _watched : 0;
            int[]? on = watched == 0 ? null : CommandCatalog.All.Where(command => !CommandCatalog.ForView(command) && _commands.IsOn(command, watched))
                .Select(command => (int)command).ToArray();
            HelperLink.HelperItem[] tiles = _shrunk.Where(pair => !pair.Value.IsDisposed)
                .Select(pair => new HelperLink.HelperItem(pair.Key, pair.Value.Text)).ToArray();
            HelperLink.HelperItem[] effects = _content.Sources.Select(source => new HelperLink.HelperItem(source, Windows.WindowInfo.Title(source))).ToArray();
            var state = new HelperLink.Message(HelperLink.State, Window: watched, Counts: [_windows.ClickThroughCount, _windows.PinnedCount],
                Views: views, On: on, Tiles: tiles, Effects: effects);
            string encoded = HelperLink.Encode(state);
            if (encoded != _stateSent && ordinary.Send(state)) _stateSent = encoded;
        }
        catch (Exception failure) when (IsExpected(failure) || failure is InvalidOperationException or ArgumentException)
        {
            Diagnostics.Log.Swallowed(failure);
        }
    }

    private void FromHelperState(HelperLink.Message state)
    {
        int[] counts = state.Counts ?? [];
        bool changed = _helperState is null || !(_helperState.Counts ?? []).SequenceEqual(counts) ||
                       HelperViews.Length != (state.Views?.Length ?? 0) || _helperState.Window != state.Window ||
                       (_helperState.Tiles?.Length ?? 0) != (state.Tiles?.Length ?? 0) || (_helperState.Effects?.Length ?? 0) != (state.Effects?.Length ?? 0) ||
                       !(_helperState.On ?? []).SequenceEqual(state.On ?? []);
        _helperState = state;
        if (changed)
            Diagnostics.Log.Event("helper.state", ("clickThrough", HelperCount(0)), ("pinned", HelperCount(1)), ("views", HelperViews.Length),
                ("watched", state.Window != 0), ("on", state.On?.Length ?? 0), ("tiles", state.Tiles?.Length ?? 0),
                ("effects", state.Effects?.Length ?? 0));
    }

    private void RunHelperViewAction(nint view, string action)
    {
        if (_helper is not { Connected: true } link) return;
        User32.AllowSetForegroundWindow(link.Peer); // Go to the original: the tray click's right to the front is passed on.
        link.Send(new HelperLink.Message(HelperLink.ViewAction, Window: view, Text: action));
    }

    private void ViewActionFromOrdinaryCopy(nint handle, string? action)
    {
        if (_viewsInOrder.FirstOrDefault(each => each.IsHandleCreated && each.Handle == handle) is not { IsDisposed: false } view) return;
        Attempt(() =>
        {
            switch (action)
            {
                case "resume": view.ResumeView(); break;
                case "goTo": view.GoToSource(); break;
                case "clicks": view.Overrides = view.Overrides with { ClickThrough = false }; break;
                case "retry": if (view.CanRetryCapture) view.RetryCapture(); break;
                case "close": view.Close(); break;
            }
        });
        SendState();
    }

    private void TileActionFromOrdinaryCopy(nint source)
    {
        if (!_shrunk.TryGetValue(source, out var tile) || tile.IsDisposed) return;
        tile.BringBack = true;
        tile.Close();
        SendState();
    }

    // Started through the sign-in task, this copy may already have a helper. Followed every two seconds: a helper that
    // went is started again, a few times.
    private void StartHelper()
    {
        if (!HelperWanted) return;
        if (_helper is null)
        {
            _helper = new HelperLink.OrdinaryEnd();
            _helper.Received += message => OnToolbar(() => FromHelper(message));
            _helper.ConnectionChanged += connected => OnToolbar(() =>
            {
                Diagnostics.Log.Event("helper.link", ("connected", connected));
                _watchSent = -1;
                if (!connected) _helperState = null;
                // A helper started since anchors or the toolbar were hidden hides them too.
                else foreach (AppCommand hide in new[] { AppCommand.HideAnchors, AppCommand.HideToolbar })
                    if (IsHidden(hide) == true) _helper?.Send(new HelperLink.Message(HelperLink.Run, (int)hide, Text: "on"));
            });
            _helper.Start();
        }
        _helperStarts = 0;
        if (_helperCheck is null)
        {
            _helperCheck = new System.Windows.Forms.Timer { Interval = HelperCheckMilliseconds };
            _helperCheck.Tick += (_, _) => FollowHelper();
            _helperCheck.Start();
        }
        FollowHelper();
    }

    // The link reports from its own thread, also as WinScreen closes: the end of the link came after the toolbar was
    // gone and crashed WinScreen on the way out.
    private void OnToolbar(Action action)
    {
        if (_toolbar.IsDisposed || !_toolbar.IsHandleCreated) return;
        try { _toolbar.BeginInvoke(action); }
        catch (InvalidOperationException failure) { Diagnostics.Log.Swallowed(failure); }
    }

    private void FollowHelper()
    {
        if (_helper is not { } link || link.Connected) return;
        if (HelperLink.FindCopy(AppRoles.HelperHostTitle, elevated: true) != 0) return; // Starting, or about to connect.
        if (_helperStarts >= HelperStartsAllowed)
        {
            if (_helperStarts++ == HelperStartsAllowed) Alert(T("settings.admin"), T("settings.admin.helperFailed"));
            return;
        }
        _helperStarts++;
        Diagnostics.Log.Event("helper.starting", ("attempt", _helperStarts));
        if (Elevation.StartHelper(_startup.Tasks)) return;
        // Windows asked and the person said no: as before, the choice goes back to off.
        StopHelper();
        _settings.RunAsAdministrator = false;
        _startup.AsAdministrator = false;
        try { SaveSettings(_settings); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(failure); }
        if (_settingsWindow is { IsDisposed: false } open) open.LoadValues(_settings);
        Alert(T("settings.admin"), T("settings.admin.declined"), ToolTipIcon.Info);
    }

    private void StopHelper()
    {
        _helperCheck?.Dispose();
        _helperCheck = null;
        _helper?.Send(new HelperLink.Message(HelperLink.Exit));
        _helper?.Dispose();
        _helper = null;
    }

    private void FromHelper(HelperLink.Message message)
    {
        switch (message.Kind)
        {
            case HelperLink.Alert when message.Title is { } title:
                Remember(title, message.Text ?? "");
                Balloon(title, message.Text ?? "", Enum.IsDefined((ToolTipIcon)message.Icon) ? (ToolTipIcon)message.Icon : ToolTipIcon.Info);
                break;
            case HelperLink.OpenSettings:
                OpenSettings();
                break;
            case HelperLink.Run when message.Command == (int)AppCommand.RestoreAll:
                Run(AppCommand.RestoreAll, 0);
                break;
            case HelperLink.State:
                FromHelperState(message);
                break;
            case HelperLink.Outcome when _helperAnswers.Remove(message.Id, out Action<CommandLineExit>? answer):
                answer(CommandLine.ExitFor((CommandOutcome)message.Command));
                break;
            case HelperLink.Save when message.Text is { } json:
                try
                {
                    AppSettings changed = AppSettings.Parse(json);
                    if (Apply(changed) is { Count: > 0 } problems) Alert(T("errors.settings.saveFailed"), Describe(problems));
                    if (_settingsWindow is { IsDisposed: false } open) open.LoadValues(_settings);
                }
                catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Log.Error("helper.saveFailed", failure);
                    Alert(T("errors.settings.saveFailed"), failure.Message);
                }
                break;
        }
    }

    // Started by the sign-in task with no ordinary copy yet, it starts one, as the person.
    private void StartAsHelper()
    {
        _ordinary = new HelperLink.HelperEnd(() => OnToolbar(LeaveAsHelper));
        _ordinary.Received += message => OnToolbar(() => FromOrdinaryCopy(message));
        _ordinary.ConnectionChanged += connected => OnToolbar(() =>
        {
            Diagnostics.Log.Event("helper.link", ("connected", connected));
            _stateSent = null;
        });
        _ordinary.Start(OrdinaryCopyWait);
        _stateTimer = new System.Windows.Forms.Timer { Interval = StateMilliseconds };
        _stateTimer.Tick += (_, _) => SendState();
        _stateTimer.Start();
        if (HelperLink.FindCopy(Hotkeys.HotkeyHost.WindowTitle, elevated: false) != 0) return;
        // Started at sign-in: the ordinary copy is started as the person, through the shell, which may still be
        // starting.
        _helperCheck = new System.Windows.Forms.Timer { Interval = HelperCheckMilliseconds };
        _helperCheck.Tick += (_, _) => StartOrdinaryCopy();
        _helperCheck.Start();
        StartOrdinaryCopy();
    }

    // Two-second looks: a start every third, the last at about half a minute.
    private const int OrdinaryLooksAllowed = 13;

    private void StartOrdinaryCopy()
    {
        if (_ordinary?.Connected == true || HelperLink.FindCopy(Hotkeys.HotkeyHost.WindowTitle, elevated: false) != 0)
        {
            _helperCheck?.Dispose();
            _helperCheck = null;
            return;
        }
        int look = ++_helperStarts;
        if (look <= OrdinaryLooksAllowed)
        {
            // A start the shell took gets a few seconds to show its window before the next.
            if (look % 3 == 1 && UnelevatedStart.Run(Environment.ProcessPath!, Elevation.DataFolderArguments()))
                Diagnostics.Log.Event("helper.startedOrdinaryCopy", ("attempt", look / 3 + 1));
            return;
        }
        // No way to start WinScreen as the person: the one elevated copy of before takes over, and this helper goes.
        _helperCheck?.Dispose();
        _helperCheck = null;
        Diagnostics.Log.Event("helper.fallingBack", ("started", Elevation.StartSingleCopy()));
        LeaveAsHelper();
    }

    private void LeaveAsHelper()
    {
        if (Exiting) return;
        Diagnostics.Log.Event("helper.leaving");
        ExitThread();
        if (Exiting) return;
        var again = new System.Windows.Forms.Timer { Interval = 5000 };
        again.Tick += (_, _) =>
        {
            ExitThread();
            if (Exiting) again.Dispose();
        };
        again.Start();
    }

    private void FromOrdinaryCopy(HelperLink.Message message)
    {
        switch (message.Kind)
        {
            case HelperLink.Run when Enum.IsDefined((AppCommand)message.Command):
                nint window = (nint)message.Window;
                // Only a window this copy may act on: an administrator app's, or none for the commands for every
                // window.
                if (window != 0 && (!User32.IsWindow(window) || !CanReach(window))) return;
                _forOrdinaryCopy = true;
                _wantedOn = message.Text switch { "on" => true, "off" => false, _ => null };
                // A command from the command line: its outcome goes back, or for a dialog or menu, that it is open.
                bool replied = false;
                void Reply(CommandOutcome outcome)
                {
                    if (replied || message.Id == 0) return;
                    replied = true;
                    _ordinary?.Send(new HelperLink.Message(HelperLink.Outcome, (int)outcome, Id: message.Id));
                }
                var asked = (AppCommand)message.Command;
                if (message.Id != 0 && _commands.TryGet(asked, out CommandDefinition definition) && definition.Opens?.Invoke(window) == true)
                    _toolbar.BeginInvoke(() => Reply(CommandOutcome.Started));
                CommandOutcome result = CommandOutcome.Failed;
                try { result = RunOnDisplay(asked, window, message.DisplayBounds); }
                finally
                {
                    _forOrdinaryCopy = false;
                    _wantedOn = null;
                    Reply(result);
                }
                SendState();
                break;
            case HelperLink.ViewAction when message.Text == "showTile":
                TileActionFromOrdinaryCopy((nint)message.Window);
                break;
            case HelperLink.ViewAction:
                ViewActionFromOrdinaryCopy((nint)message.Window, message.Text);
                break;
            case HelperLink.Watch:
                _watched = (nint)message.Window;
                SendState();
                break;
            case HelperLink.SettingsSaved:
                ReloadSettings();
                break;
            case HelperLink.Exit:
                ReloadSettings(); // Run as administrator off: the sign-in task follows, then the helper goes.
                if (!Exiting) ExitThread();
                break;
        }
    }

    // Read without setting a damaged file aside: only the writer does that. One it can't read leaves things as they
    // are.
    private void ReloadSettings()
    {
        AppSettings? loaded = null;
        for (int attempt = 0; attempt < 5 && loaded is null; attempt++)
        {
            try { loaded = AppSettings.Parse(File.ReadAllText(_settingsPath)); }
            catch (IOException failure) { Diagnostics.Log.Swallowed(failure); Thread.Sleep(50); }
            catch (Exception failure) when (failure is JsonException or UnauthorizedAccessException)
            {
                Diagnostics.Log.Swallowed(failure);
                return;
            }
        }
        if (loaded is null) return;
        if (!loaded.RunAsAdministrator || !loaded.AdministratorHelper)
        {
            // The sign-in task is an elevated copy's to change: back to the Run entry, or to the one elevated copy.
            FollowStartup(loaded, asAdministrator: loaded.RunAsAdministrator, arguments: "");
            if (!Exiting) ExitThread();
            return;
        }
        bool experimentalBefore = _settings.ExperimentalFeatures;
        string themeBefore = _settings.Theme;
        AppSettings before = _settings.Clone();
        _settings.CopyFrom(loaded);
        _setupNone.Clear();
        RulesChanged();
        ApplyPreferences(experimentalBefore, before);
        Native.Translucency.Allowed = _settings.MenuTransparency;
        if (_settings.Theme != themeBefore) ChangeTheme();
        FollowStartup(_settings, asAdministrator: true, AppRoles.HelperArgument);
    }

    private void FollowStartup(AppSettings settings, bool asAdministrator, string arguments)
    {
        try
        {
            _startup.AsAdministrator = asAdministrator;
            _startup.TaskArguments = arguments;
            if (settings.StartWithWindows is { } startup) _startup.IsOn = startup;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Alert(T("errors.settings.saveFailed"), failure.Message);
        }
    }

    // The helper never writes settings itself (one writer): it hands them to the ordinary copy, which saves and applies
    // them, then hands them back. Throws when they couldn't be written or handed over.
    private void SaveSettings(AppSettings settings)
    {
        if (_role != AppRole.Helper)
        {
            settings.Save(_settingsPath);
            _helper?.Send(new HelperLink.Message(HelperLink.SettingsSaved));
            return;
        }
        if (_ordinary?.Send(new HelperLink.Message(HelperLink.Save, Text: JsonSerializer.Serialize(settings))) != true)
            throw new IOException(T("errors.settings.saveFailed"));
    }
}
