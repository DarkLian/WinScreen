using System.Runtime.InteropServices;
using WinScreen.Native;
using static WinScreen.Localization.UiText;

namespace WinScreen.Windows;

public sealed partial class WindowController
{
    internal const long BorderlessMask = User32.WS_CAPTION | User32.WS_THICKFRAME;
    internal const long SwitcherMask = User32.WS_EX_TOOLWINDOW | User32.WS_EX_APPWINDOW;

    private sealed record GestureState(nint Window, bool Hidden, bool Peeking);
    private volatile GestureState[] _gestureState = [];

    private void PublishGestureState() => _gestureState = _changed.Select(pair => new GestureState(pair.Key,
        pair.Value.Hidden, pair.Value.PlacementBeforePeek is not null)).ToArray();

    // The mouse hook must never wait for the controller's UI-thread lock.
    internal bool GestureBlocked(nint window) => _gestureState.Any(state =>
        state.Window == window && (state.Hidden || state.Peeking));

    internal bool IsBorderlessFilled(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Identity.Matches() &&
            state.PlacementBeforeBorderless is not null && state.BorderlessFilled;
    }

    public bool IsBorderless(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Identity.Matches() &&
                            state.PlacementBeforeBorderless is not null;
    }

    public bool IsOutOfSwitcher(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Identity.Matches() && state.SwitcherBits is not null;
    }

