using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// Windows' documented virtual desktop question: is this window on the desktop being shown now? Asked through its
/// COM table, made once on the thread that first asks (the UI thread). When Windows can't say, the answer is yes, so
/// nothing is hidden by mistake.
/// </summary>
internal static unsafe partial class VirtualDesktops
{
    private const int IsWindowOnCurrentVirtualDesktop = 3;
    private static readonly Guid ManagerClass = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid ManagerInterface = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
    private static nint _manager;
    private static bool _tried;

    public static bool OnCurrentDesktop(nint window)
    {
        if (!_tried)
        {
            _tried = true;
            Guid type = ManagerClass, wanted = ManagerInterface;
            nint manager;
            if (CoCreateInstance(&type, 0, 1, &wanted, &manager) == 0) _manager = manager;
        }
        if (_manager == 0 || window == 0) return true;
        int on;
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)(*(nint**)_manager)[IsWindowOnCurrentVirtualDesktop])(_manager, window, &on);
        return result != 0 || on != 0;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* wanted, nint* instance);
}
