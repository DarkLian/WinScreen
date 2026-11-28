using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

// Rectangles only, through UI Automation. Asked on its own thread, the newest question replacing any waiting one, so a
// slow app can't hold anything up.
internal sealed unsafe class ElementFinder : IDisposable
{
    private const int DeepestLevel = 24;
    private const int MostSiblings = 300;
    private const int MostLooked = 3000;
    private const int GW_HWNDNEXT = 2;
    private const uint InProcessServer = 1;

    // Places in the COM tables of IUIAutomation, IUIAutomationTreeWalker and IUIAutomationElement.
    private const int CompareElements = 3, ElementFromHandle = 6, ControlViewWalker = 14, FirstChild = 4, NextSibling = 6, ControlType = 21,
        AutomationId = 29, ClassName = 30, BoundingRectangle = 43;

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
        return Walk(window, (walker, element) =>
        {
            List<(nint Element, int Index)> below = DeepestAt(walker, element, point);
            if (below.Count > 0)
            {
                Marshal.Release(element);
                element = below[^1].Element;
                foreach ((nint outer, _) in below.Take(below.Count - 1)) Marshal.Release(outer);
            }
            return (element, BoundsOf(element) is { Width: > 0, Height: > 0 } found
                ? new FoundElement(found, window, [.. below.Select(step => step.Index)], ControlTypeOf(element)) : null);
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

    // Elements from the window's own down to the deepest one at the point, outermost first, for a caller that keeps
    // some: it releases each. Null when the app didn't answer.
    internal static List<nint>? ChainAt(nint automation, nint walker, nint window, Point point)
    {
        nint element = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, ElementFromHandle))(automation, window, &element) != 0 ||
            element == 0)
            return null;
        return [element, .. DeepestAt(walker, element, point).Select(step => step.Element)];
    }

    // The caller releases it.
    internal static nint ControlWalkerOf(nint automation)
    {
        nint walker = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, ControlViewWalker))(automation, &walker) == 0 ? walker : 0;
    }

    internal static string ClassNameOf(nint element) => Words(element, ClassName);

    internal static string AutomationIdOf(nint element) => Words(element, AutomationId);

    private static string Words(nint element, int slot)
    {
        nint words = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, slot))(element, &words) != 0 || words == 0)
            return "";
        try { return Marshal.PtrToStringBSTR(words); }
        finally { Marshal.FreeBSTR(words); }
    }

    // The step's held element is released here.
    private static T? Walk<T>(nint window, Func<nint, nint, (nint Held, T? Result)> step)
    {
        nint automation = 0, walker = 0, element = 0;
        try
        {
            if ((automation = CreateAutomation()) == 0 ||
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

    // The newer object, as PipControlInput uses: only it takes timeouts, and a hung app must not hold the pointer's hint
    // for UI Automation's 20 seconds. The caller releases it.
    internal static nint CreateAutomation()
    {
        Guid wanted = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"); // IUIAutomation
        Guid newer = new("E22AD333-B25F-460C-83D0-0581107395C9"), older = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        if (Ole32.CoCreateInstance(in newer, 0, InProcessServer, in wanted, out nint automation) != 0 &&
            Ole32.CoCreateInstance(in older, 0, InProcessServer, in wanted, out automation) != 0) return 0;
        Guid second = new("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A"); // IUIAutomation2
        if (Marshal.QueryInterface(automation, in second, out nint automation2) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation2, 61))(automation2, 1500); // put_ConnectionTimeout
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation2, 63))(automation2, 1500); // put_TransactionTimeout
            Marshal.Release(automation2);
        }
        return automation;
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

    internal static bool Same(nint automation, nint one, nint other)
    {
        int same;
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int*, int>)Slot(automation, CompareElements))(automation, one, other, &same) == 0 &&
               same != 0;
    }

    internal static int ControlTypeOf(nint element)
    {
        int type;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, ControlType))(element, &type) == 0 ? type : 0;
    }

    // The elements below this one at the point, outermost first, each with its place among its siblings; the caller
    // releases each. Of siblings at the point, the one leading deepest is taken: Chrome's first child is a pane over
    // the whole window with nothing in it, and its page is in a later one.
    private static List<(nint Element, int Index)> DeepestAt(nint walker, nint element, Point point)
    {
        int budget = MostLooked;
        return Below(walker, element, point, 0, ref budget);
    }

    private static List<(nint Element, int Index)> Below(nint walker, nint element, Point point, int level, ref int budget)
    {
        var best = new List<(nint Element, int Index)>();
        nint child = 0;
        if (level >= DeepestLevel ||
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, FirstChild))(walker, element, &child) != 0)
            return best;
        for (int sibling = 0; child != 0 && sibling < MostSiblings && budget > 0; sibling++, budget--)
        {
            nint next = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, NextSibling))(walker, child, &next) != 0)
                next = 0;
            if (BoundsOf(child) is { } bounds && bounds.Contains(point))
            {
                List<(nint Element, int Index)> deeper = Below(walker, child, point, level + 1, ref budget);
                deeper.Insert(0, (child, sibling));
                if (deeper.Count > best.Count) (best, deeper) = (deeper, best);
                foreach ((nint unused, _) in deeper) Marshal.Release(unused);
            }
            else
                Marshal.Release(child);
            child = next;
        }
        if (child != 0)
            Marshal.Release(child);
        return best;
    }

    internal static Rectangle? BoundsOf(nint element)
    {
        Rect bounds;
        return ((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(element, BoundingRectangle))(element, &bounds) == 0
            ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            : null;
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

    private static nint WindowBelow(nint overlay, Point point)
    {
        for (nint window = User32.GetWindow(overlay, GW_HWNDNEXT); window != 0; window = User32.GetWindow(window, GW_HWNDNEXT))
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

}

internal sealed record FoundElement(Rectangle Bounds, nint Window, int[] Path, int ControlType);
