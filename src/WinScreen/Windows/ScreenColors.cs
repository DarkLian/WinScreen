using WinScreen.Localization;

namespace WinScreen.Windows;

public static class ScreenColors
{
    private const int Step = 8;

    internal static double Brightness(Color color) =>
        (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255;

    public static Color MostCommon(Rectangle area)
    {
        try
        {
            using var bitmap = new Bitmap(area.Width, area.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);

            var counts = new Dictionary<int, int>();
            System.Drawing.Imaging.BitmapData bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < bitmap.Height; y += Step)
                    {
                        int* row = (int*)((byte*)bits.Scan0 + (long)y * bits.Stride);
                        for (int x = 0; x < bitmap.Width; x += Step)
                        {
                            int color = row[x];
                            counts[color] = counts.GetValueOrDefault(color) + 1;
                        }
                    }
                }
            }
            finally { bitmap.UnlockBits(bits); }

            return Color.FromArgb(counts.MaxBy(pair => pair.Value).Key);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException
                                      or InvalidOperationException)
        {
            throw new WindowOperationException(
                string.Format(UiText.T("errors.glass.sampleFailed"), e.Message));
        }
    }
}
