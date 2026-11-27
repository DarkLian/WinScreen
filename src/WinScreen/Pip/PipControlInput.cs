using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

// One background thread serves every view for the app's life, so a click made just before the pointer leaves a view
// still happens, and the automation object is made once.
internal sealed class PipControlInput
{
    internal static PipControlInput Shared { get; } = new();

    internal enum Request
    {
        Click,
        // Automation has no double-click: it acts only where a double-click means two clicks, and is unsupported
        // elsewhere.
        DoubleClick,
        ContextMenu,
        Scroll,
        // Nothing is done: "item:" and its name, or "none".
        IsItem,
        // Nothing is done: the named item's "left,top,width,height", or not done.
        FindItem,
    }

    internal readonly record struct Result(bool Done, string What, bool Unsupported = false);

    private readonly BlockingCollection<(WindowIdentity Identity, nint Host, Point At, Request Kind, int Lines, bool Horizontal, Action<Result> Done, string Name)> _requests = new(16);
    private readonly Lazy<Thread> _thread;
    private nint _automation;

    private PipControlInput() => _thread = new Lazy<Thread>(() =>
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen control input" };
        thread.SetApartmentState(ApartmentState.MTA); // The UI Automation client works from the multithreaded apartment.
        thread.Start();
        return thread;
    });

    // False when the queue is full, and done is then never called.
    internal bool Submit(WindowIdentity identity, nint host, Point at, Request kind, Action<Result> done)
    {
        _ = _thread.Value;
        return _requests.TryAdd((identity, host, at, kind, 0, false, done, ""));
    }

    internal Rectangle? ItemBounds(WindowIdentity identity, string name, int waitMilliseconds)
    {
        _ = _thread.Value;
        using var answered = new ManualResetEventSlim();
        Rectangle? found = null;
        if (!_requests.TryAdd((identity, identity.Window, Point.Empty, Request.FindItem, 0, false, result =>
            {
                string[] parts = result.What.Split(',');
                if (result.Done && parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
                    found = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                try { answered.Set(); }
                catch (ObjectDisposedException late) { Diagnostics.Log.Swallowed(late); }
            }, name))) return null;
        return answered.Wait(waitMilliseconds) ? found : null;
    }

    internal bool Scroll(WindowIdentity identity, nint host, Point at, int lines, bool horizontal, Action<Result> done)
    {
        _ = _thread.Value;
        return _requests.TryAdd((identity, host, at, Request.Scroll, lines, horizontal, done, ""));
    }

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            Result result;
            try
            {
                result = request.Kind switch
                {
                    Request.Scroll => ScrollAt(request.Identity, request.Host, request.At, request.Lines, request.Horizontal),
                    Request.FindItem => FindItem(request.Identity, request.Name),
                    _ => Activate(request.Identity, request.Host, request.At, request.Kind),
                };
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

    // Only the surface's own process's controls may be touched.
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
        if (Ole32.CoCreateInstance(in newer, 0, 1, in wanted, out _automation) < 0 &&
            Ole32.CoCreateInstance(in older, 0, 1, in wanted, out _automation) < 0) return _automation = 0;
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
            if (kind == Request.IsItem)
            {
                for (int i = path.Count - 1; i >= 1; i--)
                {
                    int type = 0;
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(path[i], 21))(path[i], &type);
                    if (type is ListItem or TreeItem or DataItem) return new(true, "item:" + NameOf(path[i]));
                }
                return new(true, "none");
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
                // In text a double-click selects a word: through the text's own pattern where it offers one, and
                // impossible where it doesn't.
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

    private const int ListItem = 50007, TreeItem = 50024, DataItem = 50029, Tree = 50023, ToolBar = 50021, MenuBar = 50010,
        TitleBar = 50037;

    private static unsafe string NameOf(nint element)
    {
        nint text = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, 23))(element, &text) < 0 || text == 0) return ""; // get_CurrentName
        try { return Marshal.PtrToStringBSTR(text); }
        finally { Marshal.FreeBSTR(text); }
    }

    private unsafe Result FindItem(WindowIdentity identity, string name)
    {
        if (name.Length == 0 || !Valid(identity, identity.Window, out _)) return new(false, "window gone");
        if (!User32.GetWindowRect(identity.Window, out Rect w)) return new(false, "window gone");
        Rectangle window = Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom);
        nint automation = Automation(), walker = 0, root = 0;
        if (automation == 0) return new(false, "no UI Automation");
        var pending = new Stack<nint>();
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker) < 0 || walker == 0)
                return new(false, "no tree walker");
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6))(automation, identity.Window, &root) < 0 || root == 0)
                return new(false, "no element");
            pending.Push(root);
            root = 0;
            int seen = 0;
            while (pending.Count > 0 && seen++ < 3000)
            {
                nint element = pending.Pop();
                try
                {
                    Rect b = default;
                    ((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(element, 43))(element, &b);
                    Rectangle bounds = Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom);
                    if (seen > 1 && !bounds.IntersectsWith(window)) continue;
                    int type = 0;
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 21))(element, &type);
                    // Never inside a tree (the navigation pane), a toolbar, a menu bar or a title bar: those are left
                    // out, as
                    // they are most of what a slow walk would read.
                    if (seen > 1 && type is Tree or ToolBar or MenuBar or TitleBar) continue;
                    if (type is ListItem or TreeItem or DataItem && bounds.Width > 0 && NameOf(element) == name)
                    {
                        Rectangle shown = Rectangle.Intersect(bounds, window);
                        return new(true, $"{shown.Left},{shown.Top},{shown.Width},{shown.Height}");
                    }
                    nint child = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 4))(walker, element, &child) < 0) continue;
                    var children = new List<nint>();
                    while (child != 0 && children.Count < 500)
                    {
                        children.Add(child);
                        nint next = 0;
                        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 6))(walker, child, &next);
                        child = next;
                    }
                    if (child != 0) Marshal.Release(child);
                    for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]); // First child first.
                }
                finally { Marshal.Release(element); }
            }
            return new(false, "not found");
        }
        finally
        {
            while (pending.Count > 0) Marshal.Release(pending.Pop());
            if (root != 0) Marshal.Release(root);
            if (walker != 0) Marshal.Release(walker);
        }
    }

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
                    // IUIAutomationScrollPattern: get_CurrentHorizontallyScrollable 9, get_CurrentVerticallyScrollable
                    // 10.
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

    // Store app content lives in its own process, in a child window of the original.
    private static bool OwnsChildOf(nint original, uint process)
    {
        for (nint child = User32.GetWindow(original, 5 /* GW_CHILD */); child != 0; child = User32.GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            User32.GetWindowThreadProcessId(child, out uint owner);
            if (owner == process) return true;
        }
        return false;
    }

    // Controls whose double-click is just two clicks: button, check box, hyperlink, menu item, radio button, split
    // button.
    private static readonly HashSet<int> ClickOnly = [50000, 50002, 50005, 50011, 50013, 50031];

    private static readonly (int Id, string Name)[] Patterns = [(10000, "invoke"), (10010, "select"), (10015, "toggle"), (10005, "expand")];

    private const int NoContextMenuInterface = int.MinValue;

    // IUIAutomationTextPattern::RangeFromPoint, then the range's ExpandToEnclosingUnit(word) and Select.
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

    // IUIAutomationElement3::ShowContextMenu (Windows 10 1703 and later).
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
}
