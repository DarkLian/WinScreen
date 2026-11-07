using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinScreen.Pip;

/// <summary>The three documented Win32/WinRT bridges. Each returned COM reference has exactly one owner.</summary>
internal static unsafe partial class WgcInterop
{
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string value, int length, out nint result);
    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);

    private const int CreateForWindow = 3, CreateForMonitor = 4;

    internal static GraphicsCaptureItem Item(nint hwnd) => CreateItem(hwnd, CreateForWindow);

    internal static GraphicsCaptureItem ItemForMonitor(nint monitor) => CreateItem(monitor, CreateForMonitor);

    private static GraphicsCaptureItem CreateItem(nint target, int method)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out nint name));
        nint factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name,
                new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), out factory));
            Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[method];
            Marshal.ThrowExceptionForHR(create(factory, target, &iid, &item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(name);
        }
    }

    internal static IDirect3DDevice Device(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint pointer));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    internal static ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        nint pointer = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            Guid iid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in iid, out access));
            iid = typeof(ID3D11Texture2D).GUID;
            nint texture = 0;
            var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Marshal.ThrowExceptionForHR(get(access, &iid, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            if (access != 0) Marshal.Release(access);
            Marshal.Release(pointer);
        }
    }
}
