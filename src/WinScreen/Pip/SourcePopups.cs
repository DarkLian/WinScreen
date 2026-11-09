using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// The hidden original's own popups. They appear where the original really is, not where its view is, so:
/// <list type="bullet">
/// <item>display-only ones (click-through: link text, tooltips, loading bubbles) are moved off the screen while
/// the original is hidden;</item>
/// <item>message input relocates menus and lists to the view pointer; compatible input keeps those already
/// by the pointer and conceals ones wholly away from the view;</item>
/// <item>dialogs are never moved; one showing, or the original being disabled, is reported, so the visit can
/// end and the question be seen.</item>
/// </list>
/// Everything moved is put back where it was. Safe to use from any thread.
/// </summary>
internal sealed class SourcePopups
{
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private readonly nint _source;
    private readonly Dictionary<nint, (Point Original, Point Placed)> _placed = [];
    private readonly Dictionary<nint, Point> _hidden = []; // Popup, and where it was.
    public SourcePopups(nint source) => _source = source;
    internal Action<nint> RaisePopup { get; init; } = Windows.TopLayer.Raise;

    /// <summary>
    /// Moves popups off the screen or back. <paramref name="view"/> is where the pointer works with the original;
    /// null at rest, when only display-only popups are moved.
    /// </summary>
    /// <returns>True if the original is asking something.</returns>
    public bool Sweep(Rectangle? view) => SweepCore(view, null, null);

    internal bool SweepMapped(Rectangle view, Point pointer, Point mapped, nint viewWindow) => SweepCore(view, pointer, mapped, viewWindow);

    private bool SweepCore(Rectangle? view, Point? pointer, Point? mapped, nint viewWindow = 0)
    {
        lock (_hidden)
        {
            uint thread = User32.GetWindowThreadProcessId(_source, out uint process);
            var seen = new HashSet<nint>();
            if (thread != 0)
                EnumWindows((window, _) =>
                {
                    if (window != _source && User32.IsWindowVisible(window) && IsPopupOf(window, _source, process))
                        seen.Add(window);
                    return true;
                }, 0);

            bool question = thread != 0 && !IsWindowEnabled(_source);
            foreach (nint popup in seen)
            {
                if (!User32.GetWindowRect(popup, out Rect r)) continue;
                Kind kind = KindOf(popup);
                if (kind == Kind.Dialog)
                {
                    question = true;
                    continue;
                }
                if (kind == Kind.Interactive && pointer is { } at && mapped is { } sourcePoint)
                {
                    Point current = new(r.Left, r.Top);
                    if (!_placed.TryGetValue(popup, out var previous))
                    {
                        int width = r.Right - r.Left, height = r.Bottom - r.Top;
                        int dx = r.Left - sourcePoint.X, dy = r.Top - sourcePoint.Y;
                        if (Math.Abs(dx) > width) dx = 0;
                        if (Math.Abs(dy) > height) dy = 0;
                        Rectangle desired = new(at.X + dx, at.Y + dy, width, height);
                        Point place = PipGeometry.KeepInside(desired, Screen.FromPoint(at).WorkingArea);
                        _placed[popup] = (current, place);
                        User32.SetWindowPos(popup, 0, place.X, place.Y, 0, 0, Move);
                        RaisePopup(popup);
                    }
                    else if (current != previous.Placed)
                    {
                        // Layout may put a popup back at its source coordinates. Its destination belongs to
                        // this opening, not to the latest pointer or transient layout position.
                        User32.SetWindowPos(popup, 0, previous.Placed.X, previous.Placed.Y, 0, 0, Move);
                    }
                    if (BehindView(popup, viewWindow)) RaisePopup(popup);
                    continue;
                }
                bool away = kind == Kind.DisplayOnly ||
                            view is { } area && !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(area);
                // Sent off before but put back by its app: send it off again.
                if (away && (!_hidden.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)) Hide(popup, r);
                else if (!away && _hidden.ContainsKey(popup)) Show(popup);
            }
            foreach (nint gone in _placed.Keys.Where(popup => !seen.Contains(popup)).ToArray()) _placed.Remove(gone);
            foreach (nint gone in _hidden.Keys.Where(popup => !seen.Contains(popup)).ToArray())
                Show(gone); // Closed, or hidden by its app: put back as it was for next time.
            return question;
        }
    }

    /// <summary>Puts every popup moved away back where it was.</summary>
    public void ShowAll()
    {
        lock (_hidden)
        {
            foreach (var (popup, location) in _placed)
                if (User32.GetWindowRect(popup, out Rect bounds) && new Point(bounds.Left, bounds.Top) == location.Placed)
                    User32.SetWindowPos(popup, 0, location.Original.X, location.Original.Y, 0, 0, Move);
            _placed.Clear();
            foreach (nint popup in _hidden.Keys.ToArray()) Show(popup);
        }
    }

    /// <summary>
    /// The original's popup: owned by it, by any of its program's threads or a helper process, or one of its program's
    /// own menus, which Windows doesn't give an owner.
    /// </summary>
    internal static bool IsPopupOf(nint window, nint source, uint sourceProcess)
    {
        if (Windows.WindowInfo.IsOwnedBy(window, source)) return true;
        if (Windows.WindowInfo.ClassName(window) != "#32768") return false;
        User32.GetWindowThreadProcessId(window, out uint process);
        return process == sourceProcess;
    }

    internal enum Kind { DisplayOnly, Interactive, Dialog }

    /// <summary>Click-through windows can't be used, so they only show something; a captioned, activatable one asks something.</summary>
    internal static Kind KindOf(nint popup)
    {
        long ex = User32.GetWindowLongPtr(popup, User32.GWL_EXSTYLE);
        long style = User32.GetWindowLongPtr(popup, User32.GWL_STYLE);
        if ((ex & User32.WS_EX_TRANSPARENT) != 0 || Windows.WindowInfo.ClassName(popup) == "tooltips_class32")
            return Kind.DisplayOnly;
        bool activatable = (ex & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) == 0;
        if (activatable && (style & User32.WS_CAPTION) == User32.WS_CAPTION)
            return Kind.Dialog;
        return Kind.Interactive;
    }

    private static bool BehindView(nint popup, nint view)
    {
        if (view == 0) return false;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), 5);
        for (int count = 0; window != 0 && count < 4096; count++, window = User32.GetWindow(window, 2))
        {
            if (window == popup) return false;
            if (window == view) return true;
        }
        return false;
    }

    private void Hide(nint popup, Rect r)
    {
        if (!_hidden.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)
            _hidden[popup] = new Point(r.Left, r.Top);
        Rectangle all = SystemInformation.VirtualScreen;
        User32.SetWindowPos(popup, 0, all.Left - (r.Right - r.Left) - 100, all.Top - (r.Bottom - r.Top) - 100, 0, 0, Move);
    }

    private void Show(nint popup)
    {
        if (!_hidden.Remove(popup, out Point at) || !User32.IsWindow(popup)) return;
        // Only if it's still where it was sent: if the app has placed it since, that place wins.
        if (User32.GetWindowRect(popup, out Rect r) && r.Right <= SystemInformation.VirtualScreen.Left)
            User32.SetWindowPos(popup, 0, at.X, at.Y, 0, 0, Move);
    }

    private delegate bool EnumWindowsProc(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}
