using WinScreen.Localization;

namespace WinScreen.Windows;

/// <summary>Finds a window's background colour from what is on screen.</summary>
public static class ScreenColors
{
    private const int Step = 8;

    public static Color MostCommon(Rectangle area)
    {
        try
        {
            using var bitmap = new Bitmap(area.Width, area.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);

            var counts = new Dictionary<int, int>();
            for (int y = 0; y < bitmap.Height; y += Step)
            {
                for (int x = 0; x < bitmap.Width; x += Step)
                {
                    int color = bitmap.GetPixel(x, y).ToArgb();
                    counts[color] = counts.GetValueOrDefault(color) + 1;
                }
            }

            return Color.FromArgb(counts.MaxBy(pair => pair.Value).Key);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException
                                      or InvalidOperationException)
        {
            throw new WindowOperationException(
                UiText.T("errors.glass.sampleFailedPrefix") + e.Message);
        }
    }
}
