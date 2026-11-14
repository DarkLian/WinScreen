using System.Reflection;
using WinScreen.Anchors;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// The toolbar, Anchors and a view's state chip on a second display at another scale (roadmap P2-3, step 7.2): each is the size its
/// display asks for, wherever it was before. The menus have their own test (<see cref="MixedScaleMenuTests"/>).
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class MixedScaleSurfaceTests(ITestOutputHelper output)
{
    /// <summary>The scale of the display a point is on, read from a window put there.</summary>
    private static int DpiAt(Point point)
    {
        using var probe = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = point, Size = new Size(20, 20) };
        probe.Show();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
        return probe.DeviceDpi;
    }

    private static void Pump(int milliseconds)
    {
        for (int waited = 0; waited < milliseconds; waited += 20) { Application.DoEvents(); Thread.Sleep(20); }
    }

    private static void WithBothScales(Action body)
    {
        if (!DisplayScale.Allowed) { Skipped.Because("Changes a display's scale: only in the test VM."); return; }
        int before = DisplayScale.Current;
        try
        {
            DisplayScale.Set(150);
            Thread.Sleep(1500);
            body();
        }
        finally { DisplayScale.Set(before); }
    }

    [MultiDisplayFact]
    public void The_toolbar_is_the_size_its_display_asks_for_after_showing_on_another()
    {
        WithBothScales(() => WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var toolbar = new WindowToolbar(_ => true, (_, _) => false, _ => true, (_, _) => { });
            toolbar.Configure(new ToolbarPreferences());
            MethodInfo arrange = typeof(WindowToolbar).GetMethod("Arrange", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // Back and forth, as the bar follows windows from one display to the other.
            Screen[] order = [.. Screen.AllScreens, .. Screen.AllScreens];
            foreach (Screen screen in order)
            {
                Point at = new(screen.WorkingArea.Left + 60, screen.WorkingArea.Top + 60);
                int dpi = DpiAt(at);
                toolbar.Hide();
                // As Watch does: arranged for the window's display, then moved there and shown.
                arrange.Invoke(toolbar, [dpi]);
                toolbar.Location = at;
                toolbar.Show();
                Pump(400);
                int wanted = Dpi.Scale(34, dpi);
                Button first = toolbar.Controls.OfType<Button>().First(button => button.Visible);
                output.WriteLine($"{screen.DeviceName} at {dpi} dpi: bar {toolbar.ClientSize}, button {first.Size}, want {wanted}");
                Assert.InRange(toolbar.ClientSize.Height, wanted - 1, wanted + 1);
                Assert.InRange(first.Height, wanted - 1, wanted + 1);
                Assert.InRange(first.Width, wanted - 1, wanted + 1);
                Assert.True(screen.Bounds.Contains(toolbar.Location), $"The bar left the display it was put on: {toolbar.Bounds}.");
            }
        }));
    }

    [MultiDisplayFact]
    public void An_anchor_is_the_size_its_windows_display_asks_for()
    {
        WithBothScales(() =>
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                using var target = new TargetProcess(new Rectangle(screen.WorkingArea.Left + 100, screen.WorkingArea.Top + 100, 420, 300));
                WindowMenuBuilderTests.OnUiThread(() =>
                {
                    using var anchors = new AnchorSet();
                    Assert.True(anchors.Toggle(target.Handle));
                    Pump(600);
                    AnchorButton anchor = ((Dictionary<nint, AnchorButton>)typeof(AnchorSet).GetField("_anchors",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(anchors)!)[target.Handle];
                    int dpi = Dpi.Of(target.Handle);
                    int wanted = Dpi.Scale(new AnchorPreferences().Size, dpi);
                    output.WriteLine($"{screen.DeviceName}: window at {dpi} dpi, Anchor {anchor.ClientSize} on {Screen.FromControl(anchor).DeviceName} at {anchor.DeviceDpi} dpi, want {wanted}");
                    Assert.InRange(anchor.ClientSize.Width, wanted - 1, wanted + 1);
                    Assert.InRange(anchor.ClientSize.Height, wanted - 1, wanted + 1);
                });
            }
        });
    }

    /// <summary>
    /// A view's state chip, shown on one display and then the other, with the same words and with new ones: its text is
    /// drawn at its display's scale and fits inside it.
    /// </summary>
    [MultiDisplayFact]
    public void A_views_state_chip_fits_its_words_on_either_display()
    {
        WithBothScales(() => WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new Form { ShowInTaskbar = false };
            using var chip = new Pip.PipStateChip(view);
            int baseHeight = TextRenderer.MeasureText("Ag", Control.DefaultFont).Height;
            Screen[] order = [.. Screen.AllScreens, .. Screen.AllScreens];
            Pip.PipChipState[] states = [Pip.PipChipState.Starting, Pip.PipChipState.Starting, Pip.PipChipState.SourceMinimized, Pip.PipChipState.SourceMinimized];
            for (int i = 0; i < order.Length; i++)
            {
                Screen screen = order[i];
                Point at = new(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 200);
                int dpi = DpiAt(at);
                chip.Hide();
                chip.Show(states[i % states.Length], at);
                Pump(400);
                Size words = TextRenderer.MeasureText(chip.Text, chip.Font);
                output.WriteLine($"{screen.DeviceName} at {dpi} dpi: chip {chip.ClientSize} at {chip.DeviceDpi} dpi, words {words}, font {chip.Font.Height}px");
                Assert.Equal(dpi, chip.DeviceDpi);
                Assert.InRange(TextRenderer.MeasureText("Ag", chip.Font).Height, baseHeight * dpi / 96 - 3, baseHeight * dpi / 96 + 3);
                Assert.True(chip.ClientSize.Width >= words.Width && chip.ClientSize.Height >= words.Height,
                    $"The words {words} don't fit the chip {chip.ClientSize} on {screen.DeviceName}.");
            }
        }));
    }
}
