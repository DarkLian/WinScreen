using static WinScreen.Localization.UiText;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>A Windows call refused a change, for example because the target runs as administrator.</summary>
public sealed class WindowOperationException(string message) : Exception(message);

/// <summary>
/// Changes other programs' windows (pin, opacity, click-through) and remembers how each one
/// looked first, so every change can be undone.
/// </summary>
public sealed partial class WindowController(IEnumerable<string> excludedProcesses, ChangeJournal? journal = null)
{
    public const byte OpacityStep = 26;
    internal const string EnhancedHoverOwnerProperty = "WinScreen.EnhancedHoverOwner.v1";
    internal const string EnhancedHoverActiveProperty = "WinScreen.EnhancedHoverActive.v1";
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);
    public const byte MinimumOpacity = 51;
    public const byte ClickThroughOpacity = 204;

    // Fully transparent. On some Windows 11 PCs even 1 of 255 showed as a faint ghost of the whole window (seen
    // without HDR; the cause isn't known). Views and captures still get the whole picture.
    private const byte HiddenOpacity = 0;

    // A window with no opacity at all gets no clicks, so while it's being used through its view it's at the
    // least opacity that does. That's only while the pointer is on the view.
    private const int PinAttempts = 5;
    private const int PinRetryMilliseconds = 10;
    private const uint ResponsiveMilliseconds = 400;
    private const int CaptionMetric = 4, FrameMetric = 33, PaddingMetric = 92;
    private const int RollUpSlack = 24;

    /// <summary>Parts of Windows itself that can hold the focus but aren't windows a person would change.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow", "ForegroundStaging", "SystemTray_Main", "Shell_InputSwitchTopLevelWindow",
        "ControlCenterWindow", "Xaml_WindowedPopupClass", "EdgeUiInputTopWndClass", "tooltips_class32", "#32768",
        "IME", "MSCTFIME UI",
    };

    private static readonly HashSet<string> ShellPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ShellHost",
        "LockApp",
    };

    private sealed class ChangedWindow
    {
        public required WindowIdentity Identity { get; init; }
        public long OwnedStyles { get; set; }
        public required long OriginalExStyle { get; init; }
        public required bool HadLayeredAttributes { get; init; }
        public uint OriginalColorKey { get; init; }
        public byte OriginalAlpha { get; init; }
        public uint OriginalLayeredFlags { get; init; }
        private const string CurrentState = "Current state: recovery puts back the original from OriginalExStyle and OwnedStyles.";

        [NotJournalled(CurrentState)] public bool Pinned { get; set; }
        [NotJournalled(CurrentState)] public bool Layered { get; set; }
        [NotJournalled(CurrentState)] public bool ClickThrough { get; set; }
        [NotJournalled("Current level: recovery puts back OriginalAlpha.")] public byte Alpha { get; set; } = 255;
        public bool Hidden { get; set; }
        [NotJournalled("An object in this process, gone after a crash; Hidden is journalled as Concealed.")]
        public object? ConcealmentOwner { get; set; }
        [NotJournalled("Lasts only while the pointer is over the window; recovery puts back OriginalAlpha.")]
        public bool SolidWhileHovered { get; set; }
        [NotJournalled("Glass is drawn by WinScreen's own window, which ends with it; the styles it set are in OwnedStyles.")]
        public Color? GlassColor { get; set; }
        [NotJournalled(CurrentState)] public bool PinPaused { get; set; }
        public bool Cropped { get; set; }
        [NotJournalled("A level in between: recovery puts back OriginalAlpha.")] public byte AlphaBeforeHiding { get; set; } = 255;
        [NotJournalled("A level in between: recovery puts back OriginalAlpha.")] public byte? AlphaFromClickThrough { get; set; }
        public WindowPlacement? PlacementBeforePeek { get; set; }
        public WindowPlacement? PlacementBeforeRollUp { get; set; }
        public long BorderlessBits { get; set; }
        public WindowPlacement? PlacementBeforeBorderless { get; set; }
        public Point? BorderlessOrigin { get; set; }
        public bool BorderlessFilled { get; set; }
        public bool Parked { get; set; }
        [NotJournalled("Transient; the journal already records the original placement.")]
        public bool HoverParking { get; set; }
        [NotJournalled("Transient: a visit asked for it; given only while it is off screen.")]
        public bool ReachableWanted { get; set; }
        [NotJournalled("Transient: when a parked window was first seen on screen, not covering a monitor; 0 if not.")]
        public long LooseSince { get; set; }
        [NotJournalled("Transient: covered a monitor (full screen) since it was parked.")]
        public bool CoveredAMonitor { get; set; }
        public long? SwitcherBits { get; set; }
        public bool TaskbarButtonRemoved { get; set; }
        public int? OriginalDarkTitleBar { get; set; }
        public bool BorderHidden { get; set; }
    }

    private HashSet<string> _excluded = new(excludedProcesses, StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ChangedWindow> _changed = [];
    private readonly Lock _gate = new();
    private bool _recoveryBlocked;
    public string? RecoveryWarning { get; private set; }

    /// <summary>True while recovery data can't be read, so new changes must wait.</summary>
    public bool RecoveryBlocked => _recoveryBlocked;

    public IReadOnlyList<string> RestoreFailures { get; private set; } = [];

    /// <summary>The level the default see-through command and click-through use.</summary>
    public byte DefaultOpacity { get; set; } = ClickThroughOpacity;

    internal Action<string, nint>? Checkpoint { get; set; }

    public int PendingRestores
    {
        get
        {
            lock (_gate) return _changed.Count;
        }
    }

    internal bool NeedsRecoveryWatcher
    {
        get { lock (_gate) return _changed.Values.Any(state => state.Hidden || state.PlacementBeforePeek is not null); }
    }

    internal void HideOwned(nint window, object owner, bool solid = false)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.Hidden && !ReferenceEquals(state.ConcealmentOwner, owner))
                throw new WindowOperationException(T("errors.window.showFirst"));
            state.ConcealmentOwner = owner;
            if (solid) ConcealSolid(window);
            else Hide(window);
        }
    }

    internal void ShowOwned(nint window, object owner)
    {
        lock (_gate)
        {
            _changed.TryGetValue(window, out var state);
            if (state is { Hidden: true } && !ReferenceEquals(state.ConcealmentOwner, owner))
                throw new WindowOperationException(T("errors.window.showFirst"));
            Show(window);
            if (state is not null) state.ConcealmentOwner = null;
        }
    }

    internal bool IsConcealedBy(nint window, object owner)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Hidden &&
            state.Identity.Matches() && ReferenceEquals(state.ConcealmentOwner, owner);
    }

    public void SetExcluded(IEnumerable<string> programs)
    {
        lock (_gate)
            _excluded = new HashSet<string>(programs, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Limits WinScreen to these programs. An empty list means every program.</summary>
    public void SetIncluded(IEnumerable<string> programs)
    {
        lock (_gate)
            _included = new HashSet<string>(programs, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsPinned(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() && state.Pinned;
    }

    public bool IsGlass(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() &&
                   state.GlassColor is not null;
    }

    /// <summary>
    /// Makes a window's background see-through and click-through by removing its most common colour,
    /// or puts the background back. Works where a program draws that colour exactly.
    /// Returns the new state.
    /// </summary>
    public bool ToggleGlass(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            Color? before = state.GlassColor;
            Color? after = null;
            if (before is null)
            {
                // Its colour is read off the screen, where a hidden window isn't.
                if (state.Hidden || state.PlacementBeforePeek is not null)
                    throw new WindowOperationException(T("errors.window.showFirst"));
                if (!User32.GetWindowRect(window, out Rect r))
                    throw Failure(T("actions.readPosition"), window);
                after = ScreenColors.MostCommon(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            }

            EnsureLayeringSafe(state);
            Prepare(window, state, User32.WS_EX_LAYERED);
            state.GlassColor = after;
            try
            {
                if (!state.Layered)
                {
                    WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_LAYERED);
                    state.Layered = true;
                }

                WriteLayered(window, state, state.SolidWhileHovered ? (byte)255 : state.Alpha);
            }
            catch (WindowOperationException)
            {
                state.GlassColor = before;
                throw;
            }

            SaveJournal();
            return state.GlassColor is not null;
        }
    }

    public bool IsClickThrough(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() &&
                   state.ClickThrough;
    }

    public bool IsCropped(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() && state.Cropped;
    }

    /// <summary>
    /// Keeps only this part of a window, given in window coordinates: the rest disappears and clicks
    /// there go to whatever is behind. The program isn't resized, so it keeps working as before.
    /// </summary>
    public void Crop(nint window, Rectangle part)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.PlacementBeforeBorderless is not null)
                throw new WindowOperationException(T("errors.borderless.disableFirst"));
            // A hidden window is parked out of sight, so a part chosen on screen would be a part of something else.
            if (state.Hidden || state.PlacementBeforePeek is not null)
                throw new WindowOperationException(T("errors.window.showFirst"));
            if (!state.Cropped && WindowInfo.ShapeOf(window) is not null)
                throw new WindowOperationException(
                    string.Format(T("errors.crop.customShape"), WindowInfo.Title(window)));

            if (part.Width <= 0 || part.Height <= 0)
                throw new WindowOperationException(T("errors.crop.empty"));
            bool croppedBefore = state.Cropped;
            state.Cropped = true; // Recorded first, so a crash during the change can still be undone.
            try
            {
                Prepare(window, state);
                state.Identity.Require();
                nint region = User32.CreateRectRgn(part.Left, part.Top, part.Right, part.Bottom);
                if (User32.SetWindowRgn(window, region, true) == 0)
                {
                    User32.DeleteObject(region);
                    throw Failure(T("actions.crop"), window);
                }
                // Some apps put their own shape straight back (Windows Forms ones with a frame, for one), so a crop
                // that didn't stay is said to have failed rather than shown as on.
                if (WindowInfo.ShapeOf(window) is null)
                    throw new WindowOperationException(string.Format(T("errors.crop.customShape"), WindowInfo.Title(window)));
            }
            catch (WindowOperationException)
            {
                state.Cropped = croppedBefore;
                TrySaveJournal();
                throw;
            }

            SaveJournal();
        }
    }

    /// <summary>Shows the whole window again.</summary>
    public void RemoveCrop(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out ChangedWindow? state) || !state.Cropped)
                return;

            ClearCrop(window, state);
            SaveJournal();
        }
    }

    private static void ClearCrop(nint window, ChangedWindow state)
    {
        EnsureResponsive(window);
        state.Identity.Require();
        if (User32.SetWindowRgn(window, 0, true) == 0)
            throw Failure(T("actions.uncrop"), window);
        state.Cropped = false;
    }

    public bool IsRolledUp(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() &&
                   state.PlacementBeforeRollUp is not null;
    }

    /// <summary>
    /// Shrinks a window to its title bar, or brings its height back. Returns the new state. It's journaled
    /// because a crash would otherwise leave the window that small. Windows that draw their own title bar or
    /// enforce a minimum height can refuse, which is read back and reported rather than left half done.
    /// </summary>
    public bool ToggleRollUp(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.PlacementBeforeRollUp is not null)
            {
                Prepare(window, state);
                RollDown(window, state);
                SaveJournal();
                return false;
            }

            if (state.Hidden || state.PlacementBeforePeek is not null || state.Cropped || state.PlacementBeforeBorderless is not null)
                throw new WindowOperationException(T("errors.window.showNormally"));
            if ((User32.GetWindowLongPtr(window, User32.GWL_STYLE) & User32.WS_CAPTION) != User32.WS_CAPTION)
                throw new WindowOperationException(
                    string.Format(T("errors.rollUp.noCaption"), WindowInfo.Title(window)));

            // Only a window at its usual size can be rolled up, so a minimized or maximized one comes back to that
            // first; one that was maximized is maximized again when it's rolled down.
            bool wasMaximized = BringBackToNormal(window);
            state.PlacementBeforeRollUp = ReadPlacement(window);
            if (wasMaximized) state.PlacementBeforeRollUp = state.PlacementBeforeRollUp.Value with { ShowCommand = ShowMaximized };
            try
            {
                Prepare(window, state); // Journals where it came from before anything changes.
            }
            catch (WindowOperationException)
            {
                state.PlacementBeforeRollUp = null;
                TrySaveJournal();
                throw;
            }

            uint dpi = (uint)Dpi.Of(window);
            int height = User32.GetSystemMetricsForDpi(CaptionMetric, dpi) +
                         2 * (User32.GetSystemMetricsForDpi(FrameMetric, dpi) + User32.GetSystemMetricsForDpi(PaddingMetric, dpi));
            if (!User32.GetWindowRect(window, out Rect before))
                throw Failure(T("actions.readPosition"), window);
            const uint flags = User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;
            state.Identity.Require();
            bool moved = User32.SetWindowPos(window, 0, 0, 0, before.Right - before.Left, height, flags);
            if (!moved || !User32.GetWindowRect(window, out Rect after) ||
                after.Bottom - after.Top > height + Dpi.Scale(RollUpSlack, (int)dpi))
            {
                RollDown(window, state);
                SaveJournal();
                throw new WindowOperationException(
                    string.Format(T("errors.rollUp.minimumHeight"), WindowInfo.Title(window)));
            }

            SaveJournal();
            return true;
        }
    }

    /// <summary>Gives a window its height back and nothing else, so wherever it was moved since is kept.</summary>
    private void RollDown(nint window, ChangedWindow state)
    {
        if (state.PlacementBeforeRollUp is not { } before)
            return;
        WindowPlacement placement = ReadPlacement(window);
        Rect normal = placement.NormalPosition;
        placement.NormalPosition = new Rect(normal.Left, normal.Top, normal.Right,
            normal.Top + before.NormalPosition.Bottom - before.NormalPosition.Top);
        if (before.ShowCommand == ShowMaximized && placement.ShowCommand != ShowMinimized)
            placement.ShowCommand = ShowMaximized;
        WritePlacement(window, placement);
        state.PlacementBeforeRollUp = null;
    }

    private const int ShowMinimized = 2, ShowMaximized = 3, RestoreToMaximized = 2;

    /// <summary>
    /// Brings a minimized or maximized window back to its usual size and place, as its own Restore would. Returns
    /// whether it was maximized (or minimized from maximized), so a change can put that back later.
    /// </summary>
    private bool BringBackToNormal(nint window)
    {
        bool maximized = User32.IsZoomed(window) ||
                         (User32.IsIconic(window) && (ReadPlacement(window).Flags & RestoreToMaximized) != 0);
        if (!User32.IsIconic(window) && !User32.IsZoomed(window)) return false;
        EnsureResponsive(window);
        _changed[window].Identity.Require();
        for (int i = 0; i < 2 && (User32.IsIconic(window) || User32.IsZoomed(window)); i++)
            User32.ShowWindow(window, User32.SW_RESTORE); // Minimized from maximized takes two: back to maximized, then normal.
        if (User32.IsIconic(window) || User32.IsZoomed(window))
            throw new WindowOperationException(T("errors.window.showNormally"));
        return maximized;
    }

    /// <summary>The windows that are pinned right now and can be seen, for the highlight.</summary>
    public nint[] VisiblePinnedWindows()
    {
        lock (_gate)
        {
            return _changed
                .Where(pair =>
                    pair.Value is { Pinned: true, PinPaused: false, Hidden: false, PlacementBeforePeek: null } &&
                    pair.Value.Identity.Matches())
                .Select(pair => pair.Key)
                .ToArray();
        }
    }

    /// <summary>Applies the options that depend on where the mouse is and what the windows are doing.</summary>
    public void Refresh(Point cursor, bool opaqueOnHover, bool pausePinWhenMaximized)
    {
        lock (_gate)
        {
            foreach ((nint window, ChangedWindow state) in _changed.ToArray())
            {
                if (!state.Identity.Matches())
                {
                    _changed.Remove(window);
                    continue;
                }

                if (state.Hidden || state.PlacementBeforePeek is not null)
                    continue;

                try
                {
                    FollowHover(window, state, cursor, opaqueOnHover);
                    FollowMaximized(window, state, pausePinWhenMaximized);
                }
                catch (WindowOperationException)
                {
                    // Tried again on the next pass; nothing here is worth interrupting anyone for.
                }
            }
        }
    }

    private void FollowHover(nint window, ChangedWindow state, Point cursor, bool opaqueOnHover)
    {
        if (!state.Layered || !User32.GetWindowRect(window, out Rect r))
            return;

        // A click-through window is hovered to reach what's behind it, so it must stay see-through.
        bool solid = opaqueOnHover && !state.ClickThrough &&
                     Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(cursor);
        if (solid != state.SolidWhileHovered)
        {
            WriteLayered(window, state, solid ? (byte)255 : state.Alpha);
            state.SolidWhileHovered = solid;
        }
    }

    private void FollowMaximized(nint window, ChangedWindow state, bool pausePinWhenMaximized)
    {
        if (!state.Pinned)
            return;

        bool pause = pausePinWhenMaximized && User32.IsZoomed(window);
        if (pause && !state.PinPaused)
        {
            Prepare(window, state, User32.WS_EX_TOPMOST);
            SetTopmost(window, false, state.Identity);
            state.PinPaused = true;
        }
        else if (!pause && state.PinPaused && User32.GetForegroundWindow() == window)
        {
            Prepare(window, state, User32.WS_EX_TOPMOST);
            SetTopmost(window, true, state.Identity);
            state.PinPaused = false;
        }
    }

    /// <summary>
    /// True when WinScreen may change this window: not the desktop, the taskbar, itself or an excluded
    /// program, and one of the included programs when any are listed.
    /// </summary>
    public bool CanControl(nint window) => Refusal(window) is null;

    /// <summary>True for the desktop, taskbar and other parts of Windows, which fill screens without being apps.</summary>
    public static bool IsShell(nint window) => window == User32.GetShellWindow() || window == User32.GetDesktopWindow() ||
                                               ShellClasses.Contains(WindowInfo.ClassName(window));

    /// <summary>Why WinScreen won't change this window, in words for the person, or null when it will.</summary>
    public string? Refusal(nint window)
    {
        if (window == 0 || !User32.IsWindow(window))
            return T("errors.window.noSelection");
        if (window == User32.GetShellWindow() || window == User32.GetDesktopWindow() ||
            ShellClasses.Contains(WindowInfo.ClassName(window)))
            return T("errors.window.shell");

        User32.GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId)
            return T("errors.window.ownProcess");

        string program = WindowInfo.ProcessName(processId);
        if (ShellPrograms.Contains(program))
            return T("errors.window.shell");
        if (_excluded.Contains(program))
            return string.Format(
                T("errors.program.excluded"), program);
        if (_included.Count > 0 && !_included.Contains(program))
            return string.Format(
                T("errors.program.notIncluded"), program);
        return null;
    }

    /// <summary>True when WinScreen is holding any change it could undo for this window.</summary>
    public bool HasChanges(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches();
    }

    /// <summary>Pins the window on top, or unpins it. Returns the new state.</summary>
    public bool TogglePin(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            bool pin = !state.Pinned;
            Prepare(window, state, User32.WS_EX_TOPMOST);
            if (pin)
                Pin(window, state.Identity);
            else
                SetTopmost(window, false, state.Identity);
            state.Pinned = pin;
            SaveJournal();
            return pin;
        }
    }

    /// <summary>Raises or lowers opacity by <paramref name="delta"/>, staying between the minimum and fully opaque.</summary>
    public byte ChangeOpacity(nint window, int delta)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            byte alpha = (byte)Math.Clamp(state.Alpha + delta, MinimumOpacity, 255);
            ApplyAlpha(window, state, alpha);
            SaveJournal();
            return alpha;
        }
    }

    /// <summary>Sets an exact opacity, staying between the minimum and fully opaque.</summary>
    public byte SetOpacity(nint window, byte alpha)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            alpha = Math.Max(alpha, MinimumOpacity);
            ApplyAlpha(window, state, alpha);
            SaveJournal();
            return alpha;
        }
    }

    /// <summary>The opacity WinScreen has given this window; 255 for a window it hasn't changed.</summary>
    public byte OpacityOf(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() && state.Layered
                ? (state.Hidden ? state.AlphaBeforeHiding : state.Alpha)
                : (byte)255;
    }

    /// <summary>
    /// Puts a window behind all others. A window kept on top can't be at the bottom, so it stops being kept on top:
    /// WinScreen's pin comes off, and a window that keeps itself on top loses that until it's put back.
    /// </summary>
    public void SendToBottom(nint window)
    {
        if (Refusal(window) is { } refusal)
            throw new WindowOperationException(refusal);
        if (IsHidden(window) || IsPeeking(window))
            throw new WindowOperationException(T("errors.window.showFirst"));
        EnsureResponsive(window);
        lock (_gate)
        {
            // A pin paused while maximized has no topmost bit right now, but would come back on top once restored.
            bool pinned = _changed.TryGetValue(window, out ChangedWindow? known) && known.Identity.Matches() && known.Pinned;
            if (pinned || (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0)
            {
                ChangedWindow state = Track(window);
                Prepare(window, state, User32.WS_EX_TOPMOST); // Recorded, so Put back makes it topmost again.
                SetTopmost(window, false, state.Identity);
                state.Pinned = false;
                state.PinPaused = false;
                SaveJournal();
            }
            const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
            if (!User32.SetWindowPos(window, User32.HWND_BOTTOM, 0, 0, 0, 0, flags))
                throw Failure(T("actions.move"), window);
        }
    }

    /// <summary>Makes a solid window as see-through as the default level, or a see-through one solid.</summary>
    public byte ToggleDefaultOpacity(nint window)
    {
        return SetOpacity(window, OpacityOf(window) == 255 ? DefaultOpacity : (byte)255);
    }

    /// <summary>Lets clicks pass through the window, or stops doing so. Returns the new state.</summary>
    public bool ToggleClickThrough(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            bool on = !state.ClickThrough;
            if (on && !state.Layered)
            {
                ApplyAlpha(window, state, DefaultOpacity);
                state.AlphaFromClickThrough = state.Alpha;
            }

            Prepare(window, state, User32.WS_EX_TRANSPARENT);
            long style = ReadExStyle(window);
            WriteExStyle(window, on ? style | User32.WS_EX_TRANSPARENT : style & ~User32.WS_EX_TRANSPARENT);
            state.ClickThrough = on;
            if (!on)
            {
                // Takes back the transparency click-through added, unless the person has since chosen a level.
                if (state.AlphaFromClickThrough == state.Alpha)
                    ApplyAlpha(window, state, 255);
                state.AlphaFromClickThrough = null;
            }

            SaveJournal();
            return on;
        }
    }

    /// <summary>How many open windows WinScreen has made click-through.</summary>
    public int ClickThroughCount
    {
        get { lock (_gate) return _changed.Values.Count(state => state.ClickThrough && state.Identity.Matches()); }
    }

    /// <summary>How many open windows WinScreen pinned. A window that was already on top by itself isn't counted.</summary>
    public int PinnedCount
    {
        get { lock (_gate) return _changed.Values.Count(state => PinnedHere(state) && state.Identity.Matches()); }
    }

    private static bool PinnedHere(ChangedWindow state) => state.Pinned && (state.OriginalExStyle & User32.WS_EX_TOPMOST) == 0;

    /// <summary>
    /// Lets clicks reach every window again. Opacity and everything else stay as they are, except the
    /// see-through level click-through added by itself. Returns how many windows refused.
    /// </summary>
    public int TurnOffClickThroughEverywhere() =>
        Everywhere(state => state.ClickThrough, (window, state) =>
        {
            if (!state.Hidden)
            {
                ToggleClickThrough(window);
                return;
            }
            // Hidden for a view: the window must stay click-through while hidden, so only what it goes back to changes.
            if (state.AlphaFromClickThrough == state.AlphaBeforeHiding) state.AlphaBeforeHiding = 255;
            state.AlphaFromClickThrough = null;
            state.ClickThrough = false;
        });

    /// <summary>Unpins every window WinScreen pinned. Windows that keep themselves on top stay so. Returns how many refused.</summary>
    public int UnpinEverywhere() =>
        Everywhere(PinnedHere, (window, state) =>
        {
            if (state.Hidden) state.Pinned = false; // Hidden for a view: it goes back unpinned when shown.
            else TogglePin(window);
        });

    private int Everywhere(Func<ChangedWindow, bool> wanted, Action<nint, ChangedWindow> change)
    {
        lock (_gate)
        {
            var problems = new List<string>();
            foreach ((nint window, ChangedWindow state) in _changed.ToArray())
            {
                if (!wanted(state) || !state.Identity.Matches()) continue;
                try { change(window, state); }
                catch (WindowOperationException failure) { problems.Add(WindowInfo.Title(window) + ": " + failure.Message); }
            }
            SaveJournal();
            RestoreFailures = problems;
            return problems.Count;
        }
    }

    public bool IsHidden(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() && state.Hidden;
    }

    public bool IsPeeking(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() &&
                   state.PlacementBeforePeek is not null;
    }

    /// <summary>
    /// Makes a window invisible and lets clicks pass through it while it stays where it is. Windows
    /// stops repainting minimized and fully off-screen windows, which would freeze a live view of it.
    /// </summary>
    public void Hide(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.Hidden)
                return;
            if (state.PlacementBeforeBorderless is not null)
                throw new WindowOperationException(T("errors.borderless.disableFirst"));
            EnsureLayeringSafe(state);
            state.AlphaBeforeHiding = state.Layered ? state.Alpha : (byte)255;
            state.Hidden = true;
            Prepare(window, state, User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOPMOST);
            try { Conceal(window, state); }
            catch (WindowOperationException)
            {
                // Not hidden after all (the app refused to become see-through): it mustn't stay recorded as hidden,
                // or WinScreen would keep a recovery record, and a watcher, for a window it never hid.
                state.Hidden = false;
                try
                {
                    RestoreBorder(window, state);
                    if (!state.ClickThrough) WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
                }
                catch (WindowOperationException) { }
                SaveJournal();
                throw;
            }
            LiftWhileHidden(window, state);
        }
    }

    /// <summary>
    /// Makes a window invisible but leaves it clickable and in its place in the stack, for an effect that
    /// draws it again on top. Recovery treats it like any hidden window.
    /// </summary>
    public void ConcealSolid(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.Hidden)
                return;
            if (state.PlacementBeforeBorderless is not null)
                throw new WindowOperationException(T("errors.borderless.disableFirst"));
            EnsureLayeringSafe(state);
            state.AlphaBeforeHiding = state.Layered ? state.Alpha : (byte)255;
            state.Hidden = true;
            Prepare(window, state, User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT);
            Conceal(window, state);
            if (!state.ClickThrough)
                WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
        }
    }

    /// <summary>Makes a hidden window visible and clickable again.</summary>
    public void Show(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out ChangedWindow? state) || !state.Hidden)
                return;

            bool parked = state.Parked;
            if (parked) Unpark(window);
            RestoreSwitcher(window, state);
            RestoreTaskbarButton(window, state);
            if (parked)
            {
                // Let the app repaint at its restored placement while it remains fully transparent.
                User32.SendMessageTimeout(window, 0x000F, 0, 0, 2, 100, out _);
                Thread.Sleep(50);
                for (int step = 1; step < 8; step++)
                {
                    ApplyAlpha(window, state, (byte)(state.AlphaBeforeHiding * step / 8));
                    Thread.Sleep(10);
                }
            }
            Reveal(window, state);
            state.Hidden = false;
            SaveJournal();
        }
    }

    /// <summary>Puts every changed window back. Safe to call at any time, including during a crash.</summary>
    /// <returns>How many windows could not be restored.</returns>
    public int RestoreAll()
    {
        lock (_gate)
        {
            if (_recoveryBlocked)
                RecoverFromJournal(); // Whatever stopped it being read may have cleared.

            int failed = 0;
            var problems = new List<string>();
            foreach ((nint window, ChangedWindow state) in _changed.ToArray())
            {
                if (!state.Identity.Matches())
                {
                    _changed.Remove(window);
                    continue;
                }

                try
                {
                    Restore(window, state);
                    _changed.Remove(window);
                }
                catch (WindowOperationException failure)
                {
                    failed++;
                    problems.Add(WindowInfo.Title(window) + ": " + failure.Message);
                }
            }

            // A record that can't be cleared isn't a window left changed, so it can't hold up exit: the
            // next start finds its window restored or gone. While blocked, nothing has been changed at all.
            if (!_recoveryBlocked)
            {
                try
                {
                    SaveJournal();
                }
                catch (WindowOperationException failure)
                {
                    problems.Add(failure.Message);
                }
            }

            RestoreFailures = problems;
            return failed;
        }
    }

    /// <summary>Puts back windows left changed by a copy of WinScreen that was killed or crashed.</summary>
    public void RecoverFromJournal()
    {
        if (journal is null)
            return;
        using Mutex recovery = RecoveryWatcher.ForRecovery(out bool held);
        if (!held)
        {
            _recoveryBlocked = true;
            RecoveryWarning = T("errors.recovery.busy");
            return;
        }
        try { RecoverJournalUnderLock(); }
        finally { recovery.ReleaseMutex(); }
    }

    private void RecoverJournalUnderLock()
    {
        lock (_gate)
        {
            var records = journal!.Load();
            _recoveryBlocked = journal.ReadFailed;
            if (_recoveryBlocked)
            {
                RecoveryWarning = string.Format(
                    T(
                        "errors.recovery.readFailed"),
                    journal.FilePath);
                return;
            }

            RecoveryWarning = journal.DamagedBackup is { } backup
                ? string.Format(
                    T(
                        "errors.recovery.damaged"),
                    backup)
                : null;

            foreach (WindowRecord record in records)
            {
                // Legacy records cannot distinguish a reused handle. Preserve them for diagnosis rather than
                // writing an unverifiable snapshot into a potentially unrelated application's window.
                var identity = new WindowIdentity((nint)record.Window, record.ProcessId, record.ThreadId,
                    record.ProcessStartTicks, record.IdentityToken);
                if (!identity.Matches())
                    continue;

                var state = new ChangedWindow
                {
                    Identity = identity,
                    Hidden = record.Concealed || WindowInfo.IsConcealed((nint)record.Window),
                    OwnedStyles = record.OwnedStyles,
                    OriginalExStyle = record.ExStyle,
                    HadLayeredAttributes = record.HadLayeredAttributes,
                    OriginalColorKey = record.ColorKey,
                    OriginalAlpha = record.Alpha,
                    OriginalLayeredFlags = record.LayeredFlags,
                    Pinned = true,
                    Cropped = record.Cropped,
                    PlacementBeforePeek = record.Placement is null ? null : ToPlacement(record.Placement),
                    PlacementBeforeRollUp = record.RollUpPlacement is null ? null : ToPlacement(record.RollUpPlacement),
                    BorderlessBits = record.StyleBits,
                    PlacementBeforeBorderless = record.BorderlessPlacement is null ? null : ToPlacement(record.BorderlessPlacement),
                    BorderlessOrigin = record.BorderlessOrigin is { } origin ? new Point(origin[0], origin[1]) : null,
                    BorderlessFilled = record.BorderlessFilled,
                    Parked = record.Parked,
                    SwitcherBits = record.SwitcherBits,
                    TaskbarButtonRemoved = record.TaskbarButtonRemoved,
                    OriginalDarkTitleBar = record.OriginalDarkTitleBar,
                    BorderHidden = record.BorderHidden,
                };
                _changed[(nint)record.Window] = state;
            }

            int failed = RestoreAll();
            if (failed > 0)
                RecoveryWarning =
                    string.Format(
                        T("errors.recovery.pendingCount"),
                        failed) +
                    Environment.NewLine + string.Join(Environment.NewLine, RestoreFailures);
        }
    }

    private bool _anythingConcealed;

    /// <summary>Raised when the first window is concealed and again when the last is revealed.</summary>
    public event Action<bool>? ConcealmentChanged;

    private void SaveJournal()
    {
        PublishGestureState();
        if (_recoveryBlocked)
            throw new WindowOperationException(RecoveryWarning!);
        try
        {
            journal?.Save(_changed.Where(pair => pair.Value.Identity.Matches())
                .Select(pair => ToRecord(pair.Key, pair.Value)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WindowOperationException(
                T("errors.recovery.saveFailedPrefix") + e.Message);
        }

        // After the save, so a watcher that starts now already has this window to recover.
        bool concealed = NeedsRecoveryWatcher;
        if (concealed != _anythingConcealed)
        {
            _anythingConcealed = concealed;
            ConcealmentChanged?.Invoke(concealed);
        }
    }

    /// <summary>For cleanup after a failure that is already being reported.</summary>
    private void TrySaveJournal()
    {
        try
        {
            SaveJournal();
        }
        catch (WindowOperationException)
        {
            // The failure on its way to the person matters more than this one.
        }
    }

    private static WindowRecord ToRecord(nint window, ChangedWindow state)
    {
        return new WindowRecord(window, state.Identity.ProcessId, state.OriginalExStyle, state.HadLayeredAttributes,
            state.OriginalColorKey,
            state.OriginalAlpha, state.OriginalLayeredFlags,
            state.PlacementBeforePeek is { } p ? FromPlacement(p) : null, state.Cropped)
        {
            Concealed = state.Hidden,
            IdentityToken = state.Identity.Token, ProcessStartTicks = state.Identity.ProcessStartTicks,
            ThreadId = state.Identity.ThreadId, OwnedStyles = state.OwnedStyles,
            RollUpPlacement = state.PlacementBeforeRollUp is { } rolled ? FromPlacement(rolled) : null,
            StyleBits = state.BorderlessBits,
            BorderlessPlacement = state.PlacementBeforeBorderless is { } borderless ? FromPlacement(borderless) : null,
            BorderlessOrigin = state.BorderlessOrigin is { } origin ? [origin.X, origin.Y] : null,
            BorderlessFilled = state.BorderlessFilled,
            Parked = state.Parked,
            SwitcherBits = state.SwitcherBits,
            TaskbarButtonRemoved = state.TaskbarButtonRemoved,
            OriginalDarkTitleBar = state.OriginalDarkTitleBar,
            BorderHidden = state.BorderHidden,
        };
    }

    private static int[] FromPlacement(WindowPlacement p) =>
    [
        p.Flags, p.ShowCommand, p.MinimizedPosition.X, p.MinimizedPosition.Y, p.MaximizedPosition.X,
        p.MaximizedPosition.Y,
        p.NormalPosition.Left, p.NormalPosition.Top, p.NormalPosition.Right, p.NormalPosition.Bottom,
    ];

    private static WindowPlacement ToPlacement(int[] v) => new()
    {
        Length = Marshal.SizeOf<WindowPlacement>(),
        Flags = v[0],
        ShowCommand = v[1],
        MinimizedPosition = new NativePoint { X = v[2], Y = v[3] },
        MaximizedPosition = new NativePoint { X = v[4], Y = v[5] },
        NormalPosition = new Rect(v[6], v[7], v[8], v[9]),
    };

    private void FinishPeek(nint window, ChangedWindow state)
    {
        if (state.PlacementBeforePeek is not { } placement)
            return;

        state.Identity.Require();
        var failures = new List<string>();
        if (state.Parked)
        {
            User32.SendMessageTimeout(window, 0x001F /* WM_CANCELMODE */, 0, 0, 2, 30, out _);
            // Only enhanced sessions leave this marker after a crash. Ordinary recovery keeps its
            // existing message semantics; no blanket leave is sent to the source's child windows.
            nint owner = User32.GetProp(window, EnhancedHoverOwnerProperty);
            if (owner != 0)
            {
                User32.SendMessageTimeout(window, 0x02A3 /* WM_MOUSELEAVE */, 0, 0, 2, 30, out _);
                if (User32.GetProp(window, EnhancedHoverOwnerProperty) == owner)
                {
                    RemoveProp(window, EnhancedHoverOwnerProperty);
                    if (User32.GetProp(window, EnhancedHoverActiveProperty) == owner) RemoveProp(window, EnhancedHoverActiveProperty);
                }
            }
        }
        // Full screen now, it may have gone so while parked and will go back to the parked spot when it leaves.
        bool fullScreen = state.Parked && User32.GetWindowRect(window, out Rect shown) && FillsAMonitor(
            Rectangle.FromLTRB(shown.Left, shown.Top, shown.Right, shown.Bottom), Screen.AllScreens.Select(screen => screen.Bounds).ToArray());
        Attempt("peek-pin", () => SetTopmost(window, state.Pinned, state.Identity));
        Attempt("peek-placement", () => WritePlacement(window, placement));
        if (state.Hidden)
            Attempt("peek-hide", () =>
            {
                Conceal(window, state);
                LiftWhileHidden(window, state);
            });
        if (failures.Count > 0)
            throw new WindowOperationException(string.Join(Environment.NewLine, failures));
        if (fullScreen) WatchForReturn(window, placement);
        state.PlacementBeforePeek = null;
        state.Parked = false;
        state.HoverParking = false;
        state.ReachableWanted = false;
        state.CoveredAMonitor = false;
        state.LooseSince = 0;

        void Attempt(string step, Action action)
        {
            try
            {
                state.Identity.Require();
                Checkpoint?.Invoke(step, window);
                action();
            }
            catch (WindowOperationException e)
            {
                failures.Add(e.Message);
            }
        }
    }

    private void Conceal(nint window, ChangedWindow state)
    {
        ApplyAlpha(window, state, HiddenOpacity);
        WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_TRANSPARENT);
        HideBorder(window, state);
    }

    /// <summary>
    /// Windows 11 draws a thin border round each window that doesn't fade with the window, so a hidden one
    /// would still show its outline. It's taken off while hidden. Windows can't say what color an app chose
    /// for it, so it goes back to the default afterwards.
    /// </summary>
    private static void HideBorder(nint window, ChangedWindow state)
    {
        if (state.BorderHidden || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        state.BorderHidden = true; // Recorded first, so recovery puts it back even if this is the last thing that happens.
        if (Dwmapi.DwmSetWindowAttribute(window, Dwmapi.DWMWA_BORDER_COLOR, Dwmapi.DWMWA_COLOR_NONE, sizeof(int)) != 0)
            state.BorderHidden = false;
    }

    private static void RestoreBorder(nint window, ChangedWindow state)
    {
        if (!state.BorderHidden) return;
        Dwmapi.DwmSetWindowAttribute(window, Dwmapi.DWMWA_BORDER_COLOR, Dwmapi.DWMWA_COLOR_DEFAULT, sizeof(int));
        state.BorderHidden = false;
    }

    private void Reveal(nint window, ChangedWindow state)
    {
        state.SolidWhileHovered = false;
        RestoreBorder(window, state);
        if (state.Layered || state.AlphaBeforeHiding != 255) // Never made see-through: nothing to put back.
            ApplyAlpha(window, state, state.AlphaBeforeHiding);
        if (!state.ClickThrough)
            WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
        if (!state.Pinned)
            TrySetTopmost(window, false, state.Identity);
    }

    /// <summary>
    /// Chromium and Electron apps stop drawing once other windows cover them, which would freeze their
    /// live view. A hidden window can be neither seen nor clicked, so keeping it above the others costs
    /// nothing and keeps it drawing. It stays out of <see cref="ChangedWindow.Pinned"/> because the person
    /// didn't pin it, and only liveness depends on it, so a refusal isn't worth stopping for.
    /// </summary>
    private static void LiftWhileHidden(nint window, ChangedWindow state)
    {
        if (!state.Pinned)
            TrySetTopmost(window, true, state.Identity);
    }

    /// <summary>
    /// Changes the topmost state of a window that isn't in use. Windows can ignore that for a background
    /// window, so it's brought forward for a moment if needed and the focus goes back where it was.
    /// </summary>
    private static void TrySetTopmost(nint window, bool topmost, WindowIdentity identity)
    {
        try
        {
            SetTopmost(window, topmost, identity);
            return;
        }
        catch (WindowOperationException)
        {
            // Fall through to bringing it forward.
        }

        nint previous = User32.GetForegroundWindow();
        try
        {
            for (int attempt = 0; attempt < PinAttempts && User32.GetForegroundWindow() != window; attempt++)
            {
                User32.SetForegroundWindow(window);
                if (User32.GetForegroundWindow() != window)
                    Thread.Sleep(PinRetryMilliseconds);
            }

            SetTopmost(window, topmost, identity);
        }
        catch (WindowOperationException)
        {
            // Owned in the journal, so Restore all windows and crash recovery still put it right.
        }
        finally
        {
            if (previous != 0 && previous != window)
                User32.SetForegroundWindow(previous);
        }
    }

    private void Move(nint window, Point position)
    {
        EnsureResponsive(window);
        _changed[window].Identity.Require();
        const uint flags = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;
        if (!User32.SetWindowPos(window, 0, position.X, position.Y, 0, 0, flags))
            throw Failure(T("actions.move"), window);
    }

    private static WindowPlacement ReadPlacement(nint window)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!User32.GetWindowPlacement(window, ref placement))
            throw Failure(T("actions.readPosition"), window);
        return placement;
    }

    private void WritePlacement(nint window, WindowPlacement placement)
    {
        EnsureResponsive(window);
        _changed[window].Identity.Require();
        const int showNormal = 1;
        if (placement.ShowCommand == showNormal)
            placement.ShowCommand = User32.SW_SHOWNOACTIVATE;
        if (!User32.SetWindowPlacement(window, ref placement))
            throw Failure(T("actions.restorePosition"), window);
    }

    private void Restore(nint window, ChangedWindow state)
    {
        state.Identity.Require();
        var failures = new List<string>();
        if (state.PlacementBeforePeek is not null)
            Attempt("restore-placement", () => FinishPeek(window, state));
        if (state.PlacementBeforeRollUp is not null)
            Attempt("restore-rollup", () => RollDown(window, state));
        if (state.Cropped)
            Attempt("restore-crop", () => ClearCrop(window, state));
        if (state.PlacementBeforeBorderless is not null)
            Attempt("restore-borderless", () => RemoveBorderless(window, state));
        if (state.SwitcherBits is not null)
            Attempt("restore-switcher", () => RestoreSwitcher(window, state));
        if (state.TaskbarButtonRemoved)
            Attempt("restore-taskbar", () => RestoreTaskbarButton(window, state));
        if (state.OriginalDarkTitleBar is not null)
            Attempt("restore-titlebar", () => RestoreTitleBar(window, state));
        if (state.BorderHidden)
            Attempt("restore-border", () => RestoreBorder(window, state));
        bool restoredAttributes = !state.HadLayeredAttributes;
        if ((state.OwnedStyles & User32.WS_EX_LAYERED) != 0 && state.HadLayeredAttributes)
            Attempt("restore-alpha", () =>
            {
                state.Identity.Require();
                if (!User32.SetLayeredWindowAttributes(window, state.OriginalColorKey, state.OriginalAlpha,
                        state.OriginalLayeredFlags))
                    throw Failure(T("actions.restoreOpacity"), window);
                restoredAttributes = true;
            });
        Attempt("restore-styles", () =>
        {
            long mask = state.OwnedStyles & ~User32.WS_EX_TOPMOST;
            if (mask != 0)
                WriteExStyle(window, (ReadExStyle(window) & ~mask) | (state.OriginalExStyle & mask));
            state.OwnedStyles &= restoredAttributes ? ~mask : ~(mask & ~User32.WS_EX_LAYERED);
            state.Layered = (state.OriginalExStyle & User32.WS_EX_LAYERED) != 0;
            state.Alpha = state.OriginalAlpha;
            state.ClickThrough = (state.OriginalExStyle & User32.WS_EX_TRANSPARENT) != 0;
            state.GlassColor = null;
            state.SolidWhileHovered = false;
        });
        if ((state.OwnedStyles & User32.WS_EX_TOPMOST) != 0)
            Attempt("restore-pin", () =>
            {
                SetTopmost(window, (state.OriginalExStyle & User32.WS_EX_TOPMOST) != 0, state.Identity);
                state.OwnedStyles &= ~User32.WS_EX_TOPMOST;
                state.Pinned = (state.OriginalExStyle & User32.WS_EX_TOPMOST) != 0;
                state.PinPaused = false;
            });
        if (failures.Count > 0)
            throw new WindowOperationException(string.Join(Environment.NewLine, failures));
        state.Hidden = false;
        state.ConcealmentOwner = null;

        void Attempt(string step, Action action)
        {
            try
            {
                state.Identity.Require();
                Checkpoint?.Invoke(step, window);
                action();
            }
            catch (WindowOperationException e)
            {
                failures.Add(e.Message);
            }
        }
    }

    public bool RestoreWindow(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state))
                return true;
            if (!state.Identity.Matches())
                _changed.Remove(window);
            else
            {
                try
                {
                    Restore(window, state);
                    _changed.Remove(window);
                }
                catch (WindowOperationException failure)
                {
                    RestoreFailures = [WindowInfo.Title(window) + ": " + failure.Message];
                    SaveJournal();
                    return false;
                }
            }

            SaveJournal();
            return true;
        }
    }

    public int EndExperimentalEffects()
    {
        lock (_gate)
        {
            int failed = 0;
            foreach (var (window, state) in _changed.ToArray())
            {
                if (!state.Identity.Matches()) continue;
                try
                {
                    if (state.Cropped) ClearCrop(window, state);
                    if (state.SwitcherBits is not null) RestoreSwitcher(window, state);
                    if (state.PlacementBeforeRollUp is not null) RollDown(window, state);
                    if (state.GlassColor is not null)
                    {
                        Prepare(window, state, User32.WS_EX_LAYERED);
                        state.GlassColor = null;
                        WriteLayered(window, state, state.SolidWhileHovered ? (byte)255 : state.Alpha);
                    }
                }
                catch (WindowOperationException)
                {
                    failed++;
                }
            }

            try
            {
                SaveJournal();
            }
            catch (WindowOperationException)
            {
                failed++;
            }

            return failed;
        }
    }

    private void Prepare(nint window, ChangedWindow state, long styles = 0)
    {
        state.Identity.Require();
        state.OwnedStyles |= styles;
        SaveJournal(); // Must succeed before the first native change, including its intent/owned bits.
        EnsureResponsive(window);
        Checkpoint?.Invoke("before-mutation", window);
    }

    private ChangedWindow Track(nint window)
    {
        if (Refusal(window) is { } refusal)
            throw new WindowOperationException(refusal);
        if (_recoveryBlocked)
            throw new WindowOperationException(RecoveryWarning!);

        if (_changed.TryGetValue(window, out ChangedWindow? existing))
        {
            if (existing.Identity.Matches())
                return existing;
            _changed.Remove(window);
        }

        var identity = WindowIdentity.Capture(window);
        long style = ReadExStyle(window);
        bool layered = (style & User32.WS_EX_LAYERED) != 0;
        uint key = 0, flags = 0;
        byte alpha = 255;
        bool hadAttributes = layered && User32.GetLayeredWindowAttributes(window, out key, out alpha, out flags);
        identity.Require(); // The snapshot must belong to the same lifetime as its recovery identity.

        var state = new ChangedWindow
        {
            Identity = identity,
            OriginalExStyle = style,
            HadLayeredAttributes = hadAttributes,
            OriginalColorKey = key,
            OriginalAlpha = alpha,
            OriginalLayeredFlags = flags,
            Pinned = (style & User32.WS_EX_TOPMOST) != 0,
        };
        if (hadAttributes && (flags & User32.LWA_ALPHA) != 0)
        {
            state.Layered = true;
            state.Alpha = alpha;
        }

        _changed[window] = state;
        return state;
    }

    private void ApplyAlpha(nint window, ChangedWindow state, byte alpha)
    {
        EnsureLayeringSafe(state);
        Prepare(window, state, User32.WS_EX_LAYERED);
        if (!state.Layered)
        {
            WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_LAYERED);
            // Some apps (WPF ones, among others) manage this style themselves and drop it again at once: then there
            // is no way to make them see-through, and saying so beats a failed system call.
            if ((ReadExStyle(window) & User32.WS_EX_LAYERED) == 0)
                throw new WindowOperationException(T("errors.opacity.refused"));
            state.Layered = true;
        }

        WriteLayered(window, state, state.SolidWhileHovered && !state.Hidden ? (byte)255 : alpha);
        state.Alpha = alpha;
    }

    private static void EnsureLayeringSafe(ChangedWindow state)
    {
        if ((state.OriginalExStyle & User32.WS_EX_LAYERED) != 0 && !state.HadLayeredAttributes)
            throw new WindowOperationException(
                T("errors.opacity.customTransparency"));
    }

    private void WriteLayered(nint window, ChangedWindow state, byte alpha)
    {
        state.Identity.Require();
        uint flags = User32.LWA_ALPHA;
        uint key = 0;
        if (state.GlassColor is { } color)
        {
            flags |= User32.LWA_COLORKEY;
            key = (uint)(color.R | color.G << 8 | color.B << 16);
        }

        if (!User32.SetLayeredWindowAttributes(window, key, alpha, flags))
            throw Failure(T("actions.changeOpacity"), window);
    }

    /// <summary>
    /// Windows can report success for HWND_TOPMOST on a background window and ignore it (seen on
    /// Windows 11 26H2), so the window is brought forward first and the result is read back.
    /// </summary>
    public static void Pin(nint window)
    {
        Pin(window, WindowIdentity.Capture(window));
    }

    private static void Pin(nint window, WindowIdentity identity)
    {
        EnsureResponsive(window);
        for (int attempt = 0; attempt < PinAttempts; attempt++)
        {
            identity.Require();
            if (User32.GetForegroundWindow() != window)
                User32.SetForegroundWindow(window);

            if (User32.GetForegroundWindow() == window)
            {
                SetTopmost(window, true, identity);
                TopLayer.Raise(window); // Still under the taskbar, so its icons can be reached.
                return;
            }

            Thread.Sleep(PinRetryMilliseconds);
        }

        throw new WindowOperationException(string.Format(
            T("errors.pin.foregroundDenied"),
            WindowInfo.Title(window)));
    }

    private static bool IsTopmost(nint window) => (ReadExStyle(window) & User32.WS_EX_TOPMOST) != 0;

    private static void SetTopmost(nint window, bool topmost, WindowIdentity identity)
    {
        EnsureResponsive(window);
        identity.Require();
        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
        nint after = topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
        if (!User32.SetWindowPos(window, after, 0, 0, 0, 0, flags))
            throw Failure(topmost ? T("actions.pin") : T("actions.unpin"), window);
        if (IsTopmost(window) != topmost)
            throw new WindowOperationException(string.Format(T("errors.window.changeIgnored"),
                topmost ? T("actions.pin") : T("actions.unpin"), WindowInfo.Title(window)));
    }

    private static long ReadExStyle(nint window)
    {
        nint style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("actions.read"), window);
        return style;
    }

    private void WriteExStyle(nint window, long style)
    {
        EnsureResponsive(window);
        _changed[window].Identity.Require();
        nint previous = User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, (nint)style);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("actions.change"), window);

        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED;
        _changed[window].Identity.Require();
        if (!User32.SetWindowPos(window, 0, 0, 0, 0, 0, flags | User32.SWP_NOZORDER))
            throw Failure(T("actions.refreshFrame"), window);
    }

    private static WindowOperationException Failure(string action, nint window)
    {
        string reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return new WindowOperationException(string.Format(T("errors.window.operationFailed"), action,
            WindowInfo.Title(window), reason));
    }

    private static void EnsureResponsive(nint window)
    {
        if (User32.SendMessageTimeout(window, 0, 0, 0, 0x3, ResponsiveMilliseconds, out _) == 0)
            throw new WindowOperationException(T("errors.window.unresponsive"));
    }
}

