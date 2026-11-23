using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace WinScreen.Pip;

// Text in a picture, read by Windows' own text recognition, in the person's languages.
internal static class PictureText
{
    internal sealed class NoReaderException() : Exception("No text recognition for the person's languages.");

    // Empty when there's no text. Throws NoReaderException when Windows has no recognizer for the person's languages.
    internal static async Task<string> Read(Bitmap picture)
    {
        OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
            ?? throw new NoReaderException();
        string plain = await ReadWith(engine, picture, contrast: false).ConfigureAwait(false);
        if (Letters(plain) >= 3) return plain;
        // A busy background, such as a video behind Glass: grey and steeper contrast find more.
        string steep = await ReadWith(engine, picture, contrast: true).ConfigureAwait(false);
        return Letters(steep) > Letters(plain) ? steep : plain;
    }

    private static int Letters(string text) => text.Count(char.IsLetterOrDigit);

    private static async Task<string> ReadWith(OcrEngine engine, Bitmap picture, bool contrast)
    {
        using SoftwareBitmap bitmap = Prepared(picture, contrast, (int)OcrEngine.MaxImageDimension);
        OcrResult result = await engine.RecognizeAsync(bitmap);
        return Joined(result.Lines.Select(line => line.Words.Select(word => word.Text)));
    }

    // Small text reads better larger. Never past what the recognizer takes.
    internal static Size ReadingSize(Size picture, int most)
    {
        double scale = Math.Max(picture.Width, picture.Height) < 1200 ? 2 : 1;
        scale = Math.Min(scale, most / (double)Math.Max(picture.Width, picture.Height));
        return new Size(Math.Max(1, (int)(picture.Width * scale)), Math.Max(1, (int)(picture.Height * scale)));
    }

    private static SoftwareBitmap Prepared(Bitmap picture, bool contrast, int most)
    {
        Size size = ReadingSize(picture.Size, most);
        using var ready = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(ready))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var look = new ImageAttributes();
            if (contrast)
            {
                const float k = 1.8f, grey = k / 3;
                look.SetColorMatrix(new ColorMatrix(
                [
                    [grey, grey, grey, 0, 0], [grey, grey, grey, 0, 0], [grey, grey, grey, 0, 0], [0, 0, 0, 1, 0],
                    [-0.4f, -0.4f, -0.4f, 0, 1],
                ]));
            }
            look.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(picture, new Rectangle(Point.Empty, size), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, look);
        }
        BitmapData bits = ready.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[size.Width * 4 * size.Height];
            for (int y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, bytes, y * size.Width * 4, size.Width * 4);
            return SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes), BitmapPixelFormat.Bgra8,
                size.Width, size.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { ready.UnlockBits(bits); }
    }

    // Words with a space between them, except between Chinese or Japanese characters, which are written without.
    internal static string Joined(IEnumerable<IEnumerable<string>> lines)
    {
        var text = new StringBuilder();
        foreach (IEnumerable<string> line in lines)
        {
            if (text.Length > 0) text.AppendLine();
            string? before = null;
            foreach (string word in line)
            {
                if (word.Length == 0) continue;
                if (before is not null && !(WrittenTogether(before[^1]) && WrittenTogether(word[0]))) text.Append(' ');
                text.Append(word);
                before = word;
            }
        }
        return text.ToString();
    }

    private static bool WrittenTogether(char letter) =>
        letter is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}
