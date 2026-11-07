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
    private const int PinAttempts = 10;
    private const int PinRetryMilliseconds = 20;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private sealed class ChangedWindow
    {
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
        public WindowPlacement? PlacementBeforePeek { get; set; }
    }

    private HashSet<string> _excluded = new(excludedProcesses, StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ChangedWindow> _changed = [];
    private readonly Lock _gate = new();

    /// <summary>The level the default see-through command and click-through use.</summary>
    public byte DefaultOpacity { get; set; } = ClickThroughOpacity;

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
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Pinned;
    }

    public bool IsGlass(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.GlassColor is not null;
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
            if (state.GlassColor is null)
            {
                if (!User32.GetWindowRect(window, out Rect r))
                    throw Failure(T("read the position of"), window);
                state.GlassColor = ScreenColors.MostCommon(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            }
            else
            {
                state.GlassColor = null;
            }

            if (!state.Layered)
            {
                WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_LAYERED);
                state.Layered = true;
            }

            WriteLayered(window, state, state.Alpha);
            SaveJournal();
            return state.GlassColor is not null;
        }
    }

    public bool IsClickThrough(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.ClickThrough;
    }

    public bool IsCropped(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Cropped;
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
                throw new WindowOperationException(string.Format(T("\"{0}\" draws its own shape, so it can't be cropped."), WindowInfo.Title(window)));

            nint region = User32.CreateRectRgn(part.Left, part.Top, part.Right, part.Bottom);
            if (User32.SetWindowRgn(window, region, true) == 0)
            {
                User32.DeleteObject(region);
                throw Failure(T("crop"), window);
            }

            state.Cropped = true;
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
        if (User32.SetWindowRgn(window, 0, true) == 0)
            throw Failure(T("uncrop"), window);
        state.Cropped = false;
    }

    /// <summary>The windows that are pinned right now and can be seen, for the highlight.</summary>
    public nint[] VisiblePinnedWindows()
    {
        lock (_gate)
        {
            return _changed
                .Where(pair => pair.Value is { Pinned: true, PinPaused: false, Hidden: false, PlacementBeforePeek: null } && User32.IsWindow(pair.Key))
                .Select(pair => pair.Key)
                .ToArray();
        }
    }

    /// <summary>Applies the options that depend on where the mouse is and what the windows are doing.</summary>
    public void Refresh(Point cursor, bool opaqueOnHover, bool pausePinWhenMaximized)
    {
        lock (_gate)
        {
            foreach ((nint window, ChangedWindow state) in _changed)
            {
                if (!User32.IsWindow(window) || state.Hidden || state.PlacementBeforePeek is not null)
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

    private static void FollowHover(nint window, ChangedWindow state, Point cursor, bool opaqueOnHover)
    {
        if (!state.Layered || state.Alpha == 255 || !User32.GetWindowRect(window, out Rect r))
            return;

        bool solid = opaqueOnHover && Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(cursor);
        if (solid != state.SolidWhileHovered)
        {
            state.SolidWhileHovered = solid;
            WriteLayered(window, state, solid ? (byte)255 : state.Alpha);
        }
    }

    private static void FollowMaximized(nint window, ChangedWindow state, bool pausePinWhenMaximized)
    {
        if (!state.Pinned)
            return;

        bool pause = pausePinWhenMaximized && User32.IsZoomed(window);
        if (pause && !state.PinPaused)
        {
            SetTopmost(window, false);
            state.PinPaused = true;
        }
        else if (!pause && state.PinPaused && User32.GetForegroundWindow() == window)
        {
            SetTopmost(window, true);
            state.PinPaused = false;
        }
    }

    /// <summary>
    /// True when WinScreen may change this window: not the desktop, the taskbar, itself or an excluded
    /// program, and one of the included programs when any are listed.
    /// </summary>
    public bool CanControl(nint window)
    {
        if (window == 0 || !User32.IsWindow(window))
            return false;
        if (window == User32.GetShellWindow() || window == User32.GetDesktopWindow())
            return false;
        if (ShellClasses.Contains(WindowInfo.ClassName(window)))
            return false;

        User32.GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId)
            return false;

        string program = WindowInfo.ProcessName(processId);
        return !_excluded.Contains(program) && (_included.Count == 0 || _included.Contains(program));
    }

    /// <summary>Pins the window on top, or unpins it. Returns the new state.</summary>
    public bool TogglePin(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            bool pin = !state.Pinned;
            if (pin)
                Pin(window);
            else
                SetTopmost(window, false);
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
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Layered ? state.Alpha : (byte)255;
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
            if (!state.Layered)
                ApplyAlpha(window, state, DefaultOpacity);

            bool on = !state.ClickThrough;
            long style = ReadExStyle(window);
            WriteExStyle(window, on ? style | User32.WS_EX_TRANSPARENT : style & ~User32.WS_EX_TRANSPARENT);
            state.ClickThrough = on;
            SaveJournal();
            return on;
        }
    }

    public bool IsHidden(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Hidden;
    }

    public bool IsPeeking(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.PlacementBeforePeek is not null;
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

            state.AlphaBeforeHiding = state.Layered ? state.Alpha : (byte)255;
            state.Hidden = true;
            SaveJournal();
            Conceal(window, state);
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
                SaveJournal();
                if (state.Hidden)
                    Reveal(window, state);

                if (maximized)
                    User32.SetForegroundWindow(window);
                else
                {
                    Move(window, position);
                    Pin(window);
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
            int failed = 0;
            foreach ((nint window, ChangedWindow state) in _changed)
            {
                if (!User32.IsWindow(window))
                    continue;
                try
                {
                    Restore(window, state);
                }
                catch (WindowOperationException)
                {
                    failed++;
                }
            }

            _changed.Clear();
            SaveJournal();
            return failed;
        }
    }

    /// <summary>Puts back windows left changed by a copy of WinScreen that was killed or crashed.</summary>
    public void RecoverFromJournal()
    {
        if (journal is null)
            return;

        foreach (WindowRecord record in journal.Load())
        {
            User32.GetWindowThreadProcessId((nint)record.Window, out uint owner);
            if (!User32.IsWindow((nint)record.Window) || owner != record.ProcessId)
                continue;

            try
            {
                var state = new ChangedWindow
                {
                    OriginalExStyle = record.ExStyle,
                    HadLayeredAttributes = record.HadLayeredAttributes,
                    OriginalColorKey = record.ColorKey,
                    OriginalAlpha = record.Alpha,
                    OriginalLayeredFlags = record.LayeredFlags,
                    Pinned = true,
                    Cropped = record.Cropped,
                    PlacementBeforePeek = record.Placement is null ? null : ToPlacement(record.Placement),
                };
                Restore((nint)record.Window, state);
            }
            catch (WindowOperationException)
            {
                // The window refused; it stays as it is and nothing more can be done for it.
            }
        }

        journal.Save([]);
    }

    private void SaveJournal()
    {
        journal?.Save(_changed.Select(pair => ToRecord(pair.Key, pair.Value)));
    }

    private static WindowRecord ToRecord(nint window, ChangedWindow state)
    {
        User32.GetWindowThreadProcessId(window, out uint owner);
        return new WindowRecord(window, owner, state.OriginalExStyle, state.HadLayeredAttributes, state.OriginalColorKey,
            state.OriginalAlpha, state.OriginalLayeredFlags, state.PlacementBeforePeek is { } p ? FromPlacement(p) : null, state.Cropped);
    }

    private static int[] FromPlacement(WindowPlacement p) =>
    [
        p.Flags, p.ShowCommand, p.MinimizedPosition.X, p.MinimizedPosition.Y, p.MaximizedPosition.X, p.MaximizedPosition.Y,
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

    private static void FinishPeek(nint window, ChangedWindow state)
    {
        if (state.PlacementBeforePeek is not { } placement)
            return;

        state.PlacementBeforePeek = null;
        if (!state.Pinned)
            SetTopmost(window, false);

        WritePlacement(window, placement);
        if (state.Hidden)
            Conceal(window, state);
    }

    private static void Conceal(nint window, ChangedWindow state)
    {
        ApplyAlpha(window, state, HiddenOpacity);
        WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_TRANSPARENT);
    }

    private static void Reveal(nint window, ChangedWindow state)
    {
        ApplyAlpha(window, state, state.AlphaBeforeHiding);
        if (!state.ClickThrough)
            WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
    }

    private static void Move(nint window, Point position)
    {
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

    private static void WritePlacement(nint window, WindowPlacement placement)
    {
        const int showNormal = 1;
        if (placement.ShowCommand == showNormal)
            placement.ShowCommand = User32.SW_SHOWNOACTIVATE;
        if (!User32.SetWindowPlacement(window, ref placement))
            throw Failure(T("put back"), window);
    }

    private static void Restore(nint window, ChangedWindow state)
    {
        state.Hidden = false;
        FinishPeek(window, state);

        if (state.Cropped)
            ClearCrop(window, state);

        if (state.HadLayeredAttributes
            && !User32.SetLayeredWindowAttributes(window, state.OriginalColorKey, state.OriginalAlpha, state.OriginalLayeredFlags))
        {
            throw Failure(T("restore the transparency of"), window);
        }

        // WS_EX_TOPMOST can only be changed with SetWindowPos, so keep the bit as it is for now.
        long current = ReadExStyle(window);
        WriteExStyle(window, (state.OriginalExStyle & ~User32.WS_EX_TOPMOST) | (current & User32.WS_EX_TOPMOST));

        bool wasTopmost = (state.OriginalExStyle & User32.WS_EX_TOPMOST) != 0;
        if (wasTopmost && !IsTopmost(window))
            Pin(window);
        else if (!wasTopmost && IsTopmost(window))
            SetTopmost(window, false);
    }

    private ChangedWindow Track(nint window)
    {
        if (!CanControl(window))
            throw new WindowOperationException(T("WinScreen doesn't change this window."));

        if (_changed.TryGetValue(window, out ChangedWindow? existing))
            return existing;

        long style = ReadExStyle(window);
        bool layered = (style & User32.WS_EX_LAYERED) != 0;
        uint key = 0, flags = 0;
        byte alpha = 255;
        bool hadAttributes = layered && User32.GetLayeredWindowAttributes(window, out key, out alpha, out flags);

        var state = new ChangedWindow
        {
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

    private static void ApplyAlpha(nint window, ChangedWindow state, byte alpha)
    {
        if (!state.Layered)
        {
            WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_LAYERED);
            state.Layered = true;
        }

        WriteLayered(window, state, alpha);
        state.Alpha = alpha;
    }

    private static void WriteLayered(nint window, ChangedWindow state, byte alpha)
    {
        uint flags = User32.LWA_ALPHA;
        uint key = 0;
        if (state.GlassColor is { } color)
        {
            flags |= User32.LWA_COLORKEY;
            key = (uint)(color.R | color.G << 8 | color.B << 16);
        }

        if (!User32.SetLayeredWindowAttributes(window, key, alpha, flags))
            throw Failure("change the transparency of", window);
    }

    /// <summary>
    /// Windows can report success for HWND_TOPMOST on a background window and ignore it (seen on
    /// Windows 11 26H2), so the window is brought forward first and the result is read back.
    /// </summary>
    public static void Pin(nint window)
    {
        for (int attempt = 0; attempt < PinAttempts; attempt++)
        {
            if (User32.GetForegroundWindow() != window)
                User32.SetForegroundWindow(window);

            if (User32.GetForegroundWindow() == window)
            {
                SetTopmost(window, true);
                return;
            }

            Thread.Sleep(PinRetryMilliseconds);
        }

        throw new WindowOperationException(string.Format(T("Windows wouldn't bring \"{0}\" forward to pin it. Click the window and try the pin command again."), WindowInfo.Title(window)));
    }

    private static bool IsTopmost(nint window) => (ReadExStyle(window) & User32.WS_EX_TOPMOST) != 0;

    private static void SetTopmost(nint window, bool topmost)
    {
        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
        nint after = topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
        if (!User32.SetWindowPos(window, after, 0, 0, 0, 0, flags))
            throw Failure(topmost ? T("pin") : T("unpin"), window);
        if (IsTopmost(window) != topmost)
            throw new WindowOperationException(string.Format(T("Windows ignored the request to {0} \"{1}\"."), topmost ? T("pin") : T("unpin"), WindowInfo.Title(window)));
    }

    private static long ReadExStyle(nint window)
    {
        nint style = User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("read"), window);
        return style;
    }

    private static void WriteExStyle(nint window, long style)
    {
        nint previous = User32.SetWindowLongPtr(window, User32.GWL_EXSTYLE, (nint)style);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            throw Failure(T("change"), window);

        const uint flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED;
        User32.SetWindowPos(window, 0, 0, 0, 0, 0, flags | User32.SWP_NOZORDER);
    }

    private static WindowOperationException Failure(string action, nint window)
    {
        string reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return new WindowOperationException(string.Format(T("Couldn't {0} \"{1}\": {2}"), T(action), WindowInfo.Title(window), reason));
    }
}

/// <summary>Read-only facts about a window.</summary>
public static class WindowInfo
{
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

    /// <summary>
    /// Where a window can really be seen on screen. GetWindowRect also counts the invisible resize
    /// margins of a normal window, and knows nothing of a crop.
    /// </summary>
    public static bool TryGetVisibleBounds(nint window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!User32.GetWindowRect(window, out Rect r))
            return false;

        var whole = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        bounds = Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, out Rect frame, Marshal.SizeOf<Rect>()) == 0
            ? Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom)
            : whole;

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
        catch (ArgumentException)
        {
            return "";
        }
    }
}
