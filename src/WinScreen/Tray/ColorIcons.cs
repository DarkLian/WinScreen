using System.Drawing.Drawing2D;

namespace WinScreen.Tray;

/// <summary>
/// WinScreen's coloured icons, for its menus, toolbar and list of pages: filled shapes in a few families of colour
/// with a soft light-to-dark shading, in the spirit of Windows 11's own. Each is drawn at four times its size on a
/// grid of 16, then scaled down, so its edges are smooth at any display scaling.
/// </summary>
internal static class ColorIcons
{
    internal const char Effects = '', DarkMode = '', Glass = '', Shrink = '', Move = '',
        Plus = '', Minus = '';

    private static readonly Dictionary<(char, int), Image> Made = [];

    private static readonly (Color Top, Color Bottom) Blue = (Rgb(0x6CCBFF), Rgb(0x0F6CBD)), DeepBlue = (Rgb(0x3A9BE6), Rgb(0x10529F)),
        Teal = (Rgb(0x5FE3D0), Rgb(0x00968C)), Grey = (Rgb(0xE2E4E8), Rgb(0x868B93)), Mid = (Rgb(0xB4B9C1), Rgb(0x6A6F78)), Slate = (Rgb(0x7A7F88), Rgb(0x3C4048)),
        Orange = (Rgb(0xFFB25A), Rgb(0xE5641A)), Yellow = (Rgb(0xFFE07A), Rgb(0xF0A818)), Red = (Rgb(0xFF7A70), Rgb(0xC62E2E)),
        Green = (Rgb(0x72E39A), Rgb(0x16934A)), Purple = (Rgb(0xC8A2FF), Rgb(0x7B3FD6)), White = (Rgb(0xFFFFFF), Rgb(0xE4E8EE)),
        Paper = (Rgb(0xFFFFFF), Rgb(0xDADFE6));

    private static Color Rgb(int value) => Color.FromArgb(value >> 16 & 0xFF, value >> 8 & 0xFF, value & 0xFF);

    /// <summary>Whether there is a coloured drawing for <paramref name="glyph"/>.</summary>
    internal static bool Has(char glyph) => Drawings.ContainsKey(glyph);

