using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// Putting another program's window on a given virtual desktop. Windows' documented call does this only for a
/// program's own windows (it refuses others with "access denied"), so this uses Explorer's undocumented interface,
/// as the owner decided (15 Nov). Only an improvement over the documented behaviour, never the only way (INV-19): an
/// interface version not in the table below, or any failure, leaves the window where Windows put it, and nothing
/// throws. Each move is checked: the desktop found must report the id asked for, and afterwards the documented
/// question must say the window is there. Recheck after each Windows feature update; a new version gets a new line.
/// </summary>
internal static unsafe partial class VirtualDesktopsInternal
{
    /// <summary>
    /// The versions of the manager interface, newest first. Windows changes the id whenever the layout changes, so an id
    /// that answers says which slots to call. Asking for an id Windows doesn't have is simply refused. Only a version
    /// someone has called on a real Windows is used: a wrong slot is a call into the shell with the wrong arguments,
    /// which nothing can catch. The others are kept for when they are measured (roadmap 12.5, TD-61); until then those
    /// Windows versions get the documented behaviour (INV-19).
    /// </summary>
    internal static readonly (Guid Manager, int FindDesktop, string Windows, bool Measured)[] Known =
    [
        (new("53F5CA0B-158F-4124-900C-057158060B27"), 14, "Windows 11 24H2 and later (measured on 26300)", true),
        (new("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"), 14, "Windows 11 22H2/23H2", false),
        (new("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10"), 13, "Windows 11 21H2", false),
        (new("F31574D6-B682-4CDC-BD56-1827860ABEC6"), 12, "Windows 10", false),
    ];

    private const int MoveViewToDesktop = 4; // The same slot in every version above.
    private const int GetViewForHwnd = 6; // In the view collection, unchanged across versions.
    private const int GetId = 4; // In a desktop, after IsViewVisible.
    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid ManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid ViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    private static nint _manager, _views;
    private static int _findSlot;
    private static bool _tried;

    /// <summary>Whether this Windows has a version in the table, asked once.</summary>
    internal static bool Supported => Prepare();

    private static bool Prepare()
    {
        if (_tried) return _manager != 0 && _views != 0;
        _tried = true;
        try
        {
            Guid type = ImmersiveShell, wanted = ServiceProvider;
            nint provider;
            if (CoCreateInstance(&type, 0, 4, &wanted, &provider) != 0 || provider == 0) return Unsupported("no shell");
            try
            {
                var query = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)(*(nint**)provider)[3];
                foreach ((Guid id, int find, _, bool measured) in Known)
                {
                    if (!measured) continue;
                    Guid service = ManagerService, iid = id;
                    nint manager;
                    if (query(provider, &service, &iid, &manager) == 0 && manager != 0)
                    {
                        _manager = manager;
                        _findSlot = find;
                        break;
                    }
                }
                Guid collection = ViewCollection;
                nint views;
                if (_manager != 0 && query(provider, &collection, &collection, &views) == 0 && views != 0) _views = views;
            }
            finally { Marshal.Release(provider); }
        }
        catch (Exception failure) when (failure is COMException or InvalidCastException)
        {
            Diagnostics.Log.Swallowed(failure);
        }
        return _manager != 0 && _views != 0 || Unsupported("unknown version");
    }

    private static bool Unsupported(string why)
    {
        Diagnostics.Log.Event("desktops.unsupported", ("build", Environment.OSVersion.Version.Build), ("noShell", why == "no shell"));
        return false;
    }

    /// <summary>
    /// Puts the window on the desktop, and says whether it is there now (by Windows' documented answer). A window
    /// already there is left alone.
    /// </summary>
    internal static bool MoveTo(nint window, Guid desktop)
    {
        if (window == 0 || desktop == Guid.Empty) return false;
        if (VirtualDesktops.DesktopOf(window) == desktop) return true;
        if (!Prepare()) return false;
        nint found = 0, view = 0;
        try
        {
            Guid wanted = desktop;
            var find = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)_manager)[_findSlot];
            if (find(_manager, &wanted, &found) != 0 || found == 0) return false;
            Guid reported;
            var id = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)(*(nint**)found)[GetId];
            if (id(found, &reported) != 0 || reported != desktop) return Mismatch();
            var forWindow = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)_views)[GetViewForHwnd];
            if (forWindow(_views, window, &view) != 0 || view == 0) return false;
            var move = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)(*(nint**)_manager)[MoveViewToDesktop];
            int result = move(_manager, view, found);
            bool there = result == 0 && VirtualDesktops.DesktopOf(window) == desktop;
            Diagnostics.Log.Event("desktops.moved", ("ok", there));
            return there;
        }
        finally
        {
            if (view != 0) Marshal.Release(view);
            if (found != 0) Marshal.Release(found);
        }
    }

    /// <summary>The interface answered but its desktop reported another id: its layout isn't the one expected. Not used again.</summary>
    private static bool Mismatch()
    {
        Diagnostics.Log.Event("desktops.unsupported", ("build", Environment.OSVersion.Version.Build), ("noShell", false));
        _manager = 0;
        _views = 0;
        return false;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* wanted, nint* instance);
}
