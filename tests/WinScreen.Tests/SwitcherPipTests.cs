using System.Reflection;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class SwitcherPipTests
{
    [Fact]
    public void Compatibility_view_stays_live_and_closing_restores_switcher() => CheckView(PipBackendPreference.Dwm);

    [WgcFact]
    public void Advanced_view_stays_live_and_closing_restores_switcher() => CheckView(PipBackendPreference.Wgc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_reveal_keeps_the_view_open_as_a_recovery_surface(bool hideSwitcher)
    {
        using var source = new TargetProcess(new Rectangle(240, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows) { AllowSwitcherHiding = true };
            pip.ShowOnTop();
            Pump(200);
            pip.SetOriginalHidden(true);
            pip.SetSwitcherHidden(hideSwitcher);
            Assert.Equal(hideSwitcher, windows.IsOutOfSwitcher(source.Handle));
            source.Hang();
            pip.Close();
            Assert.False(pip.IsDisposed);
            Assert.True(windows.IsHidden(source.Handle));
            Assert.Equal(hideSwitcher, windows.IsOutOfSwitcher(source.Handle));
        });
    }

    /// <summary>
    /// A hidden original keeps its own caption buttons in the view: its title bar looks as it did while it was
    /// shown. Taking it out of Alt+Tab as well (the separate option) gives it a tool window's frame, with a small
    /// close button only, which shows the comparison can tell the two apart.
    /// </summary>
    [Fact]
    public void A_hidden_original_keeps_its_own_caption_buttons_in_the_view()
    {
        using var source = new TargetProcess(new Rectangle(240, 160, 400, 300));
        source.UseFrame(); // A real title bar with Windows' own caption buttons.
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows) { AllowSwitcherHiding = true };
            pip.ApplyPipPreferences(new PipPreferences { Backend = PipBackendPreference.Dwm });
            try
            {
                pip.ShowOnTop();
                pip.Location = new Point(800, 140);
                Pump(800);
                Rectangle Corner()
                {
                    Rectangle b = pip.Bounds;
                    return new Rectangle(b.Right - Math.Min(160, b.Width / 2), b.Top, Math.Min(160, b.Width / 2), Math.Min(48, b.Height / 4));
                }
                Rectangle corner = Corner();
                using Bitmap shown = Capture(corner);
                pip.SetOriginalHidden(true);
                Assert.True(windows.IsHidden(source.Handle));
                Pump(1200);
                Assert.Equal(corner, Corner());
                using Bitmap hidden = Capture(corner);
                double same = Difference(shown, hidden);

                pip.SetSwitcherHidden(true);
                Assert.True(windows.IsOutOfSwitcher(source.Handle));
                Pump(1200);
                using Bitmap tool = Capture(corner);
                double changed = Difference(shown, tool);
                Assert.True(same < 6 && changed > 3 * same + 4,
                    $"Title bar corner differs from the shown original by {same:F1} when hidden and {changed:F1} as a tool window.");
                pip.Close();
                Assert.False(windows.IsHidden(source.Handle));
            }
            finally { windows.RestoreAll(); }
        });
    }

    private static Bitmap Capture(Rectangle area)
    {
        var image = new Bitmap(area.Width, area.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return image;
    }

    /// <summary>Mean difference per color channel, 0 to 255.</summary>
    private static double Difference(Bitmap a, Bitmap b)
    {
        long total = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                total += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        return total / (3.0 * a.Width * a.Height);
    }

    private static void Pump(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(2); }
    }

    private static void CheckView(PipBackendPreference preference)
    {
        using var source = new TargetProcess(new Rectangle(240, 160, 400, 300));
        long before = Desktop.ExStyle(source.Handle);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            var windows = new WindowController([]);
            using var pip = new PipWindow(source.Handle, windows) { AllowSwitcherHiding = true };
            pip.ApplyPipPreferences(new PipPreferences { Backend = preference, FrameRate = 30 });
            var problems = new List<string>();
            pip.Problem += problems.Add;
            try
            {
                pip.ShowOnTop();
                pip.Location = new Point(800, 140);
                Pump(600);
                if (preference == PipBackendPreference.Wgc)
                    Assert.True(Desktop.WaitFor(() => pip.BackendKind != PipBackendKind.DwmThumbnail, 10000, () => Pump(20)), pip.DiagnosticSnapshot);
                pip.SetOriginalHidden(true);
                Assert.True(windows.IsHidden(source.Handle));
                source.Animate(60);
                var selector = (PipBackendSelector)typeof(PipWindow).GetField("_thumbnail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pip)!;
                var advanced = (IPipCaptureBackend?)typeof(PipBackendSelector).GetField("_advanced", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(selector);
                long Frames() => advanced is null ? 0 : Interlocked.Read(ref advanced.Diagnostics.Received);
                Pump(400);
                long start = Frames();
                Pump(2000);
                long baseline = Frames() - start;
                if (preference != PipBackendPreference.Dwm && Environment.OSVersion.Version.Build < 22000)
                {
                    // Windows 10 can't capture a window out of the switcher, so this is refused, with a reason.
                    Assert.False(windows.IsOutOfSwitcher(source.Handle));
                    Assert.False(windows.IsOffTaskbar(source.Handle));
                    Assert.Equal([Localization.UiText.T("errors.switcher.windows10")], problems);
                    pip.Close();
                    Assert.False(windows.IsHidden(source.Handle));
                    return;
                }
                // Hiding takes only the taskbar button: the window keeps its styles, and so its own caption buttons.
                Assert.True(windows.IsOffTaskbar(source.Handle));
                Assert.False(windows.IsOutOfSwitcher(source.Handle));
                Assert.Equal(before & WindowController.SwitcherMask, Desktop.ExStyle(source.Handle) & WindowController.SwitcherMask);
                Pump(400);
                start = Frames();
                Pump(2000);
                long hidden = Frames() - start;
                if (advanced is not null)
                {
                    Assert.True(baseline >= 40, pip.DiagnosticSnapshot);
                    Assert.True(hidden >= baseline * .9, $"Baseline {baseline}; switcher hidden {hidden}\n{pip.DiagnosticSnapshot}");
                }
                else
                {
                    var samples = new HashSet<int>();
                    for (int i = 0; i < 12; i++)
                    {
                        Pump(115);
                        Rectangle b = pip.Bounds;
                        samples.Add(Desktop.ColorOnScreen(new Rectangle(b.Left + b.Width / 2, b.Top + b.Height / 2, 8, 8)).ToArgb());
                    }
                    Assert.True(samples.Count >= 4, $"Only {samples.Count} distinct compositor samples.");
                }
                Assert.Empty(problems);
                pip.Close();
                Assert.False(windows.IsHidden(source.Handle));
                Assert.False(windows.IsOutOfSwitcher(source.Handle));
                Assert.False(windows.IsOffTaskbar(source.Handle));
                Assert.Equal(before & WindowController.SwitcherMask, Desktop.ExStyle(source.Handle) & WindowController.SwitcherMask);
                Assert.NotEqual(source.Handle, User32.GetForegroundWindow());
            }
            finally { windows.RestoreAll(); }
        });
    }
}
