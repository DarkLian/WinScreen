using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// Acts on controls of surfaces that ignore posted mouse messages (the <see cref="InputRoute.Automation"/> route),
/// through UI Automation. One background thread serves every view for the life of the app, so a click made just
/// before the pointer leaves a view still happens, and the automation object is made once rather than per click.
/// Each request reports what became of it.
/// </summary>
internal sealed class PipControlInput
{
    internal static PipControlInput Shared { get; } = new();

    internal enum Request
    {
        /// <summary>Invoke, select, toggle or expand the control under the point, or focus an edit box.</summary>
        Click,
        /// <summary>
        /// The second click of a double-click. Automation has no double-click, so it acts only where a double-click
        /// means two clicks (buttons, check boxes, links, menu items); on items whose double-click opens something
        /// it is unsupported.
        /// </summary>
        DoubleClick,
        /// <summary>Open the control's own context menu, as a right-click would.</summary>
        ContextMenu,
        /// <summary>Scroll what scrolls under the point by lines, as the wheel would; negative is up or left.</summary>
        Scroll,
    }

    /// <summary>
    /// What a request did: a pattern name ("invoke", "select", "toggle", "expand", "focus", "menu") or why nothing
    /// happened. <paramref name="Unsupported"/> means the control can't take this kind of input at all.
    /// </summary>
    internal readonly record struct Result(bool Done, string What, bool Unsupported = false);

    private readonly BlockingCollection<(WindowIdentity Identity, nint Host, Point At, Request Kind, int Lines, bool Horizontal, Action<Result> Done)> _requests = new(16);
    private readonly Lazy<Thread> _thread;
    private nint _automation;