    /// <summary>The icon for <paramref name="glyph"/> at <paramref name="size"/> pixels, made once.</summary>
    internal static Image? Of(char glyph, int size)
    {
        if (!Drawings.TryGetValue(glyph, out Action<Painter>? draw)) return null;
        if (Made.TryGetValue((glyph, size), out Image? made)) return made;
        int big = size * 4;
        using var large = new Bitmap(big, big);
        using (Graphics g = Graphics.FromImage(large))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            draw(new Painter(g, big / 16f));
        }
        var picture = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(large, new Rectangle(0, 0, size, size));
        }
        return Made[(glyph, size)] = picture;
    }

    /// <summary>Draws on the grid of 16: every length is in sixteenths of the icon.</summary>
    private sealed class Painter(Graphics g, float unit)
    {
        private RectangleF R(float x, float y, float w, float h) => new(x * unit, y * unit, w * unit, h * unit);
        private PointF P(float x, float y) => new(x * unit, y * unit);

        private void Fill(GraphicsPath path, (Color Top, Color Bottom) colours)
        {
            RectangleF bounds = path.GetBounds();
            if (bounds.Height < 1) bounds.Height = 1;
            using var brush = new LinearGradientBrush(new RectangleF(bounds.X, bounds.Y - 0.5f, Math.Max(1, bounds.Width), bounds.Height + 1),
                colours.Top, colours.Bottom, LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        internal static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.01f, radius * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal void Box(float x, float y, float w, float h, float radius, (Color, Color) colours)
        {
            using GraphicsPath path = RoundRect(R(x, y, w, h), radius * unit);
            Fill(path, colours);
        }

        internal void Circle(float x, float y, float d, (Color, Color) colours)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(R(x, y, d, d));
            Fill(path, colours);
        }

        internal void Shape((Color, Color) colours, params float[] points)
        {
            using var path = new GraphicsPath();
            path.AddPolygon(Enumerable.Range(0, points.Length / 2).Select(i => P(points[i * 2], points[i * 2 + 1])).ToArray());
            Fill(path, colours);
        }

        internal void Line(Color colour, float width, params float[] points)
        {
            using var pen = new Pen(colour, width * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, Enumerable.Range(0, points.Length / 2).Select(i => P(points[i * 2], points[i * 2 + 1])).ToArray());
        }

        internal void Arc(Color colour, float width, float x, float y, float d, float start, float sweep)
        {
            using var pen = new Pen(colour, width * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, R(x, y, d, d), start, sweep);
        }

        internal void Ring(Color colour, float width, float x, float y, float d)
        {
            using var pen = new Pen(colour, width * unit);
            g.DrawEllipse(pen, R(x, y, d, d));
        }

        /// <summary>Takes a round bite out of what is drawn so far.</summary>
        internal void Cut(float x, float y, float d)
        {
            CompositingMode mode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.Transparent);
            g.FillEllipse(clear, R(x, y, d, d));
            g.CompositingMode = mode;
        }

        internal void Dot(Color colour, float x, float y, float d)
        {
            using var brush = new SolidBrush(colour);
            g.FillEllipse(brush, R(x - d / 2, y - d / 2, d, d));
        }

        internal void Turned(float degrees, Action draw)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(8 * unit, 8 * unit);
            g.RotateTransform(degrees);
            g.TranslateTransform(-8 * unit, -8 * unit);
            draw();
            g.Restore(state);
        }

        /// <summary>A sign drawn in a font, such as a question mark, filled white in the middle of the icon.</summary>
        internal void Sign(string text, float height, Color colour)
        {
            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(text, family, (int)FontStyle.Bold, height * unit, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF ink = path.GetBounds();
            using var move = new Matrix();
            move.Translate(8 * unit - ink.Width / 2 - ink.Left, 8 * unit - ink.Height / 2 - ink.Top);
            path.Transform(move);
            using var brush = new SolidBrush(colour);
            g.FillPath(brush, path);
        }
    }

    private static readonly Color Ink = Rgb(0x2B2F36), Light = Color.White;

    private static readonly Dictionary<char, Action<Painter>> Drawings = new()
    {
        // Pin on top: a pushpin, leaning.
        [MenuParts.Pin] = p => p.Turned(30, () =>
        {
            p.Line(Rgb(0x8A8F98), 1.1f, 8, 10, 8, 15);
            p.Box(5, 1.2f, 6, 2.8f, 1, Blue);
            p.Shape(Blue, 6, 3.8f, 10, 3.8f, 11, 8.4f, 5, 8.4f);
            p.Box(3.4f, 8, 9.2f, 2.2f, 1, DeepBlue);
        }),
        // An Anchor: a round badge holding a small window.
        [MenuParts.Pinned] = p =>
        {
            p.Circle(1, 1, 14, Orange);
            p.Box(4.4f, 5, 7.2f, 6.2f, 1, White);
            p.Box(4.4f, 5, 7.2f, 1.8f, 0.8f, (Rgb(0xFFD3A6), Rgb(0xFFC089)));
        },
        // Click-through: a pointer passing over a pane.
        [MenuParts.Pointer] = p =>
        {
            p.Box(1, 1.5f, 10.5f, 10.5f, 1.6f, (Color.FromArgb(200, 0x6C, 0xCB, 0xFF), Color.FromArgb(200, 0x0F, 0x6C, 0xBD)));
            p.Shape((Ink, Ink), 5.6f, 4.4f, 5.6f, 15.6f, 8.4f, 12.9f, 10.2f, 16.1f, 12.2f, 15.1f, 10.4f, 12, 14.2f, 11.8f);
            p.Shape(White, 6.6f, 6.6f, 6.6f, 13.4f, 8.6f, 11.4f, 10.6f, 14.8f, 11.2f, 14.5f, 9.4f, 11.1f, 12, 11);
        },
        // Picture-in-Picture: a screen with a small picture in its corner.
        [MenuParts.NewWindow] = p =>
        {
            p.Box(1, 2.5f, 14, 11, 1.8f, DeepBlue);
            p.Box(2.2f, 3.7f, 11.6f, 8.6f, 1, (Rgb(0x4FA8EC), Rgb(0x1E6CC0)));
            p.Box(7.8f, 7.6f, 5.6f, 4.2f, 0.9f, Teal);
        },
        // Dark title bar: a crescent moon.
        [MenuParts.Moon] = p =>
        {
            p.Circle(1.5f, 1.5f, 13, Yellow);
            p.Cut(5.6f, -0.6f, 11.6f);
        },
        // Borderless: a window's corners, opening out.
        [MenuParts.FullScreen] = p =>
        {
            p.Box(1, 1, 14, 14, 3, Teal);
            p.Line(Light, 1.5f, 4, 7, 4, 4, 7, 4);
            p.Line(Light, 1.5f, 9, 4, 12, 4, 12, 7);
            p.Line(Light, 1.5f, 12, 9, 12, 12, 9, 12);
            p.Line(Light, 1.5f, 7, 12, 4, 12, 4, 9);
        },
        // Restore: an arrow turning back, on a round badge.
        [MenuParts.Undo] = p =>
        {
            p.Circle(1, 1, 14, Green);
            p.Arc(Light, 1.6f, 4.3f, 4.3f, 7.4f, 210, 290);
            p.Shape(White, 2.9f, 4.4f, 7.1f, 4.1f, 4.6f, 8.2f);
        },
        // Copy: a page over another.
        [MenuParts.Copy] = p =>
        {
            p.Box(1.5f, 1.5f, 9, 11, 1.4f, Grey);
            p.Box(5.5f, 4, 9, 11, 1.4f, Paper);
            p.Box(5.5f, 4, 9, 2.6f, 1.2f, Blue);
            p.Line(Rgb(0xB0B7C2), 0.9f, 7.5f, 9, 12.5f, 9);
            p.Line(Rgb(0xB0B7C2), 0.9f, 7.5f, 11.5f, 11, 11.5f);
        },
        // A folder.
        [MenuParts.Folder] = p =>
        {
            p.Shape((Rgb(0xF3B53F), Rgb(0xD58A12)), 1, 3, 6, 3, 7.5f, 4.6f, 15, 4.6f, 15, 13.5f, 1, 13.5f);
            p.Box(1, 6, 14, 8, 1.2f, (Rgb(0xFFDA6E), Rgb(0xF6B42C)));
        },
        // Save: a disk.
        [MenuParts.Save] = p =>
        {
            p.Box(1.5f, 1.5f, 13, 13, 2, DeepBlue);
            p.Box(4, 1.5f, 8, 4.5f, 0.6f, Paper);
            p.Box(9, 2.3f, 2, 2.8f, 0.4f, Slate);
            p.Box(3.6f, 8.6f, 8.8f, 5.9f, 1, White);
        },
        // More: three dots.
        [MenuParts.More] = p =>
        {
            foreach (float x in new[] { 3.5f, 8, 12.5f }) p.Circle(x - 1.6f, 6.4f, 3.2f, Mid);
        },
        // Settings: a gear.
        [MenuParts.Settings] = p =>
        {
            for (int k = 0; k < 8; k++)
                p.Turned(k * 45, () => p.Box(6.6f, 0.8f, 2.8f, 3.6f, 0.8f, Grey));
            p.Circle(2.4f, 2.4f, 11.2f, Grey);
            p.Circle(5.3f, 5.3f, 5.4f, Slate);
            p.Circle(6.3f, 6.3f, 3.4f, (Rgb(0xD5D8DD), Rgb(0xB9BEC6)));
        },
        // Check for updates: turning arrows on a round badge.
        [MenuParts.Sync] = p =>
        {
            p.Circle(1, 1, 14, Blue);
            p.Arc(Light, 1.5f, 4.2f, 4.2f, 7.6f, 200, 130);
            p.Arc(Light, 1.5f, 4.2f, 4.2f, 7.6f, 20, 130);
            p.Shape(White, 10.6f, 3.2f, 12.9f, 6.7f, 9.2f, 6.8f);
            p.Shape(White, 5.4f, 12.8f, 3.1f, 9.3f, 6.8f, 9.2f);
        },
        // About: an "i" on a round badge.
        [MenuParts.Info] = p =>
        {
            p.Circle(1, 1, 14, Blue);
            p.Dot(Light, 8, 4.9f, 1.9f);
            p.Line(Light, 1.8f, 8, 7.4f, 8, 11.6f);
        },
        // Exit: a power sign on a red badge.
        [MenuParts.Power] = p =>
        {
            p.Circle(1, 1, 14, Red);
            p.Arc(Light, 1.5f, 4.4f, 4.7f, 7.2f, -55, 290);
            p.Line(Light, 1.5f, 8, 3.6f, 8, 8);
        },
        // Seeing: an eye.
        [MenuParts.View] = p =>
        {
            p.Shape(Teal, 0.8f, 8, 3, 4.6f, 8, 3, 13, 4.6f, 15.2f, 8, 13, 11.4f, 8, 13, 3, 11.4f);
            p.Circle(5.2f, 5.2f, 5.6f, White);
            p.Circle(6.4f, 6.4f, 3.2f, (Rgb(0x2E3440), Rgb(0x111418)));
            p.Dot(Light, 7.1f, 7.1f, 0.9f);
        },
        // Crop: two corners overlapping.
        [MenuParts.Crop] = p =>
        {
            p.Line(Rgb(0x9C6AF0), 2, 4.5f, 1.5f, 4.5f, 11.5f, 14.5f, 11.5f);
            p.Line(Rgb(0x3A9BE6), 2, 1.5f, 4.5f, 11.5f, 4.5f, 11.5f, 14.5f);
        },
        // Presets: a star.
        [MenuParts.Star] = p =>
        {
            var points = new List<float>();
            for (int k = 0; k < 10; k++)
            {
                double angle = Math.PI / 5 * k - Math.PI / 2;
                float radius = k % 2 == 0 ? 7.2f : 3.1f;
                points.Add(8 + (float)Math.Cos(angle) * radius);
                points.Add(8.6f + (float)Math.Sin(angle) * radius);
            }
            p.Shape(Yellow, [.. points]);
        },
        // Help: a question mark on a round badge.
        [MenuParts.Help] = p =>
        {
            p.Circle(1, 1, 14, Purple);
            p.Sign("?", 10.5f, Light);
        },
        // Minimize: a bar.
        [MenuParts.Minimize] = p => p.Box(2, 7, 12, 2.6f, 1.3f, Mid),
        // Close: a cross on a red badge.
        [MenuParts.Close] = p =>
        {
            p.Circle(1, 1, 14, Red);
            p.Line(Light, 1.6f, 5.4f, 5.4f, 10.6f, 10.6f);
            p.Line(Light, 1.6f, 10.6f, 5.4f, 5.4f, 10.6f);
        },
        // Something went wrong: a warning sign.
        [MenuParts.Warning] = p =>
        {
            p.Shape(Yellow, 8, 1.2f, 15.2f, 14.4f, 0.8f, 14.4f);
            p.Line(Ink, 1.5f, 8, 5.6f, 8, 9.6f);
            p.Dot(Ink, 8, 12.1f, 1.7f);
        },
        // A window.
        [MenuParts.Page] = p =>
        {
            p.Box(1, 2, 14, 12, 1.8f, Paper);
            p.Box(1, 2, 14, 3.4f, 1.6f, Blue);
            p.Line(Rgb(0xB0B7C2), 0.9f, 3.5f, 8, 12.5f, 8);
            p.Line(Rgb(0xB0B7C2), 0.9f, 3.5f, 10.5f, 10, 10.5f);
        },
        // A display.
        [MenuParts.Monitor] = p =>
        {
            p.Box(6.2f, 11, 3.6f, 3, 0.4f, Grey);
            p.Box(4, 13.4f, 8, 1.6f, 0.8f, Grey);
            p.Box(1, 1.5f, 14, 10.5f, 1.6f, DeepBlue);
            p.Box(2.1f, 2.6f, 11.8f, 8.3f, 1, (Rgb(0x55B4F2), Rgb(0x1F72C6)));
        },
        // General: a house.
        [MenuParts.Home] = p =>
        {
            p.Box(3, 7, 10, 7.6f, 1, Grey);
            p.Box(6.5f, 9.6f, 3, 5, 0.8f, Slate);
            p.Shape(Orange, 8, 1.4f, 15.2f, 8, 13.4f, 8.8f, 8, 3.9f, 2.6f, 8.8f, 0.8f, 8);
        },
        // Actions and shortcuts: a keyboard.
        [MenuParts.Keyboard] = p =>
        {
            p.Box(0.8f, 3.5f, 14.4f, 9, 1.6f, Slate);
            foreach (float y in new[] { 5.6f, 8 })
                for (float x = 2.6f; x < 13.5f; x += 2.4f) p.Box(x, y, 1.6f, 1.5f, 0.3f, White);
            p.Box(4.4f, 10.3f, 7.2f, 1.4f, 0.4f, White);
        },
        // Mouse gestures: a mouse.
        [MenuParts.Mouse] = p =>
        {
            p.Box(3.5f, 1, 9, 14, 4.5f, Grey);
            p.Box(3.5f, 1, 4.5f, 6.2f, 2.2f, Blue);
            p.Line(Rgb(0x8A8F98), 0.8f, 8, 1.6f, 8, 6.8f);
        },
        // The toolbar.
        [MenuParts.Bar] = p =>
        {
            p.Box(1, 3, 14, 10, 1.8f, Grey);
            p.Box(1, 3, 14, 4, 1.8f, Blue);
            p.Box(1, 5.4f, 14, 1.6f, 0, Blue);
            foreach (float x in new[] { 3.9f, 6.9f, 9.9f }) p.Dot(Light, x, 5, 1.6f);
        },
        // Appearance: a painter's palette.
        [MenuParts.Palette] = p =>
        {
            p.Circle(1, 1.5f, 14, (Rgb(0xF4E3C8), Rgb(0xD9BC92)));
            p.Cut(9.4f, 9.2f, 3.6f);
            p.Dot(Rgb(0xE5484D), 4.8f, 6.4f, 2.4f);
            p.Dot(Rgb(0x2E8BDC), 8, 4.4f, 2.4f);
            p.Dot(Rgb(0x2FB36A), 11.4f, 6.4f, 2.4f);
            p.Dot(Rgb(0xF2B01E), 5.2f, 10.2f, 2.4f);
        },
        // Programs: four tiles.
        [MenuParts.Apps] = p =>
        {
            p.Box(1.5f, 1.5f, 5.8f, 5.8f, 1.2f, Blue);
            p.Box(8.7f, 1.5f, 5.8f, 5.8f, 1.2f, Teal);
            p.Box(1.5f, 8.7f, 5.8f, 5.8f, 1.2f, Purple);
            p.Box(8.7f, 8.7f, 5.8f, 5.8f, 1.2f, Grey);
        },
        // Search: a magnifying glass.
        [MenuParts.Search] = p =>
        {
            p.Line(Rgb(0x6B7079), 2.4f, 10.4f, 10.4f, 14, 14);
            p.Circle(1, 1, 11, Blue);
            p.Circle(3, 3, 7, (Rgb(0xE8F5FF), Rgb(0xB9DEFA)));
        },
        // Size and options: sliders.
        [MenuParts.Sliders] = p =>
        {
            foreach (float y in new[] { 3.5f, 8, 12.5f }) p.Line(Rgb(0x8A8F98), 1.2f, 2, y, 14, y);
            p.Circle(8.6f, 1.9f, 3.2f, Blue);
            p.Circle(3.2f, 6.4f, 3.2f, Teal);
            p.Circle(10.6f, 10.9f, 3.2f, Purple);
        },
        // Opacity: a disc half see-through.
        [MenuParts.Brightness] = p =>
        {
            p.Circle(1.5f, 1.5f, 13, (Color.FromArgb(110, 0x6C, 0xCB, 0xFF), Color.FromArgb(110, 0x0F, 0x6C, 0xBD)));
            p.Shape(Blue, 8, 1.5f, 8, 14.5f, 7.4f, 14.5f, 4.6f, 13.6f, 2.6f, 11.6f, 1.6f, 9.2f, 1.6f, 6.8f, 2.6f, 4.4f, 4.6f, 2.4f, 7.4f, 1.5f);
        },
        // Effects: sparkles.
        [MenuParts.Repair] = p =>
        {
            p.Shape(Purple, 7, 1, 8.6f, 5.4f, 13, 7, 8.6f, 8.6f, 7, 13, 5.4f, 8.6f, 1, 7, 5.4f, 5.4f);
            p.Shape(Blue, 12.5f, 9.5f, 13.3f, 11.7f, 15.5f, 12.5f, 13.3f, 13.3f, 12.5f, 15.5f, 11.7f, 13.3f, 9.5f, 12.5f, 11.7f, 11.7f);
        },
        // History: a clock.
        [MenuParts.History] = p =>
        {
            p.Circle(1, 1, 14, Blue);
            p.Circle(2.6f, 2.6f, 10.8f, White);
            p.Line(Ink, 1.3f, 8, 4.8f, 8, 8, 10.6f, 9.6f);
        },
        // Delete: a bin.
        [MenuParts.Delete] = p =>
        {
            p.Box(3, 4.5f, 10, 10.5f, 1.4f, Grey);
            p.Box(1.8f, 2.6f, 12.4f, 2.2f, 1, Slate);
            p.Box(6, 1, 4, 2.2f, 0.8f, Slate);
            foreach (float x in new[] { 5.8f, 8, 10.2f }) p.Line(Rgb(0x7A7F88), 0.9f, x, 7, x, 13);
        },
        // Dark mode: a disc, half dark.
        [DarkMode] = p =>
        {
            p.Circle(1.5f, 1.5f, 13, (Rgb(0xF6F7F9), Rgb(0xD2D6DD)));
            p.Shape(Slate, 8, 1.5f, 8, 14.5f, 9.6f, 14.3f, 11.8f, 13.3f, 13.4f, 11.6f, 14.3f, 9.6f, 14.5f, 8, 14.3f, 6.4f, 13.4f, 4.4f, 11.8f, 2.7f, 9.6f, 1.7f);
        },
        // Glass: a pane that lets the light through.
        [Glass] = p =>
        {
            p.Box(1.5f, 1.5f, 13, 13, 2.4f, (Color.FromArgb(150, 0x9F, 0xDD, 0xFF), Color.FromArgb(150, 0x3A, 0x9B, 0xE6)));
            p.Line(Color.FromArgb(220, 255, 255, 255), 1.3f, 4, 9, 9, 4);
            p.Line(Color.FromArgb(220, 255, 255, 255), 1.3f, 7, 12, 12, 7);
        },
        // Shrink: a window drawn in to a small tile.
        [Shrink] = p =>
        {
            p.Box(1, 1, 14, 14, 2, (Color.FromArgb(90, 0x9A, 0x9A, 0x9A), Color.FromArgb(90, 0x9A, 0x9A, 0x9A)));
            p.Box(7.5f, 7.5f, 6.5f, 6.5f, 1.2f, Teal);
            p.Line(Rgb(0x0F6CBD), 1.4f, 3, 3, 6.4f, 6.4f);
            p.Shape(Blue, 7, 3.6f, 7, 7, 3.6f, 7);
        },
        // Arrange window: a window and four arrows.
        [Move] = p =>
        {
            p.Line(Rgb(0x0F6CBD), 1.4f, 8, 1.6f, 8, 14.4f);
            p.Line(Rgb(0x0F6CBD), 1.4f, 1.6f, 8, 14.4f, 8);
            p.Shape(Blue, 8, 0.6f, 10.2f, 3.4f, 5.8f, 3.4f);
            p.Shape(Blue, 8, 15.4f, 10.2f, 12.6f, 5.8f, 12.6f);
            p.Shape(Blue, 0.6f, 8, 3.4f, 5.8f, 3.4f, 10.2f);
            p.Shape(Blue, 15.4f, 8, 12.6f, 5.8f, 12.6f, 10.2f);
            p.Box(5.4f, 5.4f, 5.2f, 5.2f, 1, Teal);
        },
        [Plus] = p =>
        {
            p.Circle(1.5f, 1.5f, 13, Blue);
            p.Line(Light, 1.6f, 8, 4.8f, 8, 11.2f);
            p.Line(Light, 1.6f, 4.8f, 8, 11.2f, 8);
        },
        [Minus] = p =>
        {
            p.Circle(1.5f, 1.5f, 13, (Color.FromArgb(150, 0x6C, 0xCB, 0xFF), Color.FromArgb(150, 0x0F, 0x6C, 0xBD)));
            p.Line(Light, 1.6f, 4.8f, 8, 11.2f, 8);
        },
    };
}
