using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// The watchdog for looks (11.1): no look may make a window take fewer clicks than it did (INV-18). Four times a
/// second each window with a look is asked where a press at its middle would go. One that keeps sending presses
/// elsewhere for a second has its look turned off, and the person is told.
/// </summary>
internal sealed class PressWatch
{
    internal const long FailingFor = 1000;
    private readonly Dictionary<nint, long> _failingSince = [];

    /// <summary>
    /// Takes this check's answers (true: presses arrive; false: they go elsewhere; null: can't be told now, such as
    /// when something of the person's is above the window there) and returns the windows whose look should go.
    /// </summary>
    public List<nint> Check(IEnumerable<(nint Window, bool? TakesPresses)> answers, long now)
    {
        var turnOff = new List<nint>();
        var seen = new HashSet<nint>();
        foreach ((nint window, bool? takes) in answers)
        {
            seen.Add(window);
            if (takes is null) continue; // Unknown leaves the count where it was.
            if (takes.Value)
            {
                _failingSince.Remove(window);
                continue;
            }
            if (!_failingSince.TryGetValue(window, out long since))
            {
                _failingSince[window] = now;
                continue;
            }
            if (now - since < FailingFor) continue;
            _failingSince.Remove(window);
            turnOff.Add(window);
        }
        foreach (nint gone in _failingSince.Keys.Where(window => !seen.Contains(window)).ToArray())
            _failingSince.Remove(gone);
        return turnOff;
    }

    /// <summary>
    /// Where a press at the middle of the window would go. The look's own overlay catching it, or a window below
    /// getting it, means the window doesn't take presses; a window above it there is the person's own arrangement.
    /// </summary>
    public static bool? TakesPresses(nint window, nint overlay)
    {
        if (!User32.IsWindow(window) || !User32.IsWindowVisible(window) || User32.IsIconic(window)) return null;
        if (!WindowInfo.TryGetVisibleBounds(window, out Rectangle bounds) || bounds.Width < 4 || bounds.Height < 4) return null;
        var middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (!Screen.AllScreens.Any(screen => screen.Bounds.Contains(middle))) return null;
        if (!WindowController.AnswersWithin(window, 100)) return null; // A hung app is its own trouble, not the look's.
        nint hit = User32.WindowFromPoint(new NativePoint { X = middle.X, Y = middle.Y });
        nint root = hit == 0 ? 0 : User32.GetAncestor(hit, User32.GA_ROOT);
        if (root == window) return true;
        if (root == 0) return null;
        if (overlay != 0 && root == overlay) return false;
        return IsAbove(root, window) ? null : false;
    }

    /// <summary>Whether <paramref name="other"/> is above <paramref name="window"/> in Windows' order.</summary>
    private static bool IsAbove(nint other, nint window)
    {
        nint above = window;
        for (int step = 0; step < 2000; step++)
        {
            above = User32.GetWindow(above, User32.GW_HWNDPREV);
            if (above == 0) return false;
            if (above == other) return true;
        }
        return true; // Too deep to tell: not counted against the look.
    }
}
