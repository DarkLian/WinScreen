using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

/// <summary>Activates WinUI island controls, which do not consume posted legacy mouse messages.</summary>
internal sealed class PipControlInput(WindowIdentity identity) : IDisposable
{
    private readonly Channel<(nint Host, Point At)> _requests = Channel.CreateBounded<(nint, Point)>(16);
    private int _started;
    private volatile bool _stopped;

    internal bool Click(nint host, Point at)
    {
        if (_stopped || !_requests.Writer.TryWrite((host, at))) return false;
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(Run);
        return true;
    }

    private async Task Run()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync())
        {
            if (_stopped) continue;
            try { Activate(request.Host, request.At); }
            catch (COMException) { } // Controls can disappear between the press and activation.
        }
    }

    private bool Valid(nint host)
    {
        User32.GetWindowThreadProcessId(host, out uint process);
        return !_stopped && identity.StillThere() && process == identity.ProcessId &&
               User32.GetAncestor(host, User32.GA_ROOT) == identity.Window;
    }

    private unsafe void Activate(nint host, Point at)
    {
        if (!Valid(host)) return;
        nint automation = 0, walker = 0, root = 0;
        var path = new List<nint>();
        Guid implementation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), wanted = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        try
        {
            if (CoCreateInstance(in implementation, 0, 1, in wanted, out automation) < 0 ||
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6))(automation, host, &root) < 0 || root == 0) return;
            path.Add(root);
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker) < 0 || walker == 0) return;
            for (int depth = 0; depth < 24; depth++)
            {
                nint child = ChildAt(walker, path[^1], at);
                if (child == 0) break;
                path.Add(child);
            }
            // Icons and text can sit inside an actionable button; walk back to that button.
            for (int i = path.Count - 1; i >= 0 && Valid(host); i--)
            {
                nint element = path[i];
                int enabled = 0, type = 0, process = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 20))(element, &process) < 0 || process != identity.ProcessId ||
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 28))(element, &enabled) < 0 || enabled == 0) return;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 21))(element, &type);
                if (type == 50004) // Edit controls need keyboard focus, without changing their value.
                {
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(element, 3))(element);
                    return;
                }
                foreach (int patternId in new[] { 10000, 10010, 10015, 10005 })
                {
                    nint pattern = 0;
                    try
                    {
                        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(element, 16))(element, patternId, &pattern) < 0 || pattern == 0) continue;
                        if (Valid(host)) ((delegate* unmanaged[Stdcall]<nint, int>)Slot(pattern, 3))(pattern);
                        return; // Invoke, Select, Toggle and Expand are each the first pattern method.
                    }
                    finally { if (pattern != 0) Marshal.Release(pattern); }
                }
            }
        }
        finally
        {
            foreach (nint element in path) Marshal.Release(element);
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
        }
    }

    private static unsafe nint ChildAt(nint walker, nint parent, Point at)
    {
        nint child = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(walker, 4))(walker, parent, &child);
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

    public void Dispose()
    {
        _stopped = true;
        _requests.Writer.TryComplete();
    }

    private static unsafe nint Slot(nint com, int index) => (*(nint**)com)[index];
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid cls, nint outer, uint context, in Guid iid, out nint instance);
}
