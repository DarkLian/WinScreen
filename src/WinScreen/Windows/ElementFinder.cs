using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// Finds the control under the pointer during a crop or region selection, by asking the app through UI
/// Automation for rectangles only: no pixels, text recognition or models. A slow or silent app can't hold
/// anything up, because the asking happens on its own thread and the newest question replaces any waiting one.
/// It exists only for the length of a selection.
/// </summary>
internal sealed unsafe class ElementFinder : IDisposable
{
    private const int DeepestLevel = 24;
    private const int MostSiblings = 300;
    private const int GW_HWNDNEXT = 2;
    private const uint InProcessServer = 1;

    // Places in the COM tables of IUIAutomation, IUIAutomationTreeWalker and IUIAutomationElement.
    private const int ElementFromHandle = 6, ControlViewWalker = 14, FirstChild = 4, NextSibling = 6, BoundingRectangle = 43;

    private readonly nint _overlay;
    private readonly AutoResetEvent _asked = new(false);
    private long _question = long.MinValue;
    private volatile bool _stopping;

    /// <param name="overlay">The selection window, which is skipped when looking for what's under the pointer.</param>
    public ElementFinder(nint overlay)
    {
        _overlay = overlay;
        var thread = new Thread(Answer) { IsBackground = true, Name = "WinScreen element finder" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
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
        nint automation = 0, walker = 0, element = 0;
        Guid implementation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), wanted = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        try
        {
            if (CoCreateInstance(in implementation, 0, InProcessServer, in wanted, out automation) != 0 ||
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, ElementFromHandle))(automation, window, &element) != 0 ||
                element == 0 ||
                ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, ControlViewWalker))(automation, &walker) != 0)
                return null;

            for (int level = 0; level < DeepestLevel; level++)
            {
                nint inside = ChildAt(walker, element, point);
                if (inside == 0)
                    break;
                Marshal.Release(element);
                element = inside;
            }

            return BoundsOf(element) is { Width: > 0, Height: > 0 } found ? found : null;
        }
        catch (COMException)
        {
            return null; // An app that won't say just means dragging by hand.
        }
        finally
        {
            if (element != 0) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
        }
    }

    /// <summary>The child of <paramref name="element"/> containing the point, which the caller releases, or 0.</summary>
    private static nint ChildAt(nint walker, nint element, Point point)
    {
        nint child = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, FirstChild))(walker, element, &child) != 0)
            return 0;
        for (int sibling = 0; child != 0 && sibling < MostSiblings; sibling++)
        {
            if (BoundsOf(child) is { } bounds && bounds.Contains(point))
                return child;
            nint next = 0;
            int result = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, NextSibling))(walker, child, &next);
            Marshal.Release(child);
            child = result == 0 ? next : 0;
        }

        if (child != 0)
            Marshal.Release(child);
        return 0;
    }

    private static Rectangle? BoundsOf(nint element)
    {
        Rect bounds;
        return ((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(element, BoundingRectangle))(element, &bounds) == 0
            ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            : null;
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

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

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid implementation, nint outer, uint context, in Guid wanted, out nint instance);
}
