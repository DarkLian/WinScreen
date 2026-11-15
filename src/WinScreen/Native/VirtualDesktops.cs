using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinScreen.Native;

/// <summary>
/// Windows' documented virtual desktop question: is this window on the desktop being shown now? Asked through its
/// COM table, made once on the thread that first asks (the UI thread). When Windows can't say, the answer is yes, so
/// nothing is hidden by mistake.
/// </summary>
internal static unsafe partial class VirtualDesktops
{
    private const int IsWindowOnCurrentVirtualDesktop = 3;
    private const int GetWindowDesktopId = 4;
    private static readonly Guid ManagerClass = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid ManagerInterface = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
    private static nint _manager;
    private static bool _tried;

    private static nint Manager()
    {
        if (!_tried)
        {
            _tried = true;
            Guid type = ManagerClass, wanted = ManagerInterface;
            nint manager;
            if (CoCreateInstance(&type, 0, 1, &wanted, &manager) == 0) _manager = manager;
        }
        return _manager;
    }

    public static bool OnCurrentDesktop(nint window)
    {
        if (Manager() == 0 || window == 0) return true;
        int on;
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)(*(nint**)_manager)[IsWindowOnCurrentVirtualDesktop])(_manager, window, &on);
        return result != 0 || on != 0;
    }

    /// <summary>The desktop a window is on (documented), or null when Windows can't say or it shows on every desktop.</summary>
    public static Guid? DesktopOf(nint window)
    {
        if (Manager() == 0 || window == 0) return null;
        Guid desktop;
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)(*(nint**)_manager)[GetWindowDesktopId])(_manager, window, &desktop);
        return result == 0 && desktop != Guid.Empty ? desktop : null;
    }

    /// <summary>
    /// The desktop being shown now, or null when it can't be read. Windows documents no way to ask, so this reads
    /// where Explorer keeps it: undocumented, read-only, and only ever an improvement over a documented fallback
    /// (INV-19). Callers check it against the documented answer before trusting it. Recheck after each Windows
    /// feature update.
    /// </summary>
    public static Guid? CurrentDesktop()
    {
        try
        {
            const string Explorer = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\";
            object? value = Registry.GetValue(Explorer + "VirtualDesktops", "CurrentVirtualDesktop", null);
            // Windows 10 keeps it per sign-in session.
            if (value is null && ProcessIdToSessionId((uint)Environment.ProcessId, out uint session))
                value = Registry.GetValue(Explorer + @"SessionInfo\" + session + @"\VirtualDesktops", "CurrentVirtualDesktop", null);
            return value is byte[] { Length: 16 } bytes && new Guid(bytes) is { } desktop && desktop != Guid.Empty ? desktop : null;
        }
        catch (Exception reading) when (reading is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a desktop still exists, or null when the list can't be read. Read from the same undocumented place
    /// as <see cref="CurrentDesktop"/>.
    /// </summary>
    public static bool? Exists(Guid desktop)
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops", "VirtualDesktopIDs", null)
                is not byte[] { Length: > 0 } list || list.Length % 16 != 0)
                return null;
            for (int at = 0; at < list.Length; at += 16)
                if (new Guid(list.AsSpan(at, 16)) == desktop) return true;
            return false;
        }
        catch (Exception reading) when (reading is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint process, out uint session);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* wanted, nint* instance);
}
