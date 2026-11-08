using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Tests.Support;

// Test-only UIA traversal: coordinates come from the browser, including its DPI and frame layout.
internal static unsafe class BrowserAccessibility
{
    internal sealed record Control(string Name, int Type, Rectangle Bounds, bool Focused, bool Enabled);
    internal static List<Control> Read(nint window)
    {
        nint automation = 0, walker = 0, root = 0;
        Guid cls = new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), iid = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        var controls = new List<Control>();
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(in cls, 0, 1, in iid, out automation));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6))(automation, window, &root));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(automation, 14))(automation, &walker));
            Walk(root, 0, walker);
            return controls;
        }
        finally
        {
            if (root != 0) Marshal.Release(root);
            if (walker != 0) Marshal.Release(walker);
            if (automation != 0) Marshal.Release(automation);
        }
        void Walk(nint element, int depth, nint treeWalker)
        {
            if (depth > 16 || controls.Count > 1000) return;
            nint name = 0;
            int type = 0, focus = 0, enabled = 0;
            Rect bounds = default;
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, 23))(element, &name);
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 21))(element, &type);
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 26))(element, &focus);
            ((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(element, 43))(element, &bounds);
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, 28))(element, &enabled);
            controls.Add(new(name == 0 ? "" : Marshal.PtrToStringBSTR(name), type,
                Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), focus != 0, enabled != 0));
            if (name != 0) Marshal.FreeBSTR(name);
            // Browser controls only; do not walk the page's accessibility tree.
            if (type == 50030) return;
            nint child = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(treeWalker, 4))(treeWalker, element, &child);
            for (int count = 0; child != 0 && count < 300; count++)
            {
                nint next = 0;
                try
                {
                    Walk(child, depth + 1, treeWalker);
                    ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(treeWalker, 6))(treeWalker, child, &next);
                }
                finally { Marshal.Release(child); }
                child = next;
            }
            if (child != 0) Marshal.Release(child);
        }
    }
    private static nint Slot(nint com, int index) => (*(nint**)com)[index];
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid cls, nint outer, uint context, in Guid iid, out nint instance);
}
