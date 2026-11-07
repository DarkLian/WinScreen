using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Xunit;

namespace WinScreen.Tests.Support;

internal static class WgcTestSupport
{
    internal static string? SkipReason()
    {
        try
        {
            return ApiInformation.IsMethodPresent("Windows.Graphics.Capture.Direct3D11CaptureFramePool", "CreateFreeThreaded") &&
                GraphicsCaptureSession.IsSupported() ? null : "Windows Graphics Capture is not supported on this desktop.";
        }
        catch (Exception e) { return $"Windows Graphics Capture probe unavailable: {e.GetType().Name} 0x{e.HResult:X8}"; }
    }
}

public sealed class WgcFactAttribute : FactAttribute
{
    public WgcFactAttribute() => Skip = WgcTestSupport.SkipReason();
}

public sealed class WgcTheoryAttribute : TheoryAttribute
{
    public WgcTheoryAttribute() => Skip = WgcTestSupport.SkipReason();
}
