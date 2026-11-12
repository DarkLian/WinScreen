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
                // Inside the view's own border, which Windows 11 colours differently while the view is the active window.
                Rectangle Corner()
                {
                    Rectangle b = Rectangle.Inflate(pip.Bounds, -2, -2);
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
                // Share of pixels that clearly changed: the caption buttons are thin lines, so an average hides them.
                Assert.True(same < 0.5 && changed > 2 && changed > 5 * same,
                    $"Title bar corner pixels changed from the shown original: {same:F2}% when hidden, {changed:F2}% as a tool window.");
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

    /// <summary>Percentage of pixels that differ clearly (by more than 60 over the three channels).</summary>
    private static double Difference(Bitmap a, Bitmap b)
    {
        int changed = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 60) changed++;
            }
        return 100.0 * changed / (a.Width * a.Height);
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
                // Hiding the original changes what the capture needs, so the view may replace its capture: always
                // read the one in use, and count only while the same one keeps running.
                FieldInfo field = typeof(PipBackendSelector).GetField("_advanced", BindingFlags.NonPublic | BindingFlags.Instance)!;
                IPipCaptureBackend? Advanced() => (IPipCaptureBackend?)field.GetValue(selector);
                long Count(int milliseconds)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        IPipCaptureBackend? before = Advanced();
                        long first = before is null ? 0 : Interlocked.Read(ref before.Diagnostics.Received);
                        Pump(milliseconds);
                        IPipCaptureBackend? after = Advanced();
                        if (ReferenceEquals(before, after) || attempt == 3)
                            return after is null ? 0 : Interlocked.Read(ref after.Diagnostics.Received) - (ReferenceEquals(before, after) ? first : 0);
                    }
                }
                IPipCaptureBackend? advanced = Advanced();
                Pump(400);
                long baseline = Count(2000);
                advanced = Advanced();
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
                long hidden = Count(2000);
                if (advanced is not null)
                {
                    Assert.True(baseline >= 40, $"Baseline {baseline}\n{pip.DiagnosticSnapshot}");
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
