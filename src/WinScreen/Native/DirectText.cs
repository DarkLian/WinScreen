using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// Text drawn and measured with DirectWrite, the way Windows 11's own Settings draws it: fuller letters spaced as
/// the font designs them, smoothed in grey. It takes the same arguments as <see cref="TextRenderer"/> and draws on
/// the same surfaces, through a Direct2D render target bound to the surface's device context. Where Direct2D isn't
/// there, the text goes through <see cref="TextRenderer"/> instead.
/// </summary>
internal static unsafe class DirectText
{
    private static readonly Guid FactoryId = new("06152247-6f50-465a-9245-118bfd3b6007"); // ID2D1Factory
    private static readonly Guid WriteId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"); // IDWriteFactory

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int type, in Guid id, nint options, out nint factory);

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int type, in Guid id, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderTargetProperties
    {
        public int Type, Format, AlphaMode;
        public float DpiX, DpiY;
        public int Usage, MinLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorF { public float R, G, B, A; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point2F { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Trimming { public int Granularity; public uint Delimiter, DelimiterCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetrics
    {
        public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
        public uint MaxBidiReorderingDepth, LineCount;
    }

    private static readonly nint Factory, Write, Target;

    // The surface text is drawn on first: see-through, then laid over what is being drawn. Drawing straight onto a
    // device context went wrong where the context doesn't carry the colours under it, as for a picture.
    private static nint _surfaceDc, _surface, _bits;
    private static int _surfaceWidth, _surfaceHeight;

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint thing);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint thing);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    /// <summary>A surface at least this big, its pixels see-through.</summary>
    private static bool Surface(int width, int height)
    {
        if (width > _surfaceWidth || height > _surfaceHeight)
        {
            int w = Math.Max(width, _surfaceWidth), h = Math.Max(height, _surfaceHeight);
            var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            nint made = CreateDIBSection(0, info, 0, out nint bits, 0, 0);
            if (made == 0) return false;
            if (_surfaceDc == 0) _surfaceDc = CreateCompatibleDC(0);
            SelectObject(_surfaceDc, made);
            if (_surface != 0) DeleteObject(_surface);
            (_surface, _bits, _surfaceWidth, _surfaceHeight) = (made, bits, w, h);
        }
        for (int row = 0; row < height; row++)
            new Span<byte>((byte*)_bits + (long)row * _surfaceWidth * 4, width * 4).Clear();
        return true;
    }

    private static readonly Dictionary<(string, float, int, int), nint> Formats = [];
    private static readonly Dictionary<nint, nint> Ellipses = [];

    /// <summary>Whether text goes through DirectWrite; it does wherever Windows has it.</summary>
    internal static bool Available { get; }

    static DirectText()
    {
        try
        {
            if (D2D1CreateFactory(0, FactoryId, 0, out Factory) < 0 || DWriteCreateFactory(0, WriteId, out Write) < 0) return;
            var properties = new RenderTargetProperties { Type = 0, Format = 87 /* B8G8R8A8_UNORM */, AlphaMode = 1 /* premultiplied */, DpiX = 96, DpiY = 96 };
            nint target;
            if (Call(Factory, 16, &properties, &target) < 0) return; // CreateDCRenderTarget
            Target = target;
            ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(Target, 34))(Target, 2); // Grey smoothing, as Windows 11 has.
            Available = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

    private static int Call(nint com, int index, void* a, void* b) =>
        ((delegate* unmanaged[Stdcall]<nint, void*, void*, int>)Slot(com, index))(com, a, b);

    private static void Release(nint com)
    {
        if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
    }

    /// <summary>The family and weight DirectWrite knows a font by: Windows lists each weight of a font as a family of its own.</summary>
    private static (string Family, int Weight) Face(Font font)
    {
        string name = font.FontFamily.Name;
        int weight = font.Bold ? 700 : 400;
        foreach ((string suffix, int value) in new[] { (" Semibold", 600), (" Semib", 600), (" Semilight", 350), (" Semil", 350), (" Light", 300), (" Bold", 700) })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return (name[..^suffix.Length], font.Bold ? 700 : value);
        return (name, weight);
    }

    private static float PixelSize(Font font) => font.SizeInPoints * 96f / 72f;

    private static nint Format(Font font, TextFormatFlags flags)
    {
        (string family, int weight) = Face(font);
        float size = PixelSize(font);
        int style = font.Italic ? 2 : 0;
        bool wraps = (flags & TextFormatFlags.WordBreak) != 0 && (flags & TextFormatFlags.SingleLine) == 0;
        var key = (family, size, weight * 4 + style, wraps ? 1 : 0);
        if (Formats.TryGetValue(key, out nint format)) return format;
        fixed (char* familyName = family)
        fixed (char* locale = "")
        {
            ((delegate* unmanaged[Stdcall]<nint, char*, nint, int, int, int, float, char*, nint*, int>)Slot(Write, 15))(
                Write, familyName, 0, weight, style, 5, size, locale, &format); // CreateTextFormat
        }
        if (format == 0) return 0;
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(format, 5))(format, wraps ? 0 : 1); // SetWordWrapping
        return Formats[key] = format;
    }

    private static string Shown(string text, TextFormatFlags flags) =>
        (flags & TextFormatFlags.NoPrefix) != 0 ? text : text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");

    /// <summary>The words laid out in a box, with the alignment and shortening the flags ask for.</summary>
    private static nint Layout(string text, Font font, float width, float height, TextFormatFlags flags)
    {
        nint format = Format(font, flags);
        if (format == 0) return 0;
        nint layout = 0;
        fixed (char* words = text)
            ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Slot(Write, 18))(
                Write, words, (uint)text.Length, format, Math.Max(0, width), Math.Max(0, height), &layout); // CreateTextLayout
        if (layout == 0) return 0;
        int align = (flags & TextFormatFlags.HorizontalCenter) != 0 ? 2 : (flags & TextFormatFlags.Right) != 0 ? 1 : 0;
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(layout, 3))(layout, align); // SetTextAlignment
        int paragraph = (flags & TextFormatFlags.VerticalCenter) != 0 ? 2 : (flags & TextFormatFlags.Bottom) != 0 ? 1 : 0;
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(layout, 4))(layout, paragraph); // SetParagraphAlignment
        if ((flags & (TextFormatFlags.EndEllipsis | TextFormatFlags.WordEllipsis)) != 0)
        {
            if (!Ellipses.TryGetValue(format, out nint sign))
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(Write, 20))(Write, format, &sign); // CreateEllipsisTrimmingSign
                Ellipses[format] = sign;
            }
            var trimming = new Trimming { Granularity = 1 }; // By character.
            ((delegate* unmanaged[Stdcall]<nint, Trimming*, nint, int>)Slot(layout, 9))(layout, &trimming, sign); // SetTrimming
        }
        return layout;
    }

    private static int Padding(Font font, TextFormatFlags flags) =>
        (flags & TextFormatFlags.NoPadding) != 0 ? 0 : (int)Math.Round(PixelSize(font) / 6f);

    // ---- Measuring ----

    internal static Size MeasureText(string? text, Font font) => MeasureText(text, font, Size.Empty, TextFormatFlags.Default);

    internal static Size MeasureText(string? text, Font font, Size proposed) => MeasureText(text, font, proposed, TextFormatFlags.Default);

    internal static Size MeasureText(IDeviceContext _, string? text, Font font, Size proposed, TextFormatFlags flags) =>
        MeasureText(text, font, proposed, flags);

    internal static Size MeasureText(IDeviceContext _, string? text, Font font) => MeasureText(text, font);

    internal static Size MeasureText(string? text, Font font, Size proposed, TextFormatFlags flags)
    {
        if (!Available) return TextRenderer.MeasureText(text, font, proposed, flags);
        string words = Shown(string.IsNullOrEmpty(text) ? " " : text, flags);
        bool wraps = (flags & TextFormatFlags.WordBreak) != 0 && (flags & TextFormatFlags.SingleLine) == 0 && proposed.Width > 0;
        int padding = Padding(font, flags);
        float width = wraps ? proposed.Width - padding * 2 : 100000f;
        nint layout = Layout(words, font, width, 100000f, flags & ~(TextFormatFlags.EndEllipsis | TextFormatFlags.WordEllipsis |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.Bottom));
        if (layout == 0) return TextRenderer.MeasureText(text, font, proposed, flags);
        try
        {
            TextMetrics metrics;
            ((delegate* unmanaged[Stdcall]<nint, TextMetrics*, int>)Slot(layout, 60))(layout, &metrics); // GetMetrics
            int lineHeight = (int)Math.Ceiling(metrics.Height / Math.Max(1u, metrics.LineCount));
            int height = string.IsNullOrEmpty(text) ? lineHeight : (int)Math.Ceiling(metrics.Height);
            int measured = string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(metrics.WidthIncludingTrailingWhitespace);
            return new Size(measured + padding * 2, height);
        }
        finally { Release(layout); }
    }

    // ---- Drawing ----

    internal static void DrawText(IDeviceContext dc, string? text, Font font, Point at, Color color, TextFormatFlags flags = TextFormatFlags.Default)
    {
        Size size = MeasureText(text, font, Size.Empty, flags | TextFormatFlags.SingleLine);
        DrawText(dc, text, font, new Rectangle(at, size), color, flags);
    }

    internal static void DrawText(IDeviceContext dc, string? text, Font font, Point at, Color color, Color back, TextFormatFlags flags = TextFormatFlags.Default)
    {
        Size size = MeasureText(text, font, Size.Empty, flags | TextFormatFlags.SingleLine);
        DrawText(dc, text, font, new Rectangle(at, size), color, back, flags);
    }

    internal static void DrawText(IDeviceContext dc, string? text, Font font, Rectangle bounds, Color color) =>
        DrawText(dc, text, font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    /// <summary>
    /// With a background colour, the box is painted in it first, as Windows' own text drawing does: drawn again and
    /// again over itself, as text in a box Windows paints is, the words would otherwise grow darker each time.
    /// </summary>
    internal static void DrawText(IDeviceContext dc, string? text, Font font, Rectangle bounds, Color color, Color back, TextFormatFlags flags)
    {
        if (back.A > 0 && dc is Graphics graphics && Available)
        {
            using var brush = new SolidBrush(back);
            graphics.FillRectangle(brush, bounds);
        }
        DrawText(dc, text, font, bounds, color, flags);
    }

    internal static void DrawText(IDeviceContext dc, string? text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!Available || dc is not Graphics graphics) { TextRenderer.DrawText(dc, text, font, bounds, color, flags); return; }
        int padding = Padding(font, flags);
        var inner = Rectangle.FromLTRB(bounds.Left + padding, bounds.Top, bounds.Right - padding, bounds.Bottom);
        bool single = (flags & TextFormatFlags.SingleLine) != 0 || (flags & TextFormatFlags.WordBreak) == 0;
        nint layout = Layout(Shown(text, flags), font, inner.Width, inner.Height,
            single ? flags | TextFormatFlags.SingleLine : flags);
        if (layout == 0) { TextRenderer.DrawText(dc, text, font, bounds, color, flags); return; }
        try
        {
            if (inner.Width <= 0 || inner.Height <= 0 || !Surface(inner.Width, inner.Height)) return;
            var area = new Rect(0, 0, inner.Width, inner.Height);
            if (Call(Target, 57, (void*)_surfaceDc, &area) < 0) return; // BindDC
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(Target, 48))(Target); // BeginDraw
            nint brush = 0;
            var paint = new ColorF { R = color.R / 255f, G = color.G / 255f, B = color.B / 255f, A = color.A / 255f };
            ((delegate* unmanaged[Stdcall]<nint, ColorF*, nint, nint*, int>)Slot(Target, 8))(Target, &paint, 0, &brush); // CreateSolidColorBrush
            ((delegate* unmanaged[Stdcall]<nint, Point2F, nint, nint, int, void>)Slot(Target, 28))(Target, new Point2F(), layout, brush, 0); // DrawTextLayout
            ulong tag1, tag2;
            int result = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Slot(Target, 49))(Target, &tag1, &tag2); // EndDraw
            Release(brush);
            if (result < 0) { TextRenderer.DrawText(dc, text, font, bounds, color, flags); return; } // Drawn the old way once.
            // Laid over what is there, following the graphics' own clip and shift.
            using var words = new Bitmap(inner.Width, inner.Height, _surfaceWidth * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, _bits);
            System.Drawing.Drawing2D.CompositingMode mode = graphics.CompositingMode;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            graphics.DrawImageUnscaled(words, inner.Left, inner.Top);
            graphics.CompositingMode = mode;
        }
        finally { Release(layout); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        public int Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF(float left, float top, float right, float bottom)
    {
        public float Left = left, Top = top, Right = right, Bottom = bottom;
    }
}
