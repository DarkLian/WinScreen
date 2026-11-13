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
                try { Unpark(window); } catch (WindowOperationException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// After the displays change: a monitor that went away takes its edge with it, and Windows moves windows from it
    /// onto the screens that are left, where a parked original would sit invisible but in the way. Put it past an
    /// outer edge of the new layout. Its recorded placement is unchanged.
    /// </summary>
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
            Conceal(window, state);
            FinishPeek(window, state);
            SaveJournal();
        }
    }

    /// <summary>
    /// While a view is used directly, the parked original is at opacity 1 and can be hit, so apps that check what
    /// is at a point, as browsers do before they scroll, find themselves there. Only its two-pixel strip is on
    /// screen. Recovery puts back the journaled style and opacity as for any hidden window.
    /// </summary>
    /// <remarks>
    /// An app can bring itself back over the screen while parked: a browser showing a page in full screen covers its
    /// monitor and moves straight back there when moved. At opacity 1 it would show as a faint layer over everything
    /// while the pointer is on the view, so it stays fully see-through until it is off the screen again
    /// (<see cref="FollowParkedPlace"/>). Clicks and keys are sent to it all the same.
    /// </remarks>
    internal void SetParkedReachable(nint window, bool reachable)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            state.ReachableWanted = reachable;
            ApplyReachable(window, state, reachable && OffScreen(window));
        }
    }

    /// <summary>
    /// Checked often while a view shows a parked original. A browser leaving full screen goes back to where it was
    /// before and puts back the styles it had then, which can leave it on the screen and fully visible behind the view.
    /// So: a dropped see-through style is put back, a window back on the screen without covering a monitor is parked
    /// again (one in full screen would only move straight back), and it is reachable only while off the screen.
    /// Nothing is written unless something changed.
    /// </summary>
    /// <summary>What <see cref="FollowParkedPlace"/> did, for tests: the step, bounds and extended style at the time.</summary>
    internal System.Collections.Concurrent.ConcurrentQueue<string> ParkedPlaceSteps { get; } = new();

    private void Step(nint window, string what)
    {
        if (ParkedPlaceSteps.Count > 200) return;
        User32.GetWindowRect(window, out Rect b);
        ParkedPlaceSteps.Enqueue($"{Environment.TickCount64 % 100000} {what} at {b.Left},{b.Top},{b.Right - b.Left}x{b.Bottom - b.Top} ex 0x{ReadExStyle(window):X}");
    }

    internal void FollowParkedPlace(nint window)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
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

    private static bool OffScreen(nint window) =>
        User32.GetWindowRect(window, out Rect bounds) &&
        OutOfReach(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), [.. Screen.AllScreens.Select(s => s.Bounds)]);

    internal bool IsParkedReachable(nint window) =>
        IsParked(window) && (ReadExStyle(window) & User32.WS_EX_TRANSPARENT) == 0;

    private const byte ReachableOpacity = 1;
    private const int LooseSettleMilliseconds = 400;

    /// <summary>For native hover, keep the two-pixel strip clear of the usual bottom taskbar. A browser that finds
    /// its strip covered marks the page hidden and discards moves even while its leave messages are filtered, and
    /// stays that way until it is activated. Asked again while already there, it only brings the strip forward.</summary>
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

    /// <param name="home">
    /// The monitor the window is on. Its own outer edges come first: on another monitor with a different scale the app
    /// would be told its DPI changed and lay itself out again, twice, while hidden.
    /// </param>
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

    /// <summary>Bottom, right, left, top: the home monitor's edges first, then every monitor's, as before.</summary>
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
