using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// A turned or mirrored view draws exactly the upright picture's pixels, moved: each pixel of the turned drawing is
/// the upright drawing's pixel that ViewTurn says it shows. Shrinking (compute and raster), 1:1 and enlarging, with
/// each scaling, in all eight orientations. A pixel-centre slip shows as a mismatch along an edge.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class PipTurnRenderingTests(ITestOutputHelper output)
{
    private static int Pattern(int x, int y) => unchecked((int)0xFF000000) |
        ((x * 31 + y * 7) % 256 << 16) | ((x * 3 + y * 23) % 256 << 8) | ((x * 11 + y * 5) % 256);

    [WgcTheory]
    [InlineData(true, PipScaling.Auto, 257, 193, 109, 79)]
    [InlineData(false, PipScaling.Auto, 257, 193, 109, 79)]
    [InlineData(true, PipScaling.Smooth, 257, 193, 109, 79)]
    [InlineData(true, PipScaling.Crisp, 257, 193, 109, 79)]
    [InlineData(true, PipScaling.Auto, 113, 83, 113, 83)]
    [InlineData(true, PipScaling.Auto, 61, 47, 150, 113)]
    [InlineData(true, PipScaling.Smooth, 61, 47, 150, 113)]
    [InlineData(true, PipScaling.Crisp, 61, 47, 150, 113)]
    public void A_turned_view_draws_the_upright_pixels_moved(bool compute, PipScaling scaling,
        int width, int height, int targetWidth, int targetHeight)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int side = Math.Max(targetWidth, targetHeight) + 4;
                using var destination = new Form { ClientSize = new(side, side), Location = new(900, 180),
                    StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, TopMost = true,
                    ShowInTaskbar = false };
                destination.Show();
                Application.DoEvents();
                using var renderer = new D3D11Renderer(destination.Handle, PipDisplay.MonitorFromWindow(destination.Handle),
                    new(), new PipDiagnostics(), false, compute) { ViewScaling = scaling };
                Load(renderer, width + 12, height + 10);
                var crop = new Rectangle(5, 3, width, height);
                int[,] upright = Draw(renderer, destination, ViewTurn.Upright, crop, new Size(targetWidth, targetHeight));
                foreach (int turns in Enumerable.Range(0, 4))
                    foreach (bool mirrored in new[] { false, true })
                    {
                        var turn = new ViewTurn(turns, mirrored);
                        Size view = turn.Oriented(new Size(targetWidth, targetHeight));
                        int[,] turned = Draw(renderer, destination, turn, crop, new Size(targetWidth, targetHeight));
                        int wrong = 0;
                        string first = "";
                        for (int y = 0; y < view.Height; y++)
                            for (int x = 0; x < view.Width; x++)
                            {
                                Point from = turn.ToUpright(new Point(x, y), view);
                                int expected = upright[from.Y + 2, from.X + 2], actual = turned[y + 2, x + 2];
                                if (expected == actual) continue;
                                if (wrong++ == 0) first = $"({x},{y}) shows {actual:X8}, upright ({from.X},{from.Y}) is {expected:X8}";
                            }
                        Assert.True(wrong == 0, $"{turn}, {scaling}, compute {compute}: {wrong} pixels differ; first {first}");
                        // Nothing drawn outside the turned picture but the view's own background.
                        Assert.Equal(turned[0, 0], turned[view.Height + 2, view.Width + 2]);
                    }
                output.WriteLine($"{width}x{height} -> {targetWidth}x{targetHeight}, {scaling}, compute {compute}: 8 orientations match");
                destination.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static unsafe void Load(D3D11Renderer renderer, int width, int height)
    {
        int[] pixels = new int[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = Pattern(x, y);
        fixed (int* data = pixels)
        {
            using var source = renderer.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource), new SubresourceData((nint)data, (uint)width * 4));
            renderer.Cache(source, new(width, height));
        }
    }

    private static int[,] Draw(D3D11Renderer renderer, Form destination, ViewTurn turn, Rectangle crop, Size upright)
    {
        renderer.Turn = turn;
        var viewport = new Rectangle(new Point(2, 2), turn.Oriented(upright));
        for (int i = 0; i < 20 && !renderer.ReadyForWork(); i++) Thread.Sleep(10);
        // A new size waits 100 ms after the last one; the client never changes here.
        Assert.True(renderer.Render(destination.ClientSize, viewport, crop, 255, null, 0, false, false));
        var target = (ID3D11RenderTargetView)typeof(D3D11Renderer).GetField("_renderTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        using var rendered = target.Resource;
        using var back = rendered.QueryInterface<ID3D11Texture2D>();
        var description = back.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = renderer.Device.CreateTexture2D(description);
        // Shared with the renderer, which draws again after this: not disposed here.
        var context = renderer.Device.ImmediateContext;
        context.CopyResource(staging, back);
        var mapped = context.Map(staging, 0, MapMode.Read);
        var pixels = new int[description.Height, description.Width];
        try
        {
            for (int y = 0; y < description.Height; y++)
                for (int x = 0; x < description.Width; x++)
                    pixels[y, x] = Marshal.ReadInt32(mapped.DataPointer, checked((int)(y * mapped.RowPitch + x * 4))) & 0xFFFFFF;
        }
        finally { context.Unmap(staging, 0); }
        return pixels;
    }
}