    private PipControlInput() => _thread = new Lazy<Thread>(() =>
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen control input" };
        thread.SetApartmentState(ApartmentState.MTA); // The UI Automation client works from the multithreaded apartment.
        thread.Start();
        return thread;
    });

    /// <summary>Queues a request at a screen point on an automation surface inside the identified window.</summary>
    /// <returns>False when the queue is full; <paramref name="done"/> is then not called.</returns>
    internal bool Submit(WindowIdentity identity, nint host, Point at, Request kind, Action<Result> done)
    {
        _ = _thread.Value;
        return _requests.TryAdd((identity, host, at, kind, 0, false, done));
    }

    /// <summary>Queues a scroll by whole lines at a screen point.</summary>
    internal bool Scroll(WindowIdentity identity, nint host, Point at, int lines, bool horizontal, Action<Result> done)
    {
        _ = _thread.Value;
        return _requests.TryAdd((identity, host, at, Request.Scroll, lines, horizontal, done));
    }

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            Result result;
            try
            {
                result = request.Kind == Request.Scroll
                    ? ScrollAt(request.Identity, request.Host, request.At, request.Lines, request.Horizontal)
                    : Activate(request.Identity, request.Host, request.At, request.Kind);
            }
            catch (COMException e)
            {
                // Controls can disappear between the press and activation; a broken connection is rebuilt next time.
                result = new(false, $"automation error 0x{e.HResult:X8}");
                Drop();
            }
            catch (Exception e) when (e is InvalidOperationException or AccessViolationException)
            {
                result = new(false, e.GetType().Name);
                Drop();
            }
            try { request.Done(result); } catch (Exception caught) { Diagnostics.Log.Swallowed(caught); }
        }
    }

    /// <summary>The surface is still inside the original; its process is the only one whose controls may be touched.</summary>
    private static bool Valid(WindowIdentity identity, nint host, out uint process)
    {
        User32.GetWindowThreadProcessId(host, out process);
        return identity.StillThere() && User32.IsWindow(host) &&
               (host == identity.Window || User32.GetAncestor(host, User32.GA_ROOT) == identity.Window);
    }

    private void Drop()
    {
        if (_automation != 0) Marshal.Release(_automation);
        _automation = 0;
    }

    private unsafe nint Automation()
    {
        if (_automation != 0) return _automation;
        Guid wanted = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"); // IUIAutomation
        Guid newer = new("E22AD333-B25F-460C-83D0-0581107395C9"), older = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        if (CoCreateInstance(in newer, 0, 1, in wanted, out _automation) < 0 &&
            CoCreateInstance(in older, 0, 1, in wanted, out _automation) < 0) return _automation = 0;
        // A busy or broken app must not hold this thread for UI Automation's long default timeouts.
        Guid second = new("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A"); // IUIAutomation2
        if (Marshal.QueryInterface(_automation, in second, out nint automation2) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation2, 61))(automation2, 1500); // put_ConnectionTimeout
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(automation2, 63))(automation2, 1500); // put_TransactionTimeout
            Marshal.Release(automation2);
        }
        return _automation;
    }

    /// <summary>Elements under the point, outermost first, from the surface's own element.</summary>
    private unsafe List<nint> PathAt(nint automation, nint walker, nint window, Point at)
    {
        var path = new List<nint>();
        nint root = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6))(automation, window, &root) < 0 || root == 0)
            return path;
        path.Add(root);
        for (int depth = 0; depth < 32; depth++)
        {
            nint child = ChildAt(walker, path[^1], at);
            if (child == 0) break;
            path.Add(child);
        }
        return path;
    }

    private unsafe Result Activate(WindowIdentity identity, nint host, Point at, Request kind)
    {
        if (!Valid(identity, host, out uint hostProcess)) return new(false, "window gone");
        nint automation = Automation(), walker = 0;
        if (automation == 0) return new(false, "no UI Automation");
        var path = new List<nint>();
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker) < 0 || walker == 0)
                return new(false, "no tree walker");
            path = PathAt(automation, walker, host, at);
            if (path.Count < 2 && host != identity.Window)
            {
                // A frame's input window can sit over content it doesn't hold (Store apps): look from the original.
                foreach (nint element in path) Marshal.Release(element);
                path = PathAt(automation, walker, identity.Window, at);
            }
            // Icons and text can sit inside an actionable control; walk back to that control, but never to the
            // surface itself, which would act on something the pointer isn't on.
            for (int i = path.Count - 1; i >= 1; i--)
            {
                if (!Valid(identity, host, out _)) return new(false, "window gone");
                nint element = path[i];
                int enabled = 0, type = 0, process = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 20))(element, &process) < 0 ||
                    (process != identity.ProcessId && process != hostProcess && !OwnsChildOf(identity.Window, (uint)process)))
                    return new(false, "element of another program");
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 28))(element, &enabled) < 0 || enabled == 0)
                    return new(false, "disabled control");
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 21))(element, &type);
                if (kind == Request.ContextMenu)
                {
                    int shown = ShowContextMenu(element);
                    if (shown == NoContextMenuInterface) return new(false, "context menus need a newer Windows", true);
                    if (shown >= 0) return new(true, "menu");
                    continue; // Text inside a control has no menu of its own; its control may.
                }
                // In text a double-click selects a word: done through the text's own pattern where it offers one
                // (roadmap P2-12), and said to be impossible where it doesn't.
                if (type is 50004 or 50030 && kind == Request.DoubleClick)
                    return SelectWordAt(element, at) ? new(true, "select word") : new(false, "a double-click in a text box", true);
                if (type == 50004) // Edit controls need keyboard focus, without changing their value.
                    return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(element, 3))(element) >= 0
                        ? new(true, "focus") : new(false, "focus refused");
                foreach (var (patternId, name) in Patterns)
                {
                    nint pattern = 0;
                    try
                    {
                        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(element, 16))(element, patternId, &pattern) < 0 || pattern == 0) continue;
                        if (kind == Request.DoubleClick && !ClickOnly.Contains(type))
                            return new(false, "a double-click on an item that opens it", true);
                        // Invoke, Select, Toggle and Expand are each the first pattern method.
                        return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(pattern, 3))(pattern) >= 0
                            ? new(true, name) : new(false, name + " refused");
                    }
                    finally { if (pattern != 0) Marshal.Release(pattern); }
                }
            }
            return kind == Request.ContextMenu ? new(false, "no context menu here", true) : new(false, "no control to activate");
        }
        finally
        {
            foreach (nint element in path) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
        }
    }

    /// <summary>Scrolls the innermost element under the point that scrolls that way, by small steps (lines).</summary>
    private unsafe Result ScrollAt(WindowIdentity identity, nint host, Point at, int lines, bool horizontal)
    {
        if (!Valid(identity, host, out uint hostProcess)) return new(false, "window gone");
        nint automation = Automation(), walker = 0;
        if (automation == 0) return new(false, "no UI Automation");
        var path = new List<nint>();
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker) < 0 || walker == 0)
                return new(false, "no tree walker");
            path = PathAt(automation, walker, host, at);
            if (path.Count < 2 && host != identity.Window)
            {
                foreach (nint element in path) Marshal.Release(element);
                path = PathAt(automation, walker, identity.Window, at);
            }
            for (int i = path.Count - 1; i >= 0; i--) // The surface itself may be what scrolls.
            {
                nint element = path[i], pattern = 0;
                int process = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 20))(element, &process) < 0 ||
                    (process != identity.ProcessId && process != hostProcess && !OwnsChildOf(identity.Window, (uint)process)))
                    return new(false, "element of another program");
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(element, 16))(element, 10004, &pattern) < 0 || pattern == 0) continue;
                try
                {
                    int scrollable = 0;
                    // IUIAutomationScrollPattern: get_CurrentHorizontallyScrollable 9, get_CurrentVerticallyScrollable 10.
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(pattern, horizontal ? 9 : 10))(pattern, &scrollable);
                    if (scrollable == 0) continue;
                    const int NoAmount = 2, SmallDecrement = 1, SmallIncrement = 4;
                    int step = lines < 0 ? SmallDecrement : SmallIncrement;
                    for (int n = 0; n < Math.Abs(lines); n++)
                    {
                        // Scroll(horizontalAmount, verticalAmount); stops quietly at the end, as the wheel does.
                        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, int>)Slot(pattern, 3))(pattern,
                            horizontal ? step : NoAmount, horizontal ? NoAmount : step);
                        if (hr < 0) return n > 0 ? new(true, "scroll") : new(false, "scroll refused");
                    }
                    return new(true, "scroll");
                }
                finally { Marshal.Release(pattern); }
            }
            return new(false, "nothing scrolls here", true);
        }
        finally
        {
            foreach (nint element in path) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
        }
    }

    /// <summary>A child window of the original belongs to the process (Store app content lives in its own process).</summary>
    private static bool OwnsChildOf(nint original, uint process)
    {
        for (nint child = User32.GetWindow(original, 5 /* GW_CHILD */); child != 0; child = User32.GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            User32.GetWindowThreadProcessId(child, out uint owner);
            if (owner == process) return true;
        }
        return false;
    }

    // Controls whose double-click is just two clicks: button, check box, hyperlink, menu item, radio button, split button.
    private static readonly HashSet<int> ClickOnly = [50000, 50002, 50005, 50011, 50013, 50031];

    private static readonly (int Id, string Name)[] Patterns = [(10000, "invoke"), (10010, "select"), (10015, "toggle"), (10005, "expand")];

    private const int NoContextMenuInterface = int.MinValue;

    /// <summary>
    /// Selects the word at a screen point through the element's text pattern: IUIAutomationTextPattern::RangeFromPoint,
    /// then the range's ExpandToEnclosingUnit(word) and Select. False when the element offers no text pattern or any
    /// step is refused.
    /// </summary>
    private static unsafe bool SelectWordAt(nint element, Point at)
    {
        const int TextPatternId = 10014, TextUnitWord = 2;
        nint pattern = 0, range = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(element, 16))(element, TextPatternId, &pattern) < 0 || pattern == 0)
                return false;
            var point = new NativePoint { X = at.X, Y = at.Y };
            if (((delegate* unmanaged[Stdcall]<nint, NativePoint, nint*, int>)Slot(pattern, 3))(pattern, point, &range) < 0 || range == 0)
                return false;
            return ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(range, 6))(range, TextUnitWord) >= 0 &&
                   ((delegate* unmanaged[Stdcall]<nint, int>)Slot(range, 16))(range) >= 0;
        }
        finally
        {
            if (range != 0) Marshal.Release(range);
            if (pattern != 0) Marshal.Release(pattern);
        }
    }

    /// <summary>IUIAutomationElement3::ShowContextMenu (Windows 10 1703 and later).</summary>
    private static unsafe int ShowContextMenu(nint element)
    {
        Guid third = new("8471DF34-AEE0-4A01-A7DE-7DB9AF12C296");
        if (Marshal.QueryInterface(element, in third, out nint element3) < 0) return NoContextMenuInterface;
        try { return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(element3, 91))(element3); }
        finally { Marshal.Release(element3); }
    }

    private static unsafe nint ChildAt(nint walker, nint parent, Point at)
    {
        nint child = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 4))(walker, parent, &child) < 0) return 0;
        for (int count = 0; child != 0 && count < 300; count++)
        {
            Rect bounds = default;
            if (((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(child, 43))(child, &bounds) >= 0 &&
                Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom).Contains(at)) return child;
            nint next = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 6))(walker, child, &next);
            Marshal.Release(child);
            child = next;
        }
        if (child != 0) Marshal.Release(child);
        return 0;
    }

    private static unsafe nint Slot(nint com, int index) => (*(nint**)com)[index];
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid cls, nint outer, uint context, in Guid iid, out nint instance);
}
