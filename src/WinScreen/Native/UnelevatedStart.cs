using System.Runtime.InteropServices;

namespace WinScreen.Native;

// From an elevated copy: the desktop folder view's ShellExecute starts the program from Explorer, which isn't elevated
// (Windows' documented COM route). False when the desktop can't be reached (signing in, Explorer restarting). Needs a
// thread with COM set up.
internal static unsafe partial class UnelevatedStart
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid ShellWindowsInterface = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid ServiceProviderInterface = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchInterface = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid FolderViewDualInterface = new("E7A1AF80-4D96-11CF-960C-0080C7F4EE85");
    private static readonly Guid ShellDispatch2Interface = new("A4C6892C-3BA9-11D2-9DEA-00C04FB16162");

    private const uint LocalServer = 4;
    private const int DesktopClass = 8, NeedDispatch = 1;
    private const uint Background = 0;
    private const ushort VariantInt = 3, VariantText = 8;
    private const int ShowNormal = 1;

    // Places in each COM table (IUnknown 0-2, IDispatch 3-6, IOleWindow 3-4).
    private const int FindWindowSW = 15, QueryService = 3, QueryActiveShellView = 15, GetItemObject = 15, Application = 7, ShellExecute = 31;

    // A VARIANT as Windows lays it out on 64-bit: a type, padding, and 16 bytes of value.
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public nint Value, Value2;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* wanted, nint* made);

    private static void* Slot(nint table, int at) => (*(void***)table)[at];

    private static bool Query(nint table, Guid wanted, out nint result)
    {
        nint found = 0;
        int answer = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(table, 0))(table, &wanted, &found);
        result = found;
        return answer == 0 && found != 0;
    }

    private static void Release(nint table)
    {
        if (table != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(table, 2))(table);
    }

    internal static bool Run(string program, string arguments = "")
    {
        nint windows = 0, desktop = 0, provider = 0, browser = 0, view = 0, background = 0, folderView = 0, application = 0, shell = 0;
        nint file = 0, text = 0;
        try
        {
            Guid type = ShellWindowsClass, wanted = ShellWindowsInterface;
            if (CoCreateInstance(&type, 0, LocalServer, &wanted, &windows) != 0 || windows == 0) return false;
            Variant where = default, root = default;
            int found;
            if (((delegate* unmanaged[Stdcall]<nint, Variant*, Variant*, int, int*, int, nint*, int>)Slot(windows, FindWindowSW))(
                    windows, &where, &root, DesktopClass, &found, NeedDispatch, &desktop) != 0 || desktop == 0) return false;
            if (!Query(desktop, ServiceProviderInterface, out provider)) return false;
            Guid service = TopLevelBrowserService, browserInterface = ShellBrowserInterface;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(provider, QueryService))(
                    provider, &service, &browserInterface, &browser) != 0 || browser == 0) return false;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(browser, QueryActiveShellView))(browser, &view) != 0 || view == 0) return false;
            Guid dispatch = DispatchInterface;
            if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(view, GetItemObject))(
                    view, Background, &dispatch, &background) != 0 || background == 0) return false;
            if (!Query(background, FolderViewDualInterface, out folderView)) return false;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(folderView, Application))(folderView, &application) != 0 || application == 0) return false;
            if (!Query(application, ShellDispatch2Interface, out shell)) return false;
            file = Marshal.StringToBSTR(program);
            Variant given = default, folder = default, verb = default, show = new() { Type = VariantInt, Value = ShowNormal };
            if (arguments.Length > 0)
            {
                text = Marshal.StringToBSTR(arguments);
                given = new Variant { Type = VariantText, Value = text };
            }
            return ((delegate* unmanaged[Stdcall]<nint, nint, Variant, Variant, Variant, Variant, int>)Slot(shell, ShellExecute))(
                shell, file, given, folder, verb, show) == 0;
        }
        catch (Exception failure) when (failure is COMException or SEHException)
        {
            Diagnostics.Log.Swallowed(failure);
            return false;
        }
        finally
        {
            foreach (nint table in new[] { shell, application, folderView, background, view, browser, provider, desktop, windows })
                Release(table);
            if (file != 0) Marshal.FreeBSTR(file);
            if (text != 0) Marshal.FreeBSTR(text);
        }
    }
}