    public bool IsDarkTitleBar(nint window) =>
        Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, out int value, sizeof(int)) == 0 && value != 0;

    public bool ToggleDarkTitleBar(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.OriginalDarkTitleBar is not null)
            {
                Prepare(window, state);
                RestoreTitleBar(window, state);
                SaveJournal();
                return false;
            }
            if (!HasNativeTitleBar(window))
                throw new WindowOperationException(T("errors.window.nonstandardTitleBar"));
            if (Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, out int original, sizeof(int)) != 0)
                throw Failure(T("actions.readTitleBar"), window);
            state.OriginalDarkTitleBar = original == 0 ? 0 : 1;
            Prepare(window, state);
            WriteTitleBar(window, state, original == 0 ? 1 : 0);
            SaveJournal();
            return original == 0;
        }
    }

    private static void WriteTitleBar(nint window, ChangedWindow state, int value)
    {
        state.Identity.Require();
        if (Dwmapi.DwmSetWindowAttribute(window, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, in value, sizeof(int)) != 0 ||
            Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, out int read, sizeof(int)) != 0 || read != value)
            throw Failure(T("actions.changeTitleBar"), window);
    }

    private static void RestoreTitleBar(nint window, ChangedWindow state)
    {
        if (state.OriginalDarkTitleBar is not { } value) return;
        EnsureResponsive(window);
        WriteTitleBar(window, state, value);
        state.OriginalDarkTitleBar = null;
    }

    internal static bool NativeCaptionGeometry(int clientTopOffset, int captionHeight) => clientTopOffset >= captionHeight;

    internal static bool HasNativeTitleBar(nint window)
    {
        if ((User32.GetWindowLongPtr(window, User32.GWL_STYLE) & User32.WS_CAPTION) != User32.WS_CAPTION ||
            !User32.GetWindowRect(window, out Rect rect)) return false;
        // Minimized, a window has only a stub of a frame to measure, so its style is all there is to go by.
        if (User32.IsIconic(window)) return true;
        var point = new NativePoint();
        return User32.ClientToScreen(window, ref point) && NativeCaptionGeometry(point.Y - rect.Top,
            User32.GetSystemMetricsForDpi(CaptionMetric, User32.GetDpiForWindow(window)));
    }

    public bool ToggleBorderless(nint window, bool fill = false, Rectangle? display = null)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            // Undo is allowed even when a filled window triggers full-screen detection.
            if (state.PlacementBeforeBorderless is not null)
            {
                Prepare(window, state);
                RemoveBorderless(window, state);
                SaveJournal();
                return false;
            }
            if (FullScreen.Current() == FullScreenState.Exclusive)
                throw new WindowOperationException(T("errors.borderless.exclusive"));
            if (state.Hidden || state.PlacementBeforePeek is not null || state.Cropped ||
                state.PlacementBeforeRollUp is not null)
                throw new WindowOperationException(T("errors.window.showNormally"));
            EnsureResponsive(window);
            // A minimized window comes back first, as its own Restore would bring it back (maximized, if it was).
            if (User32.IsIconic(window))
            {
                state.Identity.Require();
                User32.ShowWindow(window, User32.SW_RESTORE);
            }
            if (User32.IsIconic(window) || (!fill && User32.IsZoomed(window)))
                throw new WindowOperationException(T("errors.window.showNormally"));
            // A window that draws its own title bar, as browsers and editors do, still has Windows' frame bits; taking
            // them away leaves its own drawing as it is. With neither there is nothing to take away, though it can
            // still be made to fill a display.
            bool nativeTitleBar = HasNativeTitleBar(window);
            if (!fill && ((long)User32.GetWindowLongPtr(window, User32.GWL_STYLE) & BorderlessMask) == 0)
                throw new WindowOperationException(T("errors.borderless.noFrame"));

            state.BorderlessBits = (long)User32.GetWindowLongPtr(window, User32.GWL_STYLE) & BorderlessMask;
            state.PlacementBeforeBorderless = ReadPlacement(window);
            state.BorderlessFilled = fill;
            bool prepared = false;
            try
            {
                Prepare(window, state);
                prepared = true;
                if (User32.IsZoomed(window))
                {
                    WindowPlacement normal = state.PlacementBeforeBorderless.Value;
                    normal.ShowCommand = User32.SW_SHOWNOACTIVATE;
                    normal.Flags = 0;
                    WritePlacement(window, normal);
                }
                if (!User32.GetClientRect(window, out Rect client)) throw Failure(T("actions.readPosition"), window);
                var origin = new NativePoint();
                if (!User32.ClientToScreen(window, ref origin)) throw Failure(T("actions.readPosition"), window);
                Rectangle bounds = display ?? Screen.FromHandle(window).Bounds;
                long style = (long)User32.GetWindowLongPtr(window, User32.GWL_STYLE) & ~BorderlessMask;
                var adjusted = new Rect(0, 0, client.Right, client.Bottom);
                if (!fill && !User32.AdjustWindowRectExForDpi(ref adjusted, (uint)style, User32.GetMenu(window) != 0,
                        (uint)ReadExStyle(window), User32.GetDpiForWindow(window)))
                    throw Failure(T("actions.readPosition"), window);
                // Windows' own title bar: the window keeps what is inside its frame where it is. A title bar it draws
                // itself: the window keeps the size and place it shows now.
                Rectangle target = fill ? bounds : nativeTitleBar ? new Rectangle(origin.X + adjusted.Left, origin.Y + adjusted.Top,
                    adjusted.Right - adjusted.Left, adjusted.Bottom - adjusted.Top)
                    : WindowInfo.TryGetFrameBounds(window, out Rectangle visible, out _) ? visible : throw Failure(T("actions.readPosition"), window);
                state.BorderlessOrigin = target.Location;
                SaveJournal(); // Includes the restoration origin before removing the frame.
                WriteStyle(window, state, style);
                state.Identity.Require();
                if (!User32.SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height,
                        User32.SWP_FRAMECHANGED | User32.SWP_NOACTIVATE | User32.SWP_NOZORDER) ||
                    ((long)User32.GetWindowLongPtr(window, User32.GWL_STYLE) & BorderlessMask) != 0)
                    throw new WindowOperationException(T("errors.borderless.frameRestored"));
                SaveJournal();
                return true;
            }
            catch (WindowOperationException)
            {
                if (prepared)
                {
                    try { RemoveBorderless(window, state); }
                    catch (WindowOperationException) { /* Keep the durable record for retry. */ }
                }
                else
                {
                    state.BorderlessBits = 0;
                    state.PlacementBeforeBorderless = null;
                    state.BorderlessFilled = false;
                    state.BorderlessOrigin = null;
                }
                TrySaveJournal();
                throw;
            }
        }
    }

    internal static Rect OffsetBorderlessPlacement(Rect saved, Point initial, Point current) =>
        new(saved.Left + current.X - initial.X, saved.Top + current.Y - initial.Y,
            saved.Right + current.X - initial.X, saved.Bottom + current.Y - initial.Y);

    private void RemoveBorderless(nint window, ChangedWindow state)
    {
        if (state.PlacementBeforeBorderless is not { } placement) return;
        EnsureResponsive(window);
        state.Identity.Require();
        if (!state.BorderlessFilled && state.BorderlessOrigin is { } origin && User32.GetWindowRect(window, out Rect current))
            placement.NormalPosition = OffsetBorderlessPlacement(placement.NormalPosition, origin, new Point(current.Left, current.Top));
        state.PlacementBeforeBorderless = placement;
        state.BorderlessOrigin = null;
        SaveJournal(); // Retrying a partly completed undo must not apply the movement twice.
        WriteStyle(window, state, ((long)User32.GetWindowLongPtr(window, User32.GWL_STYLE) & ~BorderlessMask) | state.BorderlessBits);
        state.Identity.Require();
        if (!User32.SetWindowPos(window, 0, 0, 0, 0, 0, User32.SWP_FRAMECHANGED | User32.SWP_NOMOVE |
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOZORDER)) throw Failure(T("actions.restorePosition"), window);
        WritePlacement(window, placement);
        state.BorderlessBits = 0;
        state.PlacementBeforeBorderless = null;
        state.BorderlessOrigin = null;
        state.BorderlessFilled = false;
    }

    private static void WriteStyle(nint window, ChangedWindow state, long style)
    {
        state.Identity.Require();
        Marshal.SetLastPInvokeError(0);
        User32.SetWindowLongPtr(window, User32.GWL_STYLE, (nint)style);
        if (Marshal.GetLastPInvokeError() != 0) throw Failure(T("actions.changeFrame"), window);
    }

    /// <summary>
    /// Store apps' frames: Windows keeps their taskbar button and Alt+Tab entry itself, so they can't leave the
    /// switcher, but they can still be hidden.
    /// </summary>
    internal static bool TaskbarButtonOwnedByWindows(nint window) => WindowInfo.ClassName(window) == "ApplicationFrameWindow";

    public void SetOutOfSwitcher(nint window, bool on)
    {
        lock (_gate)
        {
            if (!on)
            {
                if (!_changed.TryGetValue(window, out var existing) || existing.SwitcherBits is null) return;
                RestoreSwitcher(window, existing);
                SaveJournal();
                return;
            }
            ChangedWindow state = Track(window);
            if (state.SwitcherBits is not null) return;
            if (!state.Hidden || state.PlacementBeforePeek is not null && !state.Parked) // A parked window keeps its placement there too.
                throw new WindowOperationException(T("errors.switcher.hideOriginalFirst"));
            if (TaskbarButtonOwnedByWindows(window))
                throw new WindowOperationException(T("errors.switcher.windowsManaged"));
            state.SwitcherBits = ReadExStyle(window) & SwitcherMask;
            Prepare(window, state);
            state.Identity.Require();
            WriteExStyle(window, (ReadExStyle(window) & ~SwitcherMask) | User32.WS_EX_TOOLWINDOW);
            RefreshSwitcherFrame(window, state);
            if ((ReadExStyle(window) & SwitcherMask) != User32.WS_EX_TOOLWINDOW)
                throw Failure(T("actions.hideSwitcherEntry"), window);
            SaveJournal();
        }
    }

    /// <summary>
    /// Takes a hidden window off the shell's task list and changes nothing about the window, so it keeps its own
    /// caption buttons. The taskbar drops its button, and Windows 11 leaves it out of Alt+Tab as well.
    /// </summary>
    public void RemoveTaskbarButton(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.TaskbarButtonRemoved || state.SwitcherBits is not null) return;
            if (!state.Hidden || state.PlacementBeforePeek is not null && !state.Parked) // A parked window keeps its placement there too.
                throw new WindowOperationException(T("errors.switcher.hideOriginalFirst"));
            if (TaskbarButtonOwnedByWindows(window))
                throw new WindowOperationException(T("errors.switcher.windowsManaged"));
            state.TaskbarButtonRemoved = true;
            Prepare(window, state); // Recorded first, so recovery puts the button back.
            if (!TaskbarList.Remove(window))
            {
                state.TaskbarButtonRemoved = false;
                SaveJournal();
                throw Failure(T("actions.hideSwitcherEntry"), window);
            }
        }
    }

    /// <summary>
    /// Takes a window's button off the taskbar while it stays as it is, for a window reached from its Anchor. It is
    /// recorded first, so the button comes back after a crash too.
    /// </summary>
    public void TakeOffTaskbar(nint window)
    {
        lock (_gate)
        {
            if (TaskbarButtonOwnedByWindows(window)) // Checked first, so nothing is kept about a window left as it was.
                throw new WindowOperationException(T("errors.switcher.windowsManaged"));
            ChangedWindow state = Track(window);
            if (state.TaskbarButtonRemoved || state.SwitcherBits is not null) return;
            state.TaskbarButtonRemoved = true;
            Prepare(window, state);
            SaveJournal(); // Written before the button goes, so a crash right after still brings it back.
            if (!TaskbarList.Remove(window))
            {
                state.TaskbarButtonRemoved = false;
                SaveJournal();
                throw Failure(T("actions.hideSwitcherEntry"), window);
            }
        }
    }

    /// <summary>Puts a window's taskbar button back, unless a view is keeping the window hidden: the view does it then.</summary>
    public void PutBackOnTaskbar(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out ChangedWindow? state) || !state.TaskbarButtonRemoved || state.Hidden ||
                !state.Identity.Matches()) return;
            RestoreTaskbarButton(window, state);
            SaveJournal();
        }
    }

    public bool IsOffTaskbar(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Identity.Matches() && state.TaskbarButtonRemoved;
    }

    private static void RestoreTaskbarButton(nint window, ChangedWindow state)
    {
        if (!state.TaskbarButtonRemoved) return;
        state.Identity.Require();
        if (!TaskbarList.Add(window)) throw Failure(T("actions.restorePosition"), window);
        state.TaskbarButtonRemoved = false;
    }

    private void RestoreSwitcher(nint window, ChangedWindow state)
    {
        if (state.SwitcherBits is not { } original) return;
        EnsureResponsive(window);
        state.Identity.Require();
        WriteExStyle(window, (ReadExStyle(window) & ~SwitcherMask) | original);
        RefreshSwitcherFrame(window, state);
        if ((ReadExStyle(window) & SwitcherMask) != original) throw Failure(T("actions.restorePosition"), window);
        state.SwitcherBits = null;
    }

    private static void RefreshSwitcherFrame(nint window, ChangedWindow state)
    {
        state.Identity.Require();
        if (!User32.SetWindowPos(window, 0, 0, 0, 0, 0, User32.SWP_FRAMECHANGED | User32.SWP_NOMOVE |
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOZORDER)) throw Failure(T("actions.restorePosition"), window);
    }
}
