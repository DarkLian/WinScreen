using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>
/// Activates controls on surfaces that ignore posted legacy mouse messages (WinUI islands), through UI
/// Automation. One background thread serves every view for the life of the app, so a click made just before
/// the pointer leaves a view still happens, and the automation object is made once rather than per click.
/// Each request reports what became of it.
/// </summary>
internal sealed class PipControlInput
{
    internal static PipControlInput Shared { get; } = new();

    /// <summary>What a request did: a pattern name ("invoke", "select", "toggle", "expand", "focus") or why nothing happened.</summary>
    internal readonly record struct Result(bool Done, string What);

    private readonly BlockingCollection<(WindowIdentity Identity, nint Host, Point At, Action<Result> Done)> _requests = new(16);
    private readonly Lazy<Thread> _thread;
    private nint _automation;

    private PipControlInput() => _thread = new Lazy<Thread>(() =>
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen control input" };
        thread.SetApartmentState(ApartmentState.MTA); // The UI Automation client works from the multithreaded apartment.
        thread.Start();
        return thread;
    });

    /// <summary>Queues a click at a screen point on a pointer-only surface of the identified window.</summary>
    /// <returns>False when the queue is full; <paramref name="done"/> is then not called.</returns>
    internal bool Click(WindowIdentity identity, nint host, Point at, Action<Result> done)
    {
        _ = _thread.Value;
        return _requests.TryAdd((identity, host, at, done));
    }

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            Result result;
            try { result = Activate(request.Identity, request.Host, request.At); }
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
            try { request.Done(result); } catch (Exception) { /* A finished view's callback must not stop the thread. */ }
        }
    }

    private static bool Valid(WindowIdentity identity, nint host)
    {
        User32.GetWindowThreadProcessId(host, out uint process);
        return identity.StillThere() && process == identity.ProcessId && User32.GetAncestor(host, User32.GA_ROOT) == identity.Window;
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

    private unsafe Result Activate(WindowIdentity identity, nint host, Point at)
    {
        if (!Valid(identity, host)) return new(false, "window gone");
        nint automation = Automation(), walker = 0, root = 0;
        if (automation == 0) return new(false, "no UI Automation");
        var path = new List<nint>();
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6))(automation, host, &root) < 0 || root == 0)
                return new(false, "no element for the surface");
            path.Add(root);
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker) < 0 || walker == 0)
                return new(false, "no tree walker");
            for (int depth = 0; depth < 24; depth++)
            {
                nint child = ChildAt(walker, path[^1], at);
                if (child == 0) break;
                path.Add(child);
            }
            // Icons and text can sit inside an actionable button; walk back to that button, but never to the
            // surface itself, which would act on something the pointer isn't on.
            for (int i = path.Count - 1; i >= 1; i--)
            {
                if (!Valid(identity, host)) return new(false, "window gone");
                nint element = path[i];
                int enabled = 0, type = 0, process = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 20))(element, &process) < 0 || process != identity.ProcessId)
                    return new(false, "element of another process");
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 28))(element, &enabled) < 0 || enabled == 0)
                    return new(false, "disabled control");
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 21))(element, &type);
                if (type == 50004) // Edit controls need keyboard focus, without changing their value.
                    return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(element, 3))(element) >= 0
                        ? new(true, "focus") : new(false, "focus refused");
                foreach (var (patternId, name) in Patterns)
                {
                    nint pattern = 0;
                    try
                    {
                        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(element, 16))(element, patternId, &pattern) < 0 || pattern == 0) continue;
                        // Invoke, Select, Toggle and Expand are each the first pattern method.
                        return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(pattern, 3))(pattern) >= 0
                            ? new(true, name) : new(false, name + " refused");
                    }
                    finally { if (pattern != 0) Marshal.Release(pattern); }
                }
            }
            return new(false, "no control to activate");
        }
        finally
        {
            foreach (nint element in path) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
        }
    }

    private static readonly (int Id, string Name)[] Patterns = [(10000, "invoke"), (10010, "select"), (10015, "toggle"), (10005, "expand")];

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
