using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

// The original's popups appear where it really is, not where its view is. Display-only ones (click-through) go off the
// screen while it is hidden; message input moves menus and lists to the view's pointer; between visits every one goes
// off the screen.
// Dialogs are never moved: one showing, or the original disabled, is reported so the visit ends and the question is
// seen. Everything moved is put back. Safe from any thread.
internal sealed class SourcePopups
{
    private const uint Move = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    // How near a new popup must be to an open one to belong to it, as a submenu does.
    private const int Adjacent = 8;
    // Windows 11's newer menus, which move themselves back a few times while they open.
    internal const string WinUiPopup = "Microsoft.UI.Content.PopupWindowSiteBridge";
    private readonly nint _source;
    private readonly Dictionary<nint, Placement> _placed = [];
    private readonly Dictionary<nint, Point> _hidden = []; // Popup, and where it was.
    // A newer menu's popup that may still move itself: when it was first seen, where it is, and since when it has been there.
    private readonly Dictionary<nint, (long Seen, Point At, long Since)> _settling = [];
    private const int StillMilliseconds = 100, LongestSettle = 400;
    private long _opened;
    public SourcePopups(nint source) => _source = source;

    // Oldest first, so the newest (a submenu) ends on top of its parent.
    internal Action<IReadOnlyList<nint>> RaisePopups { get; init; } = popups => Windows.TopLayer.RaiseGroup(popups);

    private sealed record Placement(Point Original, Point Placed, Size Size, long Order)
    {
        internal Rectangle PlacedBounds => new(Placed, Size);
        internal Rectangle OriginalBounds => new(Original, Size);
    }

    // view is null at rest, between visits. True if the original is asking something.
    public bool Sweep(Rectangle? view) => SweepCore(view, null, null);

    internal bool SweepMapped(Rectangle view, Point pointer, Point mapped, nint viewWindow) => SweepCore(view, pointer, mapped, viewWindow);

