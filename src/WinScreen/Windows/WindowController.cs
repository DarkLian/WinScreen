using static WinScreen.Settings.UiText;
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
public sealed class WindowController(IEnumerable<string> excludedProcesses, ChangeJournal? journal = null)
{
    public const byte OpacityStep = 26;
    public const byte MinimumOpacity = 51;
    public const byte ClickThroughOpacity = 204;

    private const byte HiddenOpacity = 1;
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
        public bool Pinned { get; set; }
        public bool Layered { get; set; }
        public bool ClickThrough { get; set; }
        public byte Alpha { get; set; } = 255;
        public bool Hidden { get; set; }
        public bool SolidWhileHovered { get; set; }
        public Color? GlassColor { get; set; }
        public bool PinPaused { get; set; }
        public bool Cropped { get; set; }
        public byte AlphaBeforeHiding { get; set; } = 255;
        public byte? AlphaFromClickThrough { get; set; }
        public WindowPlacement? PlacementBeforePeek { get; set; }
        public WindowPlacement? PlacementBeforeRollUp { get; set; }
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
                if (!User32.GetWindowRect(window, out Rect r))
                    throw Failure(T("read the position of"), window);
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
            if (!state.Cropped && WindowInfo.ShapeOf(window) is not null)
                throw new WindowOperationException(
                    string.Format(T("\"{0}\" draws its own shape, so it can't be cropped."), WindowInfo.Title(window)));

