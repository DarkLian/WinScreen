using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// The hidden original's own popups. They appear where the original really is, not where its view is, so:
/// <list type="bullet">
/// <item>display-only ones (click-through: link text, tooltips, loading bubbles) are moved off the screen while
/// the original is hidden;</item>
/// <item>menus and lists stay where the app puts them, by the pointer during a visit, but are moved off the
/// screen if they end up wholly away from the view;</item>
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
    private readonly Dictionary<nint, Point> _hidden = []; // Popup, and where it was.

    public SourcePopups(nint source) => _source = source;

    /// <summary>
    /// Moves popups off the screen or back. <paramref name="view"/> is where the pointer works with the original;
    /// null at rest, when only display-only popups are moved.
    /// </summary>
    /// <returns>True if the original is asking something.</returns>
    public bool Sweep(Rectangle? view)
    {
        lock (_hidden)
        {
            uint thread = User32.GetWindowThreadProcessId(_source, out _);
            var seen = new HashSet<nint>();
            if (thread != 0)
                EnumThreadWindows(thread, (window, _) =>
                {
                    if (window != _source && User32.IsWindowVisible(window) && Windows.WindowInfo.IsOwnedBy(window, _source))
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
                bool away = kind == Kind.DisplayOnly ||
                            view is { } area && !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(area);
                // Sent off before but put back by its app: send it off again.
                if (away && (!_hidden.ContainsKey(popup) || r.Right > SystemInformation.VirtualScreen.Left)) Hide(popup, r);
                else if (!away && _hidden.ContainsKey(popup)) Show(popup);
            }
            foreach (nint gone in _hidden.Keys.Where(popup => !seen.Contains(popup)).ToArray())
                Show(gone); // Closed, or hidden by its app: put back as it was for next time.
            return question;
        }
    }

    /// <summary>Puts every popup moved away back where it was.</summary>
    public void ShowAll()
    {
        lock (_hidden)
            foreach (nint popup in _hidden.Keys.ToArray())
                Show(popup);
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
    private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}
