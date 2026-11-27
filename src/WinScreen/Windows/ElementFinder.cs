using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

// Rectangles only, through UI Automation. Asked on its own thread, the newest question replacing any waiting one, so a
// slow app can't hold anything up.
internal sealed unsafe class ElementFinder : IDisposable
{
    private const int DeepestLevel = 24;
    private const int MostSiblings = 300;
    private const int GW_HWNDNEXT = 2;
    private const uint InProcessServer = 1;

    // Places in the COM tables of IUIAutomation, IUIAutomationTreeWalker and IUIAutomationElement.
    private const int ElementFromHandle = 6, ControlViewWalker = 14, FirstChild = 4, NextSibling = 6, ControlType = 21,
        BoundingRectangle = 43;

    private readonly nint _overlay;
    private readonly AutoResetEvent _asked = new(false);
    private readonly object _lifetime = new();
    private long _question = long.MinValue;
    private volatile bool _stopping;

    public ElementFinder(nint overlay)
    {
        _overlay = overlay;
        var thread = new Thread(Answer) { IsBackground = true, Name = "WinScreen element finder" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // Raised off the UI thread.
    public event Action<FoundElement?>? Found;

    public void Ask(Point screen)
    {
        lock (_lifetime)
        {
            if (_stopping) return;
            Interlocked.Exchange(ref _question, ((long)screen.X << 32) | (uint)screen.Y);
            _asked.Set();
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_stopping) return;
            _stopping = true;
            Found = null;
            _asked.Set();
        }
    }

    private void Answer()
    {
        while (true)
        {
            _asked.WaitOne();
            if (_stopping)
            {
                lock (_lifetime) _asked.Dispose();
                return;
            }

            long question = Interlocked.Read(ref _question);
            var point = new Point((int)(question >> 32), (int)question);
            FoundElement? found = WindowBelow(_overlay, point) is var window and not 0 ? FindElement(window, point) : null;
            // The overlay can close between its own handle check and BeginInvoke; on this thread that would end the process.
            try
            {
                if (!_stopping)
                    Found?.Invoke(found);
            }
            catch (InvalidOperationException closed)
            {
                WinScreen.Diagnostics.Log.Swallowed(closed);
            }
        }
    }

    internal static Rectangle? Find(nint window, Point point) => FindElement(window, point)?.Bounds;

    internal static FoundElement? FindElement(nint window, Point point)
    {
        var path = new List<int>();
        return Walk(window, (walker, element) =>
        {
            for (int level = 0; level < DeepestLevel; level++)
            {
                nint inside = ChildAt(walker, element, point, out int index);
                if (inside == 0)
                    break;
                Marshal.Release(element);
                element = inside;
                path.Add(index);
            }
            return (element, BoundsOf(element) is { Width: > 0, Height: > 0 } found
                ? new FoundElement(found, window, [.. path], ControlTypeOf(element)) : null);
        });
    }

    internal static Rectangle? Locate(FoundElement earlier)
    {
        return Walk<Rectangle?>(earlier.Window, (walker, element) =>
        {
            foreach (int index in earlier.Path)
            {
                nint child = ChildNumber(walker, element, index);
                Marshal.Release(element);
                element = child;
                if (element == 0) return (0, null);
            }
            return (element, ControlTypeOf(element) == earlier.ControlType && BoundsOf(element) is { Width: > 0, Height: > 0 } now
                ? now : null);
        });
    }

    // The step's held element is released here.
    private static T? Walk<T>(nint window, Func<nint, nint, (nint Held, T? Result)> step)
    {
        nint automation = 0, walker = 0, element = 0;
        Guid implementation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), wanted = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        try
        {
            if (CoCreateInstance(in implementation, 0, InProcessServer, in wanted, out automation) != 0 ||
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, ElementFromHandle))(automation, window, &element) != 0 ||
                element == 0 ||
                ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, ControlViewWalker))(automation, &walker) != 0)
                return default;
            (nint held, T? result) = step(walker, element);
            element = held;
            return result;
        }
        catch (COMException)
        {
            return default; // An app that won't say just means dragging by hand.
        }
        finally
        {
            if (element != 0) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
        }
    }

    // The caller releases the result.
    private static nint ChildNumber(nint walker, nint element, int index)
    {
        nint child = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, FirstChild))(walker, element, &child) != 0)
            return 0;
        for (int sibling = 0; child != 0 && sibling < index; sibling++)
        {
            nint next = 0;
            int result = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, NextSibling))(walker, child, &next);
            Marshal.Release(child);
            child = result == 0 ? next : 0;
        }
        return child;
    }

    private static int ControlTypeOf(nint element)
    {
        int type;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, ControlType))(element, &type) == 0 ? type : 0;
    }

    // The caller releases the result.
    private static nint ChildAt(nint walker, nint element, Point point, out int index)
    {
        index = -1;
        nint child = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, FirstChild))(walker, element, &child) != 0)
            return 0;
        for (int sibling = 0; child != 0 && sibling < MostSiblings; sibling++)
        {
            if (BoundsOf(child) is { } bounds && bounds.Contains(point))
            {
                index = sibling;
                return child;
            }
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

    private static nint WindowBelow(nint overlay, Point point)
    {
        for (nint window = GetWindow(overlay, GW_HWNDNEXT); window != 0; window = GetWindow(window, GW_HWNDNEXT))
        {
            // A window presses pass through, such as the one Glass or Dark draws a window again in, is never what is marked.
            if (User32.IsWindowVisible(window) && !User32.IsIconic(window) && !WindowInfo.IsHiddenForView(window) &&
                (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TRANSPARENT) == 0 &&
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

internal sealed record FoundElement(Rectangle Bounds, nint Window, int[] Path, int ControlType);
