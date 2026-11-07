using System.Runtime.InteropServices;
using System.Windows.Automation;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// Finds the control under the pointer during a crop or region selection, by asking the app through UI
/// Automation for rectangles only: no pixels, text recognition or models. A slow or silent app can't hold
/// anything up, because the asking happens on its own thread and the newest question replaces any waiting one.
/// It exists only for the length of a selection.
/// </summary>
internal sealed class ElementFinder : IDisposable
{
    private const int DeepestLevel = 24;
    private const int MostSiblings = 300;
    private const int GW_HWNDNEXT = 2;

    private readonly nint _overlay;
    private readonly AutoResetEvent _asked = new(false);
    private long _question = long.MinValue;
    private volatile bool _stopping;

    /// <param name="overlay">The selection window, which is skipped when looking for what's under the pointer.</param>
    public ElementFinder(nint overlay)
    {
        _overlay = overlay;
        new Thread(Answer) { IsBackground = true, Name = "WinScreen element finder" }.Start();
    }

    /// <summary>Raised off the UI thread with the control's rectangle on screen, or null when there's no answer.</summary>
    public event Action<Rectangle?>? Found;

    public void Ask(Point screen)
    {
        Interlocked.Exchange(ref _question, ((long)screen.X << 32) | (uint)screen.Y);
        _asked.Set();
    }

    public void Dispose()
    {
        _stopping = true;
        Found = null;
        _asked.Set();
    }

    private void Answer()
    {
        while (true)
        {
            _asked.WaitOne();
            if (_stopping)
            {
                _asked.Dispose();
                return;
            }

            long question = Interlocked.Read(ref _question);
            var point = new Point((int)(question >> 32), (int)question);
            Rectangle? found = WindowBelow(_overlay, point) is var window and not 0 ? Find(window, point) : null;
            if (!_stopping)
                Found?.Invoke(found);
        }
    }

    /// <summary>The smallest control at a screen point in this window, or the window's own rectangle if it has none.</summary>
    internal static Rectangle? Find(nint window, Point point)
    {
        try
        {
            AutomationElement element = AutomationElement.FromHandle(window);
            TreeWalker walker = TreeWalker.ControlViewWalker;
            var spot = new System.Windows.Point(point.X, point.Y);
            for (int level = 0; level < DeepestLevel; level++)
            {
                AutomationElement? child = walker.GetFirstChild(element);
                AutomationElement? inside = null;
                for (int sibling = 0; child is not null && sibling < MostSiblings; sibling++)
                {
                    System.Windows.Rect bounds = child.Current.BoundingRectangle;
                    if (!bounds.IsEmpty && bounds.Contains(spot))
                    {
                        inside = child;
                        break;
                    }

                    child = walker.GetNextSibling(child);
                }

                if (inside is null)
                    break;
                element = inside;
            }

            System.Windows.Rect found = element.Current.BoundingRectangle;
            return found.IsEmpty
                ? null
                : Rectangle.FromLTRB((int)Math.Round(found.Left), (int)Math.Round(found.Top), (int)Math.Round(found.Right), (int)Math.Round(found.Bottom));
        }
        catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException or
                                      ArgumentException or TimeoutException)
        {
            return null; // An app that won't say just means dragging by hand.
        }
    }

    /// <summary>The first real window under a point, going down the stack from the selection window.</summary>
    private static nint WindowBelow(nint overlay, Point point)
    {
        for (nint window = GetWindow(overlay, GW_HWNDNEXT); window != 0; window = GetWindow(window, GW_HWNDNEXT))
        {
            if (User32.IsWindowVisible(window) && !User32.IsIconic(window) && !WindowInfo.IsConcealed(window) &&
                WindowInfo.TryGetVisibleBounds(window, out Rectangle bounds) && bounds.Contains(point) &&
                (Dwmapi.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0))
                return window;
        }

        return 0;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint relation);
}
