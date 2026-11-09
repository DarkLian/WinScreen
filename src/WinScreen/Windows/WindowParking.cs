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
            if (!state.Hidden || state.PlacementBeforePeek is not null)
                throw new WindowOperationException(T("errors.pip.previewNotReady"));
            if (!User32.GetWindowRect(window, out Rect bounds)) throw Failure(T("actions.readPosition"), window);
            Rectangle original = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            Rectangle parked = ParkingPlace(original.Size, Screen.AllScreens.Select(screen => screen.Bounds).ToArray(),
                Screen.FromRectangle(original).Bounds);
            state.PlacementBeforePeek = ReadPlacement(window);
            state.Parked = true;
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
    internal void SetParkedReachable(nint window, bool reachable)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out var state) || !state.Parked || !User32.IsWindow(window)) return;
            state.Identity.Require();
            long style = ReadExStyle(window);
            if (!reachable) WriteExStyle(window, style | User32.WS_EX_TRANSPARENT); // Clicks go first, then the window fades.
            ApplyAlpha(window, state, reachable ? ReachableOpacity : HiddenOpacity);
            if (reachable) WriteExStyle(window, style & ~User32.WS_EX_TRANSPARENT);
        }
    }

    internal bool IsParkedReachable(nint window) =>
        IsParked(window) && (ReadExStyle(window) & User32.WS_EX_TRANSPARENT) == 0;

    private const byte ReachableOpacity = 1;

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