    private bool SweepCore(Rectangle? view, Point? pointer, Point? mapped, nint viewWindow = 0)
    {
        lock (_hidden)
        {
            uint thread = User32.GetWindowThreadProcessId(_source, out uint process);
            var seen = new Dictionary<nint, int>(); // Each popup and its depth in the z-order, topmost 0.
            int viewDepth = -1, depth = 0;
            if (thread != 0)
                TopWindows.Each(window =>
                {
                    if (window == viewWindow && viewWindow != 0) viewDepth = depth;
                    if (window != _source && User32.IsWindowVisible(window) && IsPopupOf(window, _source, process))
                        seen[window] = depth;
                    depth++;
                    return true;
                });

            bool question = thread != 0 && !User32.IsWindowEnabled(_source), opened = false;
            foreach (nint popup in seen.Keys)
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
                        // A newer menu's submenu starts where its parent started, then slides beside it: placed at
                        // once, it went onto its parent and was held there. One that starts there waits until it has
                        // moved and kept still, or a little while in any case: a menu's strip of buttons starts there
                        // too and never moves, and then goes with its menu as before.
                        if (Windows.WindowInfo.ClassName(popup) == WinUiPopup)
                        {
                            bool waiting = _settling.TryGetValue(popup, out var settling);
                            bool onAnother = _placed.Values.Any(other => other.Original == current);
                            if (waiting || onAnother)
                            {
                                long now = Environment.TickCount64;
                                if (!waiting) settling = (now, current, now);
                                else if (settling.At != current) settling = (settling.Seen, current, now);
                                _settling[popup] = settling;
                                bool moving = !onAnother && now - settling.Since < StillMilliseconds;
                                if (now - settling.Seen < LongestSettle && (onAnother || moving)) continue;
                            }
                        }
                        _settling.Remove(popup);
                        Rectangle native = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        Point place = PlaceNew(native, at, sourcePoint);
                        _placed[popup] = new Placement(current, place, native.Size, ++_opened);
                        if (place != current) User32.SetWindowPos(popup, 0, place.X, place.Y, 0, 0, Move);
                        if (Windows.WindowInfo.ClassName(popup) == WinUiPopup) PopupGuard.Hold(popup, place);
                        opened = true;
                    }
                    else
                    {
                        if (current != previous.Placed)
                        {
                            // Layout may put a popup back at its source coordinates. Its destination belongs to
                            // this opening, not to the latest pointer or transient layout position.
                            User32.SetWindowPos(popup, 0, previous.Placed.X, previous.Placed.Y, 0, 0, Move);
                        }
                        if (Windows.WindowInfo.ClassName(popup) == WinUiPopup) PopupGuard.Hold(popup, previous.Placed);
                    }
                    continue;
                }
                // At rest every popup goes: one the app keeps open after a visit (Chrome's tab card, which it shows while
                // it thinks the pointer is on a tab) stayed where it was put by the view, or went where the app keeps it,
                // beside the hidden original: on the screen's edge.
                bool away = kind == Kind.DisplayOnly || view is not { } area ||
                            !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(area);
                // Sent off before but put back by its app: send it off again.
                if (away && (!_hidden.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)) Hide(popup, r);
                else if (!away && _hidden.ContainsKey(popup)) Show(popup);
            }
            foreach (nint gone in _placed.Keys.Where(popup => !seen.ContainsKey(popup)).ToArray()) _placed.Remove(gone);
            foreach (nint gone in _settling.Keys.Where(popup => !seen.ContainsKey(popup)).ToArray()) _settling.Remove(gone);
            foreach (nint gone in _hidden.Keys.Where(popup => !seen.ContainsKey(popup)).ToArray())
                Show(gone); // Closed, or hidden by its app: put back as it was for next time.
            if (_placed.Count != 0 && (opened || OutOfOrder(seen, viewDepth)))
                RaisePopups(_placed.OrderBy(entry => entry.Value.Order).Select(entry => entry.Key).ToArray());
            return question;
        }
    }

    // A submenu is one beside a popup already placed for this opening: Windows lays it out from where the parent really
    // is, so it is left where its app put it when that is already beside the moved parent, or else moved with the
    // parent.
    private Point PlaceNew(Rectangle native, Point at, Point sourcePoint)
    {
        Rectangle area = Screen.FromPoint(at).WorkingArea;
        foreach (Placement parent in _placed.Values.OrderByDescending(placement => placement.Order))
        {
            if (Beside(native, parent.PlacedBounds)) return native.Location;
            if (Beside(native, parent.OriginalBounds))
                return PipGeometry.KeepInside(new Rectangle(native.Location + (Size)parent.Placed - (Size)parent.Original, native.Size), area);
        }
        int dx = native.Left - sourcePoint.X, dy = native.Top - sourcePoint.Y;
        if (Math.Abs(dx) > native.Width) dx = 0;
        if (Math.Abs(dy) > native.Height) dy = 0;
        return PipGeometry.KeepInside(new Rectangle(at.X + dx, at.Y + dy, native.Width, native.Height), area);
    }

    private static bool Beside(Rectangle popup, Rectangle other)
    {
        Rectangle near = other;
        near.Inflate(Adjacent, Adjacent);
        return popup.IntersectsWith(near);
    }

    private bool OutOfOrder(Dictionary<nint, int> depths, int viewDepth)
    {
        int below = int.MaxValue;
        foreach (nint popup in _placed.OrderBy(entry => entry.Value.Order).Select(entry => entry.Key))
        {
            if (!depths.TryGetValue(popup, out int depth)) continue;
            if (depth >= below || viewDepth >= 0 && depth > viewDepth) return true;
            below = depth;
        }
        return false;
    }

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

    // Also one of its program's own menus, which Windows gives no owner.
    internal static bool IsPopupOf(nint window, nint source, uint sourceProcess)
    {
        if (Windows.WindowInfo.IsOwnedBy(window, source)) return true;
        if (Windows.WindowInfo.ClassName(window) != "#32768") return false;
        User32.GetWindowThreadProcessId(window, out uint process);
        return process == sourceProcess;
    }

    internal enum Kind { DisplayOnly, Interactive, Dialog }

    // Click-through windows can't be used, so they only show something; a captioned, activatable one asks something.
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

}
