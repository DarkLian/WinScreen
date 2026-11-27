using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinScreen.Pip;
using WinScreen.Settings;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// A Smooth view bigger than what it shows. Auto enlarges along edges and sharpens within the nearest texels; Smooth
/// blends linearly; Sharp keeps the nearest texel. Measured against the picture the small one was made from.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class PipEnlargeTests(ITestOutputHelper output)
{
    private const int Factor = 3, SmallWidth = 128, SmallHeight = 96;
    private const int LargeWidth = SmallWidth * Factor, LargeHeight = SmallHeight * Factor;

    /// <summary>Text, a circle and slanted lines, drawn smooth at the large size.</summary>
    private static int[] Original()
    {
        using var picture = new Bitmap(LargeWidth, LargeHeight);
        using (var g = Graphics.FromImage(picture))
        {
            g.Clear(Color.FromArgb(245, 245, 240));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI", 27, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("Sharp text, 0123", font, Brushes.Black, 12, 10);
            using var pen = new Pen(Color.FromArgb(30, 60, 160), 4);
            g.DrawEllipse(pen, 30, 70, 150, 150);
            for (int i = 0; i < 6; i++)
                g.DrawLine(new Pen(Color.FromArgb(180, 40, 40), 3), 210 + i * 26, 270, 250 + i * 22, 80);
        }
        int[] pixels = new int[LargeWidth * LargeHeight];
        for (int y = 0; y < LargeHeight; y++)
            for (int x = 0; x < LargeWidth; x++) pixels[y * LargeWidth + x] = picture.GetPixel(x, y).ToArgb();
        return pixels;
    }

    /// <summary>The large picture shrunk by an exact average of each three-by-three block.</summary>
    private static int[] Shrunk(int[] large)
    {
        int[] small = new int[SmallWidth * SmallHeight];
        for (int y = 0; y < SmallHeight; y++)
            for (int x = 0; x < SmallWidth; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < Factor; dy++)
                    for (int dx = 0; dx < Factor; dx++)
                    {
                        Color c = Color.FromArgb(large[(y * Factor + dy) * LargeWidth + x * Factor + dx]);
                        r += c.R; g += c.G; b += c.B;
                    }
                const int n = Factor * Factor;
                small[y * SmallWidth + x] = Color.FromArgb((r + n / 2) / n, (g + n / 2) / n, (b + n / 2) / n).ToArgb();
            }
        return small;
    }

    /// <summary>The small picture enlarged by the renderer with this scaling, read back from its own buffer.</summary>
    private static unsafe int[] Enlarged(int[] small, PipScaling scaling)
    {
        int[] result = [];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var destination = new Form { ClientSize = new(LargeWidth + 4, LargeHeight + 4),
                    Location = new(900, 180), StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false };
                destination.Show();
                Application.DoEvents();
                using var renderer = new D3D11Renderer(destination.Handle, WinScreen.Native.User32.MonitorFromWindow(destination.Handle),
                    new PipPreferences { Scaling = scaling }, new PipDiagnostics(), false);
                fixed (int* data = small)
                {
                    using var source = renderer.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                        SmallWidth, SmallHeight, 1, 1, BindFlags.ShaderResource), new SubresourceData((nint)data, SmallWidth * 4));
                    renderer.Cache(source, new(SmallWidth, SmallHeight));
                }
                Assert.True(renderer.Render(destination.ClientSize, new Rectangle(2, 2, LargeWidth, LargeHeight),
                    new Rectangle(0, 0, SmallWidth, SmallHeight), 255, null, 0, false, false));
                var renderView = (ID3D11RenderTargetView)typeof(D3D11Renderer).GetField("_renderTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                using var rendered = renderView.Resource;
                using var back = rendered.QueryInterface<ID3D11Texture2D>();
                var description = back.Description;
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                using var staging = renderer.Device.CreateTexture2D(description);
                using var context = renderer.Device.ImmediateContext;
                context.CopyResource(staging, back);
                var mapped = context.Map(staging, 0, MapMode.Read);
                result = new int[LargeWidth * LargeHeight];
                try
                {
                    for (int y = 0; y < LargeHeight; y++)
                        for (int x = 0; x < LargeWidth; x++)
                            result[y * LargeWidth + x] = Marshal.ReadInt32(mapped.DataPointer, checked((int)((y + 2) * mapped.RowPitch + (x + 2) * 4)));
                }
                finally { context.Unmap(staging, 0); }
                destination.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return result;
    }

    /// <summary>The mean difference per channel, in 0 to 255, over the whole picture.</summary>
    private static double Error(int[] expected, int[] actual)
    {
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Color e = Color.FromArgb(expected[i]), a = Color.FromArgb(actual[i]);
            sum += Math.Abs(e.R - a.R) + Math.Abs(e.G - a.G) + Math.Abs(e.B - a.B);
        }
        return sum / (expected.Length * 3.0);
    }

    private static void Save(int[] pixels, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("WINSCREEN_PIP_EVIDENCE");
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        using var picture = new Bitmap(LargeWidth, LargeHeight);
        for (int y = 0; y < LargeHeight; y++)
            for (int x = 0; x < LargeWidth; x++) picture.SetPixel(x, y, Color.FromArgb(pixels[y * LargeWidth + x]));
        picture.Save(Path.Combine(folder, name + ".png"));
    }

    [Fact]
    public void Auto_enlarges_closer_to_the_original_than_the_linear_blend_and_sharp_keeps_whole_texels()
    {
        int[] original = Original(), small = Shrunk(original);
        int[] auto = Enlarged(small, PipScaling.Auto), smooth = Enlarged(small, PipScaling.Smooth),
            sharp = Enlarged(small, PipScaling.Crisp);
        Save(original, "enlarge-original");
        Save(auto, "enlarge-auto");
        Save(smooth, "enlarge-smooth");
        Save(sharp, "enlarge-sharp");
        double autoError = Error(original, auto), smoothError = Error(original, smooth), sharpError = Error(original, sharp);
        output.WriteLine($"Mean error against the original, per channel: Auto {autoError:F2}, Smooth {smoothError:F2}, Sharp {sharpError:F2}.");
        Assert.True(autoError < smoothError * 0.92,
            $"Auto ({autoError:F2}) isn't clearly closer to the original than the linear blend ({smoothError:F2}).");
        Assert.True(autoError < sharpError, $"Auto ({autoError:F2}) is further from the original than whole texels ({sharpError:F2}).");

        // Sharp: every enlarged pixel is the small picture's texel under it, exactly.
        for (int y = 0; y < LargeHeight; y++)
            for (int x = 0; x < LargeWidth; x++)
                Assert.True(sharp[y * LargeWidth + x] == small[y / Factor * SmallWidth + x / Factor],
                    $"Sharp at ({x},{y}) isn't the texel under it.");

        // No halos: Auto never goes outside the nearest four texels' range by more than rounding.
        for (int y = Factor; y < LargeHeight - Factor; y += 2)
            for (int x = Factor; x < LargeWidth - Factor; x += 2)
            {
                double sx = (x + 0.5) / Factor - 0.5, sy = (y + 0.5) / Factor - 0.5;
                // On a texel's edge the card's rounding may pick either side: both sides' texels count.
                int x0 = (int)Math.Floor(sx - 0.001), x1 = (int)Math.Floor(sx + 0.001) + 1;
                int y0 = (int)Math.Floor(sy - 0.001), y1 = (int)Math.Floor(sy + 0.001) + 1;
                var near = new List<Color>();
                for (int ny = y0; ny <= y1; ny++)
                    for (int nx = x0; nx <= x1; nx++) near.Add(Color.FromArgb(small[ny * SmallWidth + nx]));
                Color found = Color.FromArgb(auto[y * LargeWidth + x]);
                Assert.True(found.R >= near.Min(c => c.R) - 2 && found.R <= near.Max(c => c.R) + 2 &&
                            found.G >= near.Min(c => c.G) - 2 && found.G <= near.Max(c => c.G) + 2 &&
                            found.B >= near.Min(c => c.B) - 2 && found.B <= near.Max(c => c.B) + 2,
                    $"Auto at ({x},{y}) is {found}, outside its nearest texels.");
            }
    }
}
