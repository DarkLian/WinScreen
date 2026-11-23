using WinScreen.Native;

namespace WinScreen.Windows;

// No look may make a window take fewer clicks than it did (INV-18).
internal sealed class PressWatch
{
    internal const long FailingFor = 1000;
    private readonly Dictionary<nint, long> _failingSince = [];

    // null: can't be told now, such as when something of the person's is above the window there.
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

    // Only the look's own window, or one below, getting the press counts against it. Anything else there is in front:
    // another window, or Start, search and "Open with", which Windows keeps in layers of their own that walking the
    // stack never meets. One the person made click-through sends presses below by
    // choice, so it can't be told; a cropped one is asked at the middle of the part it keeps.
    public static bool? TakesPresses(nint window, nint overlay, WindowController? windows = null)
    {
        if (!User32.IsWindow(window) || !User32.IsWindowVisible(window) || User32.IsIconic(window)) return null;
        if (windows?.IsClickThrough(window) == true) return null;
        if (!User32.IsWindowEnabled(window)) return null; // The app's own doing, as while it shows a dialog.
        // Hidden by WinScreen behind a tile or a view: see-through and click-through on purpose, and its look waits for
        // it to come back. An effect's own concealing is different: there the window still takes the presses.
        if (windows is not null && windows.IsHidden(window) && !windows.IsDrawnByEffect(window)) return null;
        // Left on another virtual desktop (cloaked by Windows): what is under its middle now is another desktop's.
        if (!VirtualDesktops.OnCurrentDesktop(window) ||
            Dwmapi.DwmGetWindowAttribute(window, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if (!WindowInfo.TryGetVisibleBounds(window, out Rectangle bounds) || bounds.Width < 4 || bounds.Height < 4) return null;
        var middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (WindowInfo.ShapeOf(window) is { Width: >= 4, Height: >= 4 } kept && User32.GetWindowRect(window, out Rect whole))
            middle = new Point(whole.Left + kept.Left + kept.Width / 2, whole.Top + kept.Top + kept.Height / 2);
        if (!Screen.AllScreens.Any(screen => screen.Bounds.Contains(middle))) return null;
        if (!WindowController.AnswersWithin(window, 100)) return null; // A hung app is its own trouble, not the look's.
        nint hit = User32.WindowFromPoint(new NativePoint { X = middle.X, Y = middle.Y });
        nint root = hit == 0 ? 0 : User32.GetAncestor(hit, User32.GA_ROOT);
        if (root == window) return true;
        if (root == 0) return null;
        if (overlay != 0 && root == overlay) return false;
        return IsBelow(root, window) ? false : null;
    }

    private static bool IsBelow(nint other, nint window)
    {
        nint below = window;
        for (int step = 0; step < 2000; step++)
        {
            below = User32.GetWindow(below, User32.GW_HWNDNEXT);
            if (below == 0) return false;
            if (below == other) return true;
        }
        return false; // Too deep to tell: not counted against the look.
    }
}
