using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// What the graphics card draws, against the same filters worked out on the processor:
/// - Auto shrinking a little, where compute shaders run, uses a cubic kept within the texels each pixel covers, so no pixel is brighter or darker
///   than those texels (no halo).
/// - Auto shrinking below two thirds averages the covered texels, and so does Smooth below half size, which would
///   otherwise skip whole texels. Sharp averages whenever it shrinks, for the same reason.
/// - 1:1 copies the texels.
/// The source is a busy pattern, the hardest case for a slip by half a pixel.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class PipScalingRenderingTests(ITestOutputHelper output)
{
    [WgcTheory]
    [InlineData(true, PipScaling.Auto, 257, 193, 211, 158)] // About 0.82: the cubic.
    [InlineData(false, PipScaling.Auto, 257, 193, 211, 158)] // Without compute shaders: the average.
    [InlineData(true, PipScaling.Auto, 257, 193, 250, 190)] // Just under 1:1.
    [InlineData(true, PipScaling.Auto, 257, 193, 140, 105)] // About 0.54: the average.
    [InlineData(false, PipScaling.Smooth, 257, 193, 100, 75)] // Under half size: the average.
    [InlineData(true, PipScaling.Crisp, 257, 193, 211, 158)] // Sharp shrinking: the average, not one texel a pixel.
    [InlineData(true, PipScaling.Crisp, 257, 193, 100, 75)]
    [InlineData(true, PipScaling.Auto, 257, 193, 257, 193)] // 1:1.
    [InlineData(false, PipScaling.Smooth, 257, 193, 257, 193)]
    public void The_view_draws_what_the_filter_says(bool compute, PipScaling scaling, int width, int height,
        int targetWidth, int targetHeight)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var destination = new Form { ClientSize = new(targetWidth + 4, targetHeight + 4), Location = new(900, 180),
                    StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, TopMost = true,
                    ShowInTaskbar = false };
                destination.Show();
                Application.DoEvents();
                using var renderer = new D3D11Renderer(destination.Handle, PipDisplay.MonitorFromWindow(destination.Handle),
                    new(), new PipDiagnostics(), false, compute) { ViewScaling = scaling };
                PipTurnRenderingTests.Load(renderer, width + 12, height + 10);
                var crop = new Rectangle(5, 3, width, height);
                int[,] drawn = PipTurnRenderingTests.Draw(renderer, destination, ViewTurn.Upright, crop, new Size(targetWidth, targetHeight));
                bool cubic = compute && scaling == PipScaling.Auto && width * 2 < targetWidth * 3 && height * 2 < targetHeight * 3;
                int worst = 0, outside = 0;
                string first = "";
                for (int y = 0; y < targetHeight; y++)
                    for (int x = 0; x < targetWidth; x++)
                    {
                        int actual = drawn[y + 2, x + 2];
                        for (int channel = 0; channel < 3; channel++)
                        {
                            (double expected, int low, int high) = Reference(crop, new Size(targetWidth, targetHeight), x, y, channel, cubic);
                            int value = actual >> (channel * 8) & 0xFF;
                            int miss = (int)Math.Ceiling(Math.Abs(value - expected) - 0.5);
                            if (miss > worst) { worst = miss; first = $"({x},{y}) channel {channel}: {value}, expected {expected:F2}"; }
                            if (value < low || value > high) outside++;
                        }
                    }
                output.WriteLine($"{width}x{height} -> {targetWidth}x{targetHeight}, {scaling}, compute {compute}, cubic {cubic}: worst miss {worst} {first}");
                Assert.True(worst <= 1, $"Off by {worst} at {first}");
                Assert.Equal(0, outside);
                destination.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static double Catrom(double t) => t < 1 ? (1.5 * t - 2.5) * t * t + 1 : t < 2 ? ((-0.5 * t + 2.5) * t - 4) * t + 2 : 0;

    // The pixel's value, and the range of the texels it covers.
    private static (double Value, int Low, int High) Reference(Rectangle crop, Size target, int x, int y, int channel, bool cubic)
    {
        double stepX = crop.Width / (double)target.Width, stepY = crop.Height / (double)target.Height;
        double loX = crop.X + x * stepX, loY = crop.Y + y * stepY;
        double hiX = Math.Min(crop.Right, loX + stepX), hiY = Math.Min(crop.Bottom, loY + stepY);
        int Texel(int tx, int ty) => PipTurnRenderingTests.Pattern(tx, ty) >> (channel * 8) & 0xFF;
        int low = 255, high = 0;
        for (int ty = (int)Math.Floor(loY); ty < (int)Math.Ceiling(hiY); ty++)
            for (int tx = (int)Math.Floor(loX); tx < (int)Math.Ceiling(hiX); tx++)
                (low, high) = (Math.Min(low, Texel(tx, ty)), Math.Max(high, Texel(tx, ty)));
        double sum = 0, total = 0;
        if (cubic)
        {
            double cx = (loX + hiX) / 2, cy = (loY + hiY) / 2;
            for (int ty = Math.Max((int)Math.Floor(cy - 2 * stepY), crop.Y); ty < Math.Min((int)Math.Ceiling(cy + 2 * stepY), crop.Bottom); ty++)
                for (int tx = Math.Max((int)Math.Floor(cx - 2 * stepX), crop.X); tx < Math.Min((int)Math.Ceiling(cx + 2 * stepX), crop.Right); tx++)
                {
                    double weight = Catrom(Math.Abs(tx + 0.5 - cx) / stepX) * Catrom(Math.Abs(ty + 0.5 - cy) / stepY);
                    sum += Texel(tx, ty) * weight;
                    total += weight;
                }
            return (Math.Clamp(sum / total, low, high), low, high);
        }
        for (int ty = (int)Math.Floor(loY); ty < (int)Math.Ceiling(hiY); ty++)
            for (int tx = (int)Math.Floor(loX); tx < (int)Math.Ceiling(hiX); tx++)
            {
                double weight = Math.Max(0, Math.Min(hiX, tx + 1) - Math.Max(loX, tx)) * Math.Max(0, Math.Min(hiY, ty + 1) - Math.Max(loY, ty));
                sum += Texel(tx, ty) * weight;
                total += weight;
            }
        return (sum / total, low, high);
    }
}