/// <summary>Read-only facts about a window.</summary>
public static class WindowInfo
{
    public static string ExecutablePath(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName ?? "";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "";
        }
    }

    /// <summary>The facts about a window, as label and value, for the person to read or copy.</summary>
    public static (string Label, string Value)[] Facts(nint window)
    {
        uint thread = User32.GetWindowThreadProcessId(window, out uint processId);
        string frame = TryGetFrameBounds(window, out Rectangle bounds, out _)
            ? $"{bounds.Width}×{bounds.Height} @ {bounds.Left}, {bounds.Top}"
            : T("common.unavailable");
        string path = ExecutablePath(window);
        Screen screen = Screen.FromHandle(window);
        return
        [
            (T("window.info.title"), Title(window)),
            (T("window.info.program"), ProcessName(processId)),
            (T("window.info.path"), path.Length > 0 ? path : T("common.unavailable")),
            (T("window.info.hwnd"), $"0x{window:X}"),
            (T("window.info.processId"), processId.ToString()),
            (T("window.info.threadId"), thread.ToString()),
            (T("window.info.class"), ClassName(window)),
            (T("window.info.frame"), frame),
            (T("window.info.display"), $"{screen.DeviceName} ({screen.Bounds.Width}×{screen.Bounds.Height})"),
            (T("pip.scaling"), $"{Dpi.Of(window) * 100 / Dpi.Default}%"),
            (T("window.info.elevated"), ProcessInfo.IsElevated(processId) ? T("common.yes") : T("common.no")),
        ];
    }

    /// <summary>
    /// True for a borderless window covering its whole monitor, which is how most games run today. A maximized
    /// window has a title bar and leaves the taskbar alone, so it doesn't count.
    /// </summary>
    public static bool FillsMonitor(nint window)
    {
        if (window == 0 || WindowController.IsShell(window) || User32.IsIconic(window) ||
            (User32.GetWindowLongPtr(window, User32.GWL_STYLE) & User32.WS_CAPTION) == User32.WS_CAPTION ||
            !User32.GetWindowRect(window, out Rect r))
            return false;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(Screen.FromHandle(window).Bounds);
    }

    public static bool IsAvailable(nint window)
    {
        if (!User32.IsWindowVisible(window) || User32.IsIconic(window))
            return false;
        return Dwmapi.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    public static unsafe string Title(nint window)
    {
        char* buffer = stackalloc char[256];
        int length = User32.GetWindowText(window, buffer, 256);
        return new string(buffer, 0, length);
    }

    public static unsafe string ClassName(nint window)
    {
        char* buffer = stackalloc char[256];
        int length = User32.GetClassName(window, buffer, 256);
        return new string(buffer, 0, length);
    }

    public static string ProgramOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint processId);
        return ProcessName(processId);
    }

    /// <summary>True for a window a person thinks of as the program, rather than a dialog or a popup.</summary>
    public static bool IsMainWindow(nint window)
    {
        return User32.IsWindowVisible(window)
               && User32.GetWindow(window, User32.GW_OWNER) == 0
               && (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) == 0
               && Title(window).Length > 0;
    }

    /// <summary>True while a window is kept invisible, as a hidden or shrunk original is.</summary>
    /// <summary>Whether <paramref name="window"/> belongs to <paramref name="owner"/>, directly or through other owned windows.</summary>
    public static bool IsOwnedBy(nint window, nint owner)
    {
        for (int depth = 0; depth < 8 && window != 0; depth++)
        {
            window = User32.GetWindow(window, User32.GW_OWNER);
            if (window == owner) return true;
        }
        return false;
    }

    public static bool IsConcealed(nint window)
    {
        return (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_LAYERED) != 0
               && User32.GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags)
               && (flags & User32.LWA_ALPHA) != 0
               && alpha <= 1;
    }

    /// <summary>The part a region keeps of a window, in window coordinates, or null when there is no region.</summary>
    public static Rectangle? ShapeOf(nint window)
    {
        return User32.GetWindowRgnBox(window, out Rect box) == 0
            ? null
            : Rectangle.FromLTRB(box.Left, box.Top, box.Right, box.Bottom);
    }

    /// <summary>Where a window's frame is on screen, without the invisible resize margins GetWindowRect counts.</summary>
    public static bool TryGetFrameBounds(nint window, out Rectangle bounds, out Rectangle whole)
    {
        bounds = whole = Rectangle.Empty;
        if (!User32.GetWindowRect(window, out Rect r))
            return false;

        whole = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        bounds = Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, out Rect frame,
            Marshal.SizeOf<Rect>()) == 0
            ? Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom)
            : whole;
        return true;
    }

    /// <summary>
    /// The part of a live preview's source that shows the window, leaving out its invisible resize
    /// margins. Preview coordinates start at the GetWindowRect corner, but the size Windows reports for
    /// them is only the frame's, so this deliberately reaches past <paramref name="sourceSize"/>: clipping
    /// to it would lose the window's right and bottom edges. A minimized window has no frame to measure.
    /// </summary>
    public static Rectangle VisiblePart(nint window, Size sourceSize)
    {
        var all = new Rectangle(Point.Empty, sourceSize);
        if (User32.IsIconic(window) || !TryGetFrameBounds(window, out Rectangle frame, out Rectangle whole))
            return all;

        frame.Offset(-whole.Left, -whole.Top);
        return frame.Width > 0 && frame.Height > 0 ? frame : all;
    }

    /// <summary>
    /// Where a window can really be seen on screen. GetWindowRect also counts the invisible resize
    /// margins of a normal window, and knows nothing of a crop.
    /// </summary>
    public static bool TryGetVisibleBounds(nint window, out Rectangle bounds)
    {
        if (!TryGetFrameBounds(window, out bounds, out Rectangle whole))
            return false;

        if (ShapeOf(window) is { } shape)
        {
            shape.Offset(whole.Location);
            bounds = Rectangle.Intersect(bounds, shape);
        }

        return !bounds.IsEmpty;
    }

    public static string ProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "";
        }
    }
}
