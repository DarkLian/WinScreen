using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// The taskbar's own list of buttons. Takes a window's button off, or puts it back, without changing the window
/// itself, so its title bar and caption buttons stay as its app draws them.
/// </summary>
internal static unsafe class TaskbarList
{
    private static readonly Guid Class = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid Interface = new("56FDF342-FD6D-11d0-958A-006097C9A090");
    private const int HrInit = 3, AddTab = 4, DeleteTab = 5; // ITaskbarList, after IUnknown.
    private const int Patience = 2000;

    internal static bool Remove(nint window) => Call(window, DeleteTab);

    internal static bool Add(nint window) => Call(window, AddTab);

    /// <summary>
    /// On a thread of its own, set up for COM whatever thread asks, and never waiting long on a busy taskbar.
    /// </summary>
    private static bool Call(nint window, int slot)
    {
        bool done = false;
        var worker = new Thread(() =>
        {
            if (CoCreateInstance(Class, 0, 1, Interface, out nint list) < 0 || list == 0) return;
            try
            {
                nint* table = *(nint**)list;
                if (((delegate* unmanaged[Stdcall]<nint, int>)table[HrInit])(list) < 0) return;
                done = ((delegate* unmanaged[Stdcall]<nint, nint, int>)table[slot])(list, window) >= 0;
            }
            finally { Marshal.Release(list); }
        }) { IsBackground = true, Name = "WinScreen taskbar" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return worker.Join(Patience) && done;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid type, nint outer, uint context, in Guid iid, out nint value);
}
