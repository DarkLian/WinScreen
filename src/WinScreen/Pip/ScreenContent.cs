using System.Drawing.Imaging;

namespace WinScreen.Pip;

/// <summary>Tells which parts of the screen hold text or images and which are plain background.</summary>
internal static class ScreenContent
{
    private const int Contrast = 48;
    private static readonly Dictionary<Rectangle, (long Time, bool[,] Cells)> Cache = [];

    public static bool[,] BusyCells(Rectangle area)
    {
        long now = Environment.TickCount64;
        if (Cache.TryGetValue(area, out var cached) && now - cached.Time < 1000)
            return cached.Cells;
        if (Cache.Count > 8)
            Cache.Clear();
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bool[,] cells = BusyCells(bitmap);
        Cache[area] = (now, cells);
        return cells;
    }

    /// <summary>
    /// A cell is busy when light and dark meet in it. Plain backgrounds and soft gradients don't count,
    /// so wallpaper colours and empty pages stay calm while text and photos don't.
    /// </summary>
    public static unsafe bool[,] BusyCells(Bitmap bitmap)
    {
        int cell = PipGeometry.ContentCell;
        var busy = new bool[bitmap.Height / cell, bitmap.Width / cell];
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < busy.GetLength(0); row++)
            {
                for (int column = 0; column < busy.GetLength(1); column++)
                {
                    int lowest = 255, highest = 0;
                    for (int y = row * cell; y < (row + 1) * cell; y += 2)
                    {
                        byte* line = (byte*)data.Scan0 + y * data.Stride;
                        for (int x = column * cell; x < (column + 1) * cell; x += 2)
                        {
                            byte* pixel = line + x * 4;
                            int brightness = (pixel[2] * 3 + pixel[1] * 6 + pixel[0]) / 10;
                            lowest = Math.Min(lowest, brightness);
                            highest = Math.Max(highest, brightness);
                        }
                    }

                    busy[row, column] = highest - lowest >= Contrast;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return busy;
    }
}