            if (part.Width <= 0 || part.Height <= 0)
                throw new WindowOperationException(T("Choose a nonempty crop."));
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
                    throw Failure(T("crop"), window);
                }
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
            throw Failure(T("uncrop"), window);
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

            if (state.Hidden || state.PlacementBeforePeek is not null || state.Cropped ||
                User32.IsIconic(window) || User32.IsZoomed(window))
                throw new WindowOperationException(T("Show this window normally first, then try again."));
            if ((User32.GetWindowLongPtr(window, User32.GWL_STYLE) & User32.WS_CAPTION) != User32.WS_CAPTION)
                throw new WindowOperationException(
                    string.Format(T("\"{0}\" has no standard title bar, so it can't be rolled up."), WindowInfo.Title(window)));

            state.PlacementBeforeRollUp = ReadPlacement(window);
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
                throw Failure(T("read the position of"), window);
            const uint flags = User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;
            state.Identity.Require();
            bool moved = User32.SetWindowPos(window, 0, 0, 0, before.Right - before.Left, height, flags);
            if (!moved || !User32.GetWindowRect(window, out Rect after) ||
                after.Bottom - after.Top > height + Dpi.Scale(RollUpSlack, (int)dpi))
            {
                RollDown(window, state);
                SaveJournal();
                throw new WindowOperationException(
                    string.Format(T("\"{0}\" keeps its own minimum height, so it can't be rolled up."), WindowInfo.Title(window)));
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
        WritePlacement(window, placement);
        state.PlacementBeforeRollUp = null;
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
            return T("No window is selected. Click the window you want, then try again.");
        if (window == User32.GetShellWindow() || window == User32.GetDesktopWindow() ||
            ShellClasses.Contains(WindowInfo.ClassName(window)))
            return T("The desktop, taskbar and other parts of Windows can't be changed.");

        User32.GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId)
            return T("Use this on the original window, not on a WinScreen window.");

        string program = WindowInfo.ProcessName(processId);
        if (ShellPrograms.Contains(program))
            return T("The desktop, taskbar and other parts of Windows can't be changed.");
        if (_excluded.Contains(program))
            return string.Format(
                T("{0} is on your list of programs WinScreen leaves alone. You can change this in Settings."), program);
        if (_included.Count > 0 && !_included.Contains(program))
            return string.Format(
                T("{0} isn't on your list of programs WinScreen works on. You can change this in Settings."), program);
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
                ? state.Alpha
                : (byte)255;
    }

    /// <summary>Puts a window behind all others. A pinned window stays in front of them by definition, so it's refused.</summary>
    public void SendToBottom(nint window)
    {
        if (Refusal(window) is { } refusal)
            throw new WindowOperationException(refusal);
        if (IsHidden(window) || IsPeeking(window))
            throw new WindowOperationException(T("Show this window first, then try again."));
        if ((User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0)
            throw new WindowOperationException(T("Unpin this window first, then send it to the bottom."));
        EnsureResponsive(window);
        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
        if (!User32.SetWindowPos(window, User32.HWND_BOTTOM, 0, 0, 0, 0, flags))
            throw Failure(T("move"), window);
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
            EnsureLayeringSafe(state);
            state.AlphaBeforeHiding = state.Layered ? state.Alpha : (byte)255;
            state.Hidden = true;
            Prepare(window, state, User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOPMOST);
            Conceal(window, state);
            LiftWhileHidden(window, state);
        }
    }

    /// <summary>Makes a hidden window visible and clickable again.</summary>
    public void Show(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out ChangedWindow? state) || !state.Hidden)
                return;

            Reveal(window, state);
            state.Hidden = false;
            SaveJournal();
        }
    }

    /// <summary>
    /// Brings a window forward at this position so it can be used directly, for now. A maximized window
    /// stays as it is, because resizing it would move everything inside it away from the spot being
    /// clicked. Returns where the window's top-left corner ended up.
    /// </summary>
    public Point BeginPeek(nint window, Point position)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            bool maximized = User32.IsZoomed(window);
            if (state.PlacementBeforePeek is null)
            {
                state.PlacementBeforePeek = ReadPlacement(window);
                Prepare(window, state, User32.WS_EX_TOPMOST);
                try
                {
                    if (state.Hidden)
                        Reveal(window, state);
                    if (maximized)
                    {
                        User32.SetForegroundWindow(window);
                        if (User32.GetForegroundWindow() != window)
                            throw new WindowOperationException(T("The source window could not be activated."));
                    }
                    else
                    {
                        Move(window, position);
                        Checkpoint?.Invoke("peek-moved", window);
                        Pin(window, state.Identity);
                    }
                }
                catch
                {
                    try
                    {
                        FinishPeek(window, state);
                        SaveJournal();
                    }
                    catch (WindowOperationException)
                    {
                        /* Keep the placement and durable record for retry. */
                    }

                    throw;
                }
            }

            if (!User32.GetWindowRect(window, out Rect r))
                throw Failure(T("read the position of"), window);
            return new Point(r.Left, r.Top);
        }
    }

    /// <summary>Sends a window back to where it was before <see cref="BeginPeek"/>.</summary>
    public void EndPeek(nint window)
    {
        lock (_gate)
        {
            if (_changed.TryGetValue(window, out ChangedWindow? state) && User32.IsWindow(window))
                FinishPeek(window, state);
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
        lock (_gate)
        {
            var records = journal.Load();
            _recoveryBlocked = journal.ReadFailed;
            if (_recoveryBlocked)
            {
                RecoveryWarning = string.Format(
                    T(
                        "WinScreen can't read its recovery file, so new changes are paused. Make sure {0} isn't open in another program, then choose Restore all windows to try again."),
                    journal.FilePath);
                return;
            }

            RecoveryWarning = journal.DamagedBackup is { } backup
                ? string.Format(
                    T(
                        "WinScreen's recovery file was damaged, so a copy was kept at {0}. A window changed before WinScreen stopped may stay that way until you close and reopen it."),
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
                };
                _changed[(nint)record.Window] = state;
            }

            int failed = RestoreAll();
            if (failed > 0)
                RecoveryWarning =
                    string.Format(
                        T("{0} window(s) couldn't be recovered yet. Choose Restore all windows to try again."),
                        failed) +
                    Environment.NewLine + string.Join(Environment.NewLine, RestoreFailures);
        }
    }

    private void SaveJournal()
    {
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
                T("WinScreen couldn't save its recovery data, so this change wasn't made: ") + e.Message);
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
            IdentityToken = state.Identity.Token, ProcessStartTicks = state.Identity.ProcessStartTicks,
            ThreadId = state.Identity.ThreadId, OwnedStyles = state.OwnedStyles,
            RollUpPlacement = state.PlacementBeforeRollUp is { } rolled ? FromPlacement(rolled) : null,
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
        state.PlacementBeforePeek = null;

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
    }

    private void Reveal(nint window, ChangedWindow state)
    {
        state.SolidWhileHovered = false;
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
            throw Failure(T("move"), window);
    }

    private static WindowPlacement ReadPlacement(nint window)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!User32.GetWindowPlacement(window, ref placement))
            throw Failure(T("read the position of"), window);
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
            throw Failure(T("put back"), window);
    }

    private void Restore(nint window, ChangedWindow state)
    {
        state.Identity.Require();
        var failures = new List<string>();
        state.Hidden = false;
        if (state.PlacementBeforePeek is not null)
            Attempt("restore-placement", () => FinishPeek(window, state));
        if (state.PlacementBeforeRollUp is not null)
            Attempt("restore-rollup", () => RollDown(window, state));
        if (state.Cropped)
            Attempt("restore-crop", () => ClearCrop(window, state));
        bool restoredAttributes = !state.HadLayeredAttributes;
        if ((state.OwnedStyles & User32.WS_EX_LAYERED) != 0 && state.HadLayeredAttributes)
            Attempt("restore-alpha", () =>
            {
                state.Identity.Require();
                if (!User32.SetLayeredWindowAttributes(window, state.OriginalColorKey, state.OriginalAlpha,
                        state.OriginalLayeredFlags))
                    throw Failure(T("restore the transparency of"), window);
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
            state.Layered = true;
        }

        WriteLayered(window, state, state.SolidWhileHovered && !state.Hidden ? (byte)255 : alpha);
        state.Alpha = alpha;
    }

    private static void EnsureLayeringSafe(ChangedWindow state)
    {
        if ((state.OriginalExStyle & User32.WS_EX_LAYERED) != 0 && !state.HadLayeredAttributes)
            throw new WindowOperationException(
                T("This window draws its own transparency, so WinScreen can't change it."));
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
            throw Failure(T("change the transparency of"), window);
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
                return;
            }

            Thread.Sleep(PinRetryMilliseconds);
        }

        throw new WindowOperationException(string.Format(
            T("Windows wouldn't bring \"{0}\" forward to pin it. Click the window and try the pin command again."),
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
            throw Failure(topmost ? T("pin") : T("unpin"), window);
        if (IsTopmost(window) != topmost)
            throw new WindowOperationException(string.Format(T("Windows ignored the request to {0} \"{1}\"."),
                topmost ? T("pin") : T("unpin"), WindowInfo.Title(window)));
    }

    private static long ReadExStyle(nint window)
    {
        nint style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("read"), window);
        return style;
    }

    private void WriteExStyle(nint window, long style)
    {
        EnsureResponsive(window);
        _changed[window].Identity.Require();
        nint previous = User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, (nint)style);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("change"), window);

        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED;
        _changed[window].Identity.Require();
        if (!User32.SetWindowPos(window, 0, 0, 0, 0, 0, flags | User32.SWP_NOZORDER))
            throw Failure(T("refresh the frame of"), window);
    }

    private static WindowOperationException Failure(string action, nint window)
    {
        string reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return new WindowOperationException(string.Format(T("Couldn't {0} \"{1}\": {2}"), T(action),
            WindowInfo.Title(window), reason));
    }

    private static void EnsureResponsive(nint window)
    {
        if (User32.SendMessageTimeout(window, 0, 0, 0, 0x3, ResponsiveMilliseconds, out _) == 0)
            throw new WindowOperationException(T("The window isn't responding right now. Try again in a moment."));
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
            : T("Unavailable");
        string path = ExecutablePath(window);
        Screen screen = Screen.FromHandle(window);
        return
        [
            (T("Title"), Title(window)),
            (T("Program"), ProcessName(processId)),
            (T("Program file"), path.Length > 0 ? path : T("Unavailable")),
            ("HWND", $"0x{window:X}"),
            (T("Process ID"), processId.ToString()),
            (T("Thread ID"), thread.ToString()),
            (T("Window class"), ClassName(window)),
            (T("Visible frame"), frame),
            (T("Display"), $"{screen.DeviceName} ({screen.Bounds.Width}×{screen.Bounds.Height})"),
            (T("Scaling"), $"{Dpi.Of(window) * 100 / Dpi.Default}%"),
            (T("Runs as administrator"), ProcessInfo.IsElevated(processId) ? T("Yes") : T("No")),
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
