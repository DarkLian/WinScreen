using Vortice.DXGI;
using Xunit;
using static Vortice.DXGI.DXGI;

namespace WinScreen.Tests.Support;

/// <summary>For effects that need a graphics card. Skipped where Windows has only its software renderer, as in most VMs.</summary>
public sealed class GraphicsCardFactAttribute : FactAttribute
{
    public GraphicsCardFactAttribute()
    {
        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(0, out IDXGIAdapter1? adapter).CheckError();
            using (adapter)
                if (adapter!.Description1.VendorId == Glass.ContentOverlay.SoftwareRendererVendor)
                    Skip = "This PC has no graphics card, only Windows' software renderer.";
        }
        catch (Exception e) { Skip = $"Couldn't check for a graphics card: {e.GetType().Name} 0x{e.HResult:X8}"; }
    }
}
