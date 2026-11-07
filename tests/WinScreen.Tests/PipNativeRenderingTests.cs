using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipNativeRenderingTests(ITestOutputHelper output)
{
    private static int Pattern(int x, int y) => unchecked((int)0xFF000000) |
        ((x * 31 + y * 7) % 256 << 16) | ((x * 3 + y * 23) % 256 << 8) | ((x + y) % 2 * 255);

    [WgcTheory]
    [InlineData(true, 257, 193, 109, 79)]
    [InlineData(false, 257, 193, 109, 79)]
    [InlineData(true, 113, 83, 113, 83)]
    [InlineData(false, 113, 83, 113, 83)]
    [InlineData(true, 113, 83, 109, 79)]
    [InlineData(false, 113, 83, 109, 79)]
    [InlineData(true, 193, 257, 31, 79)]
    [InlineData(true, 1920, 1080, 576, 324)]
    [InlineData(false, 1920, 1080, 576, 324)]
    [InlineData(true, 2560, 1440, 576, 324)]
    [InlineData(false, 2560, 1440, 576, 324)]
    [InlineData(true, 3840, 2160, 576, 324)]
    [InlineData(false, 3840, 2160, 576, 324)]
    public unsafe void Native_compute_and_raster_area_match_independent_coverage_reference(bool compute,
        int width, int height, int targetWidth, int targetHeight)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var destination = new Form { ClientSize = new(targetWidth + 4, targetHeight + 4),
                    Location = new(900, 180), StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false };
                destination.Show();
                Application.DoEvents();
                var diagnostics = new PipDiagnostics();
                using var renderer = new D3D11Renderer(destination.Handle, PipDisplay.MonitorFromWindow(destination.Handle),
                    new(), diagnostics, false, compute);
                int sourceWidth = width + 12, sourceHeight = height + 10;
                int[] pixels = new int[sourceWidth * sourceHeight];
                for (int y = 0; y < sourceHeight; y++)
                    for (int x = 0; x < sourceWidth; x++) pixels[y * sourceWidth + x] = Pattern(x, y);
                fixed (int* data = pixels)
                {
                    using var source = renderer.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                        (uint)sourceWidth, (uint)sourceHeight, 1, 1, BindFlags.ShaderResource),
                        new SubresourceData((nint)data, (uint)sourceWidth * 4));
                    renderer.Cache(source, new(sourceWidth, sourceHeight));
                }
                var crop = new Rectangle(5, 3, width, height);
                var viewport = new Rectangle(2, 2, targetWidth, targetHeight);
                Assert.True(renderer.Render(destination.ClientSize, viewport, crop, 255, null, 0, false, false));
                for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(10); }
                // Diagnostic-only readback. Production frames remain GPU-to-GPU. This also excludes
                // unrelated topmost windows and desktop color management from the filter oracle.
                var renderView = (ID3D11RenderTargetView)typeof(D3D11Renderer).GetField("_renderTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                using var rendered = renderView.Resource;
                using var back = rendered.QueryInterface<ID3D11Texture2D>();
                var readDescription = back.Description;
                readDescription.Usage = ResourceUsage.Staging;
                readDescription.BindFlags = BindFlags.None;
                readDescription.CPUAccessFlags = CpuAccessFlags.Read;
                using var staging = renderer.Device.CreateTexture2D(readDescription);
                using var context = renderer.Device.ImmediateContext;
                context.CopyResource(staging, back);
                var mapped = context.Map(staging, 0, MapMode.Read);
                using var actual = new Bitmap(destination.Width, destination.Height);
                try
                {
                    for (int y = 0; y < actual.Height; y++)
                        for (int x = 0; x < actual.Width; x++)
                            actual.SetPixel(x, y, Color.FromArgb(Marshal.ReadInt32(mapped.DataPointer, checked((int)(y * mapped.RowPitch + x * 4)))));
                }
                finally { context.Unmap(staging, 0); }
                string? artifacts = Environment.GetEnvironmentVariable("WINSCREEN_PIP_EVIDENCE");
                if (artifacts is not null)
                {
                    Directory.CreateDirectory(artifacts);
                    actual.Save(Path.Combine(artifacts, $"{(compute ? "compute" : "raster")}-{width}x{height}-{targetWidth}x{targetHeight}.png"));
                }
                for (int y = 1; y < targetHeight - 1; y += Math.Max(1, targetHeight / 8))
                    for (int x = 1; x < targetWidth - 1; x += Math.Max(1, targetWidth / 8))
                    {
                        Color expected = AreaReference(crop, new(targetWidth, targetHeight), x, y);
                        Color found = actual.GetPixel(x + 2, y + 2);
                        Assert.True(Math.Abs(expected.R - found.R) <= 3 && Math.Abs(expected.G - found.G) <= 3 &&
                            Math.Abs(expected.B - found.B) <= 3, $"{compute}: ({x},{y}) expected {expected}, actual {found}");
                    }
                output.WriteLine($"Native {(compute ? "compute" : "raster")} {width}x{height} -> {targetWidth}x{targetHeight}; max allowed channel error 3/255; {diagnostics.Capabilities}");
                destination.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Color AreaReference(Rectangle crop, Size output, int x, int y)
    {
        // Independent double-precision overlap integration, including fractional edge coverage.
        double left = crop.Left + x * (double)crop.Width / output.Width;
        double top = crop.Top + y * (double)crop.Height / output.Height;
        double right = crop.Left + (x + 1) * (double)crop.Width / output.Width;
        double bottom = crop.Top + (y + 1) * (double)crop.Height / output.Height;
        double r = 0, g = 0, b = 0, weight = 0;
        for (int sy = (int)Math.Floor(top); sy < Math.Ceiling(bottom); sy++)
            for (int sx = (int)Math.Floor(left); sx < Math.Ceiling(right); sx++)
            {
                double w = (Math.Min(right, sx + 1) - Math.Max(left, sx)) *
                    (Math.Min(bottom, sy + 1) - Math.Max(top, sy));
                Color color = Color.FromArgb(Pattern(sx, sy));
                r += color.R * w; g += color.G * w; b += color.B * w; weight += w;
            }
        return Color.FromArgb((int)Math.Round(r / weight), (int)Math.Round(g / weight), (int)Math.Round(b / weight));
    }
}
