using WinScreen.Native;
using static WinScreen.Localization.UiText;

namespace WinScreen.Windows;

public sealed partial class WindowController
{
    internal bool IsParked(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.Parked && state.Identity.Matches();
    }

    internal void Park(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.Parked) return;
            StopWatchingReturn(window); // A new view parks it again; this one's own close decides.
            if (!state.Hidden || state.PlacementBeforePeek is not null)
                throw new WindowOperationException(T("errors.pip.previewNotReady"));
            if (!User32.GetWindowRect(window, out Rect bounds)) throw Failure(T("actions.readPosition"), window);
            Rectangle original = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            Rectangle parked = ParkingPlace(original.Size, Screen.AllScreens.Select(screen => screen.Bounds).ToArray(),
                Screen.FromRectangle(original).Bounds);
            state.PlacementBeforePeek = ReadPlacement(window);
            state.Parked = true;
            state.CoveredAMonitor = false;
            state.LooseSince = 0;
            Prepare(window, state, User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT);
            Conceal(window, state);
            try
            {
                if (User32.IsZoomed(window))
                {
                    WindowPlacement normal = state.PlacementBeforePeek.Value;
                    normal.ShowCommand = User32.SW_SHOWNOACTIVATE;
                    normal.NormalPosition = bounds;
                    WritePlacement(window, normal);
                }
                state.Identity.Require();
                if (!User32.SetWindowPos(window, 0, parked.Left, parked.Top, parked.Width, parked.Height,
                    User32.SWP_NOACTIVATE | User32.SWP_NOZORDER)) throw Failure(T("actions.move"), window);
            }
            catch
            {
                try { Unpark(window); } catch (WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
                throw;
            }
        }
    }

    // Windows moves windows from a monitor that went away onto the screens left, where a parked original would sit
    // invisible but in the way. Its recorded placement is unchanged.
    internal void Repark(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            if (!User32.GetWindowRect(window, out Rect bounds)) throw Failure(T("actions.readPosition"), window);
            Rectangle now = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            Rectangle[] monitors = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
            Rect home = state.PlacementBeforePeek?.NormalPosition ?? bounds;
            Rectangle homeMonitor = Screen.FromRectangle(Rectangle.FromLTRB(home.Left, home.Top, home.Right, home.Bottom)).Bounds;
            Rectangle parked = state.HoverParking ? HoverParkingPlace(now.Size, monitors, homeMonitor) : ParkingPlace(now.Size, monitors, homeMonitor);
            if (parked == now) return;
            if (!User32.SetWindowPos(window, 0, parked.Left, parked.Top, parked.Width, parked.Height,
                    User32.SWP_NOACTIVATE | User32.SWP_NOZORDER)) throw Failure(T("actions.move"), window);
        }
    }

    internal void Unpark(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked) return;
            state.Identity.Require();
            state.ShownForDrop = false;
            state.RevealHeld = false;
            Conceal(window, state);
            FinishPeek(window, state);
            SaveJournal();
        }
    }

    // Above everything and taking the pointer, without activating it: a drop can only go to the real window under the
    // pointer. Its home and record stay, so recovery and closing the view put it back.
    // holdReveal: kept nearly see-through (still taking the pointer) until RevealShownForDrop; once held, later calls
    // keep it so until then.
    internal void ShowParkedForDrop(nint window, Point topLeft, Size? size = null, bool holdReveal = false)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            if (size is not null && state.SizeBeforeDrop is null && User32.GetWindowRect(window, out Rect own))
                state.SizeBeforeDrop = new Size(own.Right - own.Left, own.Bottom - own.Top);
            Size wanted = size ?? state.SizeBeforeDrop ?? Size.Empty;
            if (size is null) state.SizeBeforeDrop = null;
            state.ShownForDrop = true;
            state.LooseSince = 0;
            if (!User32.SetWindowPos(window, User32.HWND_TOPMOST, topLeft.X, topLeft.Y, wanted.Width, wanted.Height,
                    (wanted.IsEmpty ? User32.SWP_NOSIZE : 0) | User32.SWP_NOACTIVATE)) throw Failure(T("actions.move"), window);
            state.RevealHeld |= holdReveal;
            ApplyAlpha(window, state, state.RevealHeld ? ReachableOpacity : state.AlphaBeforeHiding);
            WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
        }
    }

    internal void RevealShownForDrop(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.ShownForDrop || !state.RevealHeld) return;
            state.RevealHeld = false;
            if (!state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            ApplyAlpha(window, state, state.AlphaBeforeHiding);
        }
    }

    internal bool RevealIsHeld(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.ShownForDrop && state.RevealHeld;
    }

    // For one that was maximized, the size it goes back to (its parked size is the whole screen's).
    internal Size? NormalSize(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !User32.IsWindow(window)) return null;
            if (state.PlacementBeforePeek is { ShowCommand: ShowMaximized } home)
                return new Size(home.NormalPosition.Right - home.NormalPosition.Left, home.NormalPosition.Bottom - home.NormalPosition.Top);
            if (state.SizeBeforeDrop is { } own) return own;
            return User32.GetWindowRect(window, out Rect r) ? new Size(r.Right - r.Left, r.Bottom - r.Top) : null;
        }
    }

    internal bool IsShownForDrop(nint window)
    {
        lock (_gate) return _changed.TryGetValue(window, out var state) && state.ShownForDrop;
    }

    internal void ReturnToParking(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.ShownForDrop) return;
            state.ShownForDrop = false;
            state.RevealHeld = false;
            if (!User32.IsWindow(window) || !state.Parked) return;
            state.Identity.Require();
            // Hidden first: given its own size back while still on the screen, it showed at that size for a moment.
            Conceal(window, state);
            if (state.SizeBeforeDrop is { } own)
            {
                state.SizeBeforeDrop = null;
                User32.SetWindowPos(window, 0, 0, 0, own.Width, own.Height, User32.SWP_NOMOVE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            }
            Repark(window);
            if (state.HoverParking) SetParkedHoverSurface(window, true);
        }
    }

    // While a view is used directly the parked original is at opacity 1 and can be hit, so apps that check what is at a
    // point (browsers, before they scroll) find themselves there.
    // A browser in full screen moves straight back over its monitor while parked; at opacity 1 it would show as a faint
    // layer, so it stays fully see-through until it is off the screen again.
    internal void SetParkedReachable(nint window, bool reachable)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            state.ReachableWanted = reachable;
            if (state.ShownForDrop) return; // Its own look while on the screen; parked again, it follows this.
            ApplyReachable(window, state, reachable && OffScreen(window));
        }
    }

    internal System.Collections.Concurrent.ConcurrentQueue<string>? ParkedPlaceSteps { get; set; }

    private void Step(nint window, string what)
    {
        if (ParkedPlaceSteps is not { Count: <= 200 }) return;
        User32.GetWindowRect(window, out Rect b);
        ParkedPlaceSteps.Enqueue($"{Environment.TickCount64 % 100000} {what} at {b.Left},{b.Top},{b.Right - b.Left}x{b.Bottom - b.Top} ex 0x{ReadExStyle(window):X}");
    }

    // A browser leaving full screen goes back where it was and restores its old styles, which can leave it on the
    // screen and visible behind the view. One in full screen would only move straight back, so only a window not
    // covering a monitor is parked again. Nothing is written unless something changed.
    internal void FollowParkedPlace(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || state.ShownForDrop || !User32.IsWindow(window)) return;
            long style = ReadExStyle(window);
            if (state.Layered && (style & User32.WS_EX_LAYERED) == 0)
            {
                Step(window, "layered style dropped; put back");
                state.Identity.Require();
                WriteExStyle(window, style | User32.WS_EX_LAYERED);
                WriteLayered(window, state, state.Alpha);
                style = ReadExStyle(window);
            }
            Rectangle[] monitors = [.. Screen.AllScreens.Select(s => s.Bounds)];
            bool known = User32.GetWindowRect(window, out Rect r);
            Rectangle now = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            bool covers = known && FillsAMonitor(now, monitors);
            if (covers) state.CoveredAMonitor = true;
            bool loose = known && !covers && !OutOfReach(now, monitors);
            // Going into or out of full screen passes through other sizes on the screen: moving it then upsets the
            // browser, so it is parked again only once it has stayed there a moment.
            if (!loose) state.LooseSince = 0;
            else if (state.LooseSince == 0) state.LooseSince = Environment.TickCount64;
            else if (Environment.TickCount64 - state.LooseSince >= LooseSettleMilliseconds)
            {
                // Where the app put itself (a browser leaving full screen goes back to where it was before) is where
                // it goes when the view closes, not the full-screen spot it had when it was parked.
                state.LooseSince = 0;
                state.Identity.Require();
                if (state.CoveredAMonitor)
                {
                    // Back from full screen: where the app put itself is its home now. Anything else on the screen
                    // keeps the home it had when parked.
                    Step(window, "back from full screen; new home, parked again");
                    WindowPlacement home = ReadPlacement(window);
                    state.PlacementBeforePeek = home;
                    state.CoveredAMonitor = false;
                    SaveJournal();
                    if (User32.IsZoomed(window))
                    {
                        WindowPlacement normal = home;
                        normal.ShowCommand = User32.SW_SHOWNOACTIVATE;
                        normal.NormalPosition = r;
                        WritePlacement(window, normal);
                    }
                }
                else Step(window, "on screen; parked again");
                Repark(window);
            }
            if (Environment.TickCount64 - state.SiblingsCheckedAt >= SiblingsCheckMilliseconds)
            {
                state.SiblingsCheckedAt = Environment.TickCount64;
                if (known) KeepSiblingsOffParking(window, state, now);
            }
            bool reachable = state.ReachableWanted && OffScreen(window);
            if (reachable == ((style & User32.WS_EX_TRANSPARENT) == 0)) return;
            state.Identity.Require();
            Step(window, reachable ? "reachable" : "see-through");
            ApplyReachable(window, state, reachable);
        }
    }

    private void ApplyReachable(nint window, ChangedWindow state, bool reachable)
    {
        long style = ReadExStyle(window);
        if (!reachable) WriteExStyle(window, style | User32.WS_EX_TRANSPARENT); // Clicks go first, then the window fades.
        ApplyAlpha(window, state, reachable ? ReachableOpacity : HiddenOpacity);
        if (reachable) WriteExStyle(window, style & ~User32.WS_EX_TRANSPARENT);
    }

    private const int SiblingsCheckMilliseconds = 300;
    private const int SW_SHOWMINNOACTIVE = 7;

    // An app opens a new window where its last one was, so a second Chrome window took the parked original's place, off
    // the screen. The app's other windows mostly on the parked place go where the app would have put them without
    // WinScreen: at the original's home, with the same offset.
    private void KeepSiblingsOffParking(nint window, ChangedWindow state, Rectangle parked)
    {
        if (state.PlacementBeforePeek is not { } home) return;
        User32.GetWindowThreadProcessId(window, out uint process);
        if (process == 0) return;
        Rect h = home.NormalPosition;
        Rectangle homeBounds = Rectangle.FromLTRB(h.Left, h.Top, h.Right, h.Bottom);
        Rectangle[] areas = [.. Screen.AllScreens.Select(s => s.WorkingArea)];
        Rectangle[] monitors = [.. Screen.AllScreens.Select(s => s.Bounds)];
        const uint GW_CHILD = 5, GW_HWNDNEXT = 2, GW_OWNER = 4;
        const long WS_CAPTION = 0x00C00000;
        nint other = User32.GetWindow(User32.GetDesktopWindow(), GW_CHILD);
        for (int seen = 0; other != 0 && seen < 4096; seen++, other = User32.GetWindow(other, GW_HWNDNEXT))
        {
            if (other == window || !User32.IsWindowVisible(other) || User32.GetWindow(other, GW_OWNER) != 0) continue;
            if (User32.GetWindowThreadProcessId(other, out uint owner) == 0 || owner != process) continue;
            if ((User32.GetWindowLongPtr(other, User32.GWL_STYLE) & WS_CAPTION) != WS_CAPTION) continue;
            if (_changed.TryGetValue(other, out ChangedWindow? tracked) && (tracked.Parked || tracked.Hidden)) continue;
            var placement = new WindowPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<WindowPlacement>() };
            if (!User32.GetWindowPlacement(other, ref placement)) continue;
            Rect n = placement.NormalPosition;
            Rectangle normal = Rectangle.FromLTRB(n.Left, n.Top, n.Right, n.Bottom);
            if (SiblingHome(normal, parked, homeBounds, monitors, areas) is not { } moved) continue;
            bool zoomed = User32.IsZoomed(other), minimized = User32.IsIconic(other);
            // Writing a maximized placement shows it maximized, which activates it: only while it has the focus anyway.
            if (zoomed && User32.GetForegroundWindow() != other) continue;
            if (!AnswersQuickly(other)) continue;
            Diagnostics.Log.Event("window.siblingOffParking", ("program", Diagnostics.LogValue.ProgramOf(other)));
            if (!zoomed && !minimized)
            {
                User32.SetWindowPos(other, 0, moved.Left, moved.Top, moved.Width, moved.Height, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                continue;
            }
            placement.NormalPosition = new Rect { Left = moved.Left, Top = moved.Top, Right = moved.Right, Bottom = moved.Bottom };
            placement.ShowCommand = zoomed ? User32.SW_MAXIMIZE : SW_SHOWMINNOACTIVE;
            // Not a window WinScreen changed: written directly, and a failure only leaves it where the app put it.
            User32.SetWindowPlacement(other, ref placement);
        }
    }

    internal static Rectangle? SiblingHome(Rectangle normal, Rectangle parked, Rectangle home,
        IReadOnlyList<Rectangle> monitors, IReadOnlyList<Rectangle> workAreas)
    {
        long Area(Rectangle r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;
        long size = Area(normal);
        if (size == 0) return null;
        if (Area(Rectangle.Intersect(normal, parked)) * 2 < size) return null;
        long shown = monitors.Sum(m => Area(Rectangle.Intersect(normal, m)));
        if (shown * 2 >= size) return null;
        var moved = new Rectangle(home.Left + normal.Left - parked.Left, home.Top + normal.Top - parked.Top, normal.Width, normal.Height);
        Rectangle area = workAreas.OrderByDescending(a => Area(Rectangle.Intersect(a, home))).FirstOrDefault(home);
        int width = Math.Min(moved.Width, area.Width), height = Math.Min(moved.Height, area.Height);
        int left = Math.Clamp(moved.Left, area.Left, area.Right - width), top = Math.Clamp(moved.Top, area.Top, area.Bottom - height);
        return new Rectangle(left, top, width, height);
    }

    private static bool OffScreen(nint window) =>
        User32.GetWindowRect(window, out Rect bounds) &&
        OutOfReach(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), [.. Screen.AllScreens.Select(s => s.Bounds)]);

    internal bool IsParkedReachable(nint window) =>
        IsParked(window) && (ReadExStyle(window) & User32.WS_EX_TRANSPARENT) == 0;

    private const byte ReachableOpacity = 1;
    private const int LooseSettleMilliseconds = 400;

    // A browser that finds its strip covered by the taskbar marks the page hidden and discards moves, even with its
    // leave messages filtered, until it is activated.
    internal void SetParkedHoverSurface(nint window, bool enabled)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            if (!enabled && !state.HoverParking) return;
            state.Identity.Require();
            bool previous = state.HoverParking;
            state.HoverParking = enabled;
            try
            {
                if (previous != enabled) Repark(window);
                if (enabled && !User32.SetWindowPos(window, 0, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE)) throw Failure(T("actions.move"), window);
            }
            catch { state.HoverParking = previous; throw; }
        }
    }

    internal static Rectangle HoverParkingPlace(Size size, IReadOnlyList<Rectangle> monitors, Rectangle? home = null)
    {
        var ordered = home is { } own && monitors.Contains(own) ? new[] { own }.Concat(monitors.Where(m => m != own)) : monitors;
        foreach (Rectangle monitor in ordered)
        foreach (int edge in new[] { 1, 2, 3, 0 })
        {
            Point at = edge switch
            {
                1 => new Point(monitor.Right - 2, monitor.Top),
                2 => new Point(monitor.Left - size.Width + 2, monitor.Top),
                3 => new Point(monitor.Left, monitor.Top - size.Height + 2),
                _ => new Point(monitor.Left, monitor.Bottom - 2),
            };
            var candidate = new Rectangle(at, size);
            if (monitors.All(other =>
            {
                Rectangle overlap = Rectangle.Intersect(candidate, other);
                return overlap.IsEmpty || (edge is 0 or 3 ? overlap.Height <= 2 : overlap.Width <= 2);
            })) return candidate;
        }
        throw new WindowOperationException(T("errors.pip.noSurface"));
    }

    // The home monitor's outer edges come first: on a monitor at another scale the app would be told its DPI changed
    // and lay itself out again, twice, while hidden.
    internal static Rectangle ParkingPlace(Size size, IReadOnlyList<Rectangle> monitors, Rectangle? home = null)
    {
        // Try each external edge in preference order. Every visible intersection stays within its two-pixel strip.
        foreach (var (edge, monitor) in Candidates(monitors, home))
        {
            Point at = edge switch
            {
                0 => new Point(monitor.Left, monitor.Bottom - 2),
                1 => new Point(monitor.Right - 2, monitor.Top),
                2 => new Point(monitor.Left - size.Width + 2, monitor.Top),
                _ => new Point(monitor.Left, monitor.Top - size.Height + 2),
            };
            var candidate = new Rectangle(at, size);
            bool fits = monitors.All(other =>
            {
                Rectangle overlap = Rectangle.Intersect(candidate, other);
                return overlap.IsEmpty || (edge is 0 or 3 ? overlap.Height <= 2 : overlap.Width <= 2);
            });
            if (fits) return candidate;
        }
        throw new WindowOperationException(T("errors.pip.noSurface"));
    }

    private static IEnumerable<(int Edge, Rectangle Monitor)> Candidates(IReadOnlyList<Rectangle> monitors, Rectangle? home)
    {
        bool hasHome = home is { } own && monitors.Contains(own);
        if (hasHome)
            for (int edge = 0; edge < 4; edge++) yield return (edge, home!.Value);
        for (int edge = 0; edge < 4; edge++)
            foreach (Rectangle monitor in monitors)
                if (!hasHome || monitor != home) yield return (edge, monitor);
    }
}
