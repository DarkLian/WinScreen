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
    /// <summary>How near a new popup must be to an open one to belong to it, as a submenu does.</summary>
    private const int Adjacent = 8;
    /// <summary>Windows 11's newer menus, which move themselves back a few times while they open.</summary>
    internal const string WinUiPopup = "Microsoft.UI.Content.PopupWindowSiteBridge";
    private readonly nint _source;
    private readonly Dictionary<nint, Placement> _placed = [];
    private readonly Dictionary<nint, Point> _hidden = []; // Popup, and where it was.
    private long _opened;
    public SourcePopups(nint source) => _source = source;

    /// <summary>Lifts the menus in use, oldest first, so the newest (a submenu) ends on top of its parent.</summary>
    internal Action<IReadOnlyList<nint>> RaisePopups { get; init; } = popups => Windows.TopLayer.RaiseGroup(popups);

    /// <param name="Original">Where its app first put it, to go back to.</param>
    /// <param name="Placed">Where it stays for this opening.</param>
    /// <param name="Order">When it opened: a later popup of the same chain is above an earlier one.</param>
    private sealed record Placement(Point Original, Point Placed, Size Size, long Order)
    {
        internal Rectangle PlacedBounds => new(Placed, Size);
        internal Rectangle OriginalBounds => new(Original, Size);
    }

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
            var seen = new Dictionary<nint, int>(); // Each popup and its depth in the z-order, topmost 0.
            int viewDepth = -1, depth = 0;
            if (thread != 0)
                EnumWindows((window, _) =>
                {
                    if (window == viewWindow && viewWindow != 0) viewDepth = depth;
                    if (window != _source && User32.IsWindowVisible(window) && IsPopupOf(window, _source, process))
                        seen[window] = depth;
                    depth++;
                    return true;
                }, 0);

            bool question = thread != 0 && !IsWindowEnabled(_source), opened = false;
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
                bool away = kind == Kind.DisplayOnly ||
                            view is { } area && !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(area);
                // Sent off before but put back by its app: send it off again.
                if (away && (!_hidden.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)) Hide(popup, r);
                else if (!away && _hidden.ContainsKey(popup)) Show(popup);
            }
            foreach (nint gone in _placed.Keys.Where(popup => !seen.ContainsKey(popup)).ToArray()) _placed.Remove(gone);
            foreach (nint gone in _hidden.Keys.Where(popup => !seen.ContainsKey(popup)).ToArray())
                Show(gone); // Closed, or hidden by its app: put back as it was for next time.
            if (_placed.Count != 0 && (opened || OutOfOrder(seen, viewDepth)))
                RaisePopups(_placed.OrderBy(entry => entry.Value.Order).Select(entry => entry.Key).ToArray());
            return question;
        }
    }

    /// <summary>
    /// Where a popup that just opened goes. One beside a popup already placed for this opening is its submenu:
    /// left where its app put it when that is already beside the moved parent (Windows lays a menu's submenu out
    /// from where the parent really is), or else moved with the parent, as one laid out from the parent's first
    /// place would be. Any other goes next to the pointer.
    /// </summary>
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

    /// <summary>
    /// Whether the placed popups need lifting: one is behind the view, or a later one (a submenu) is under an
    /// earlier one. An order that is already right is left alone.
    /// </summary>
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
