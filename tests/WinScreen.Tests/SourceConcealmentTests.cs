using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

public sealed class CaptureBorderTests : IDisposable
{
    private readonly bool _wanted = CaptureBorder.Wanted;
    private readonly Func<Task<bool>> _ask = CaptureBorder.Ask;
    private readonly Func<bool> _supported = CaptureBorder.Supported;

    public void Dispose()
    {
        CaptureBorder.Wanted = _wanted;
        CaptureBorder.Ask = _ask;
        CaptureBorder.Supported = _supported;
        CaptureBorder.Forget();
    }

    private static bool? Applied(bool wanted, bool supported, Func<Task<bool>> ask, int wait = 1000)
    {
        CaptureBorder.Forget();
        CaptureBorder.Wanted = wanted;
        CaptureBorder.Supported = () => supported;
        CaptureBorder.Ask = ask;
        bool? set = null;
        bool hidden = CaptureBorder.TryHide(required => set = required, wait);
        Assert.Equal(hidden, set == false);
        return set;
    }

    [Fact]
    public void The_border_goes_only_when_wanted_supported_and_allowed()
    {
        Assert.False(Applied(true, true, () => Task.FromResult(true)));
        Assert.Null(Applied(false, true, () => Task.FromResult(true)));
        Assert.Null(Applied(true, false, () => Task.FromResult(true)));
        Assert.Null(Applied(true, true, () => Task.FromResult(false)));
    }

    [Fact]
    public void A_failed_or_slow_answer_keeps_the_border_and_the_capture_going()
    {
        Assert.Null(Applied(true, true, () => Task.FromException<bool>(new UnauthorizedAccessException())));
        Assert.Null(Applied(true, true, async () => { await Task.Delay(5000); return true; }, wait: 50));
        CaptureBorder.Forget();
        CaptureBorder.Ask = () => Task.FromResult(true);
        Assert.False(CaptureBorder.TryHide(_ => throw new InvalidOperationException("Setter refused")));
    }

    [Fact]
    public void Windows_is_asked_once()
    {
        int asked = 0;
        Applied(true, true, () => { asked++; return Task.FromResult(true); });
        Assert.True(CaptureBorder.TryHide(_ => { }));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void It_is_on_by_default() => Assert.True(new AppSettings().HideCaptureBorder);
}

/// <summary>
/// Looks at the screen around a hidden original, at rest and while it follows the pointer under its view,
/// for anything of it showing: an outline, a shadow, or Windows' capture border.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class SourceConcealmentTests
{
    private static readonly Color Backdrop = Color.FromArgb(0xE0, 0xE0, 0xE0);
    private const int Ring = 12;

    /// <summary>The pixels in a band around <paramref name="window"/>, inside and out, that aren't under the view.</summary>
    private static string? Leak(Rectangle window, Rectangle view, Bitmap? baseline = null)
    {
        Rectangle outer = Rectangle.Inflate(window, Ring, Ring), inner = Rectangle.Inflate(window, -Ring, -Ring);
        Rectangle keepOut = Rectangle.Inflate(view, 24, 24); // The view's own frame and shadow.
        outer.Intersect(Screen.PrimaryScreen!.Bounds);
        using var bitmap = new Bitmap(outer.Width, outer.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(outer.Location, Point.Empty, outer.Size);
        int worst = 0;
        Point at = Point.Empty;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var point = new Point(outer.Left + x, outer.Top + y);
                if (inner.Contains(point) || keepOut.Contains(point)) continue;
                Color pixel = bitmap.GetPixel(x, y);
                Color expected = baseline?.GetPixel(point.X - Screen.PrimaryScreen!.Bounds.Left,
                    point.Y - Screen.PrimaryScreen!.Bounds.Top) ?? Backdrop;
                int difference = Math.Max(Math.Abs(pixel.R - expected.R),
                    Math.Max(Math.Abs(pixel.G - expected.G), Math.Abs(pixel.B - expected.B)));
                if (difference > worst) { worst = difference; at = point; }
            }
        }
        return worst > 10 ? $"{worst} levels off the backdrop at {at} (window {window}, view {view})" : null;
    }

    [Theory]
    [InlineData(PipBackendPreference.Dwm)]
    [InlineData(PipBackendPreference.Wgc)]
    public void Nothing_of_a_parked_original_shows_at_rest_or_during_a_pointer_visit(PipBackendPreference backend)
    {
        Rectangle screen = Screen.PrimaryScreen!.WorkingArea;
        using var backdrop = new TargetProcess(screen, "E0E0E0", "WinScreen test backdrop");
        using var source = new TargetProcess(new Rectangle(160, 160, 500, 400));
        source.UseFrame();
        Desktop.GainForegroundRights();
        Desktop.Activate(source.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ApplyPipPreferences(new PipPreferences { Backend = backend });
            view.ShowOnTop();
            view.Location = new Point(screen.Right - view.Width - 80, screen.Bottom - view.Height - 80);
            Rectangle originalHome = Desktop.BoundsOf(source.Handle);
            using var baseline = new Bitmap(Screen.PrimaryScreen!.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            using (Graphics graphics = Graphics.FromImage(baseline))
                graphics.CopyFromScreen(Screen.PrimaryScreen.Bounds.Location, Point.Empty, baseline.Size);
            view.SetInteractive(true);
            Assert.True(view.KeepsUpdating);
            Assert.True(Desktop.WaitFor(() => { Application.DoEvents(); return view.BackendKind == PipBackendKind.DwmThumbnail || view.DiagnosticSnapshot.Contains("Capture border: off"); }, 6000), view.DiagnosticSnapshot);
            Assert.True(view.BackendKind == PipBackendKind.DwmThumbnail || view.DiagnosticSnapshot.Contains("Capture border: off"), view.DiagnosticSnapshot);
            Pump(400);
            void Check(string when)
            {
                string? leak = Leak(Desktop.BoundsOf(source.Handle), view.Bounds, baseline);
                Assert.True(leak is null, when + ": " + leak);
                leak = Leak(originalHome, view.Bounds);
                Assert.True(leak is null, when + " at the original place: " + leak);
            }
            Check("At rest");

            Point home = Desktop.CursorPosition();
            try
            {
                Rectangle frame = view.Bounds;
                var inside = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
                Desktop.MoveMouse(new Point(inside.X - 1, inside.Y));
                Pump(30);
                Desktop.MoveMouse(inside);
                Assert.True(Desktop.WaitFor(() => { Application.DoEvents(); return view.LiveInteraction is not null; }, 2000),
                    "The visit didn't start.");
                Desktop.SlideMouse(new Point(inside.X + 40, inside.Y + 30));
                Pump(300);
                Check("During the pointer visit");
            }
            finally
            {
                Desktop.MoveMouse(home);
                Pump(200);
                view.Close();
            }
        });
    }

    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
