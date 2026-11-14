using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using Xunit;
using Xunit.Abstractions;

namespace WinScreen.Tests;

/// <summary>
/// WinScreen's menus on a second display at another scale (roadmap P2-3). Measured in the two-display VM on 14 Nov
/// 2026: beside a 150% display, the window menu's words came out at 7 pt on the 100% one, while its headings kept
/// their size. Windows Forms rescales a menu's font when it moves to a display at another scale, on top of WinScreen
/// drawing at the system's scale. Now each menu's words are the same size, for its display, wherever it opens.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class MixedScaleMenuTests(ITestOutputHelper output)
{
    [MultiDisplayFact]
    public void The_window_menu_has_the_same_size_of_words_on_displays_at_different_scales()
    {
        if (!DisplayScale.Allowed) { Skipped.Because("Changes a display's scale: only in the test VM."); return; }
        int before = DisplayScale.Current;
        try
        {
            DisplayScale.Set(150);
            Thread.Sleep(1500);
            WindowMenuBuilderTests.OnUiThread(() =>
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    using var menu = new ContextMenuStrip();
                    var builder = new WindowMenuBuilder(() => null, (_, _) => { }, (_, _) => { }, _ => true, (_, _) => true, (_, _) => false, () => { })
                        { OpacityPercent = _ => 100, SetOpacityPercent = (_, _) => { } };
                    builder.Populate(menu.Items, false);
                    MenuRenderer.Apply(menu);
                    menu.Show(new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40));
                    for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(20); }
                    // The size the words are drawn at, in pixels, against the size they should be on this display.
                    double drawn = menu.Font.SizeInPoints * Native.DirectText.TypeDpi / 72;
                    double wanted = MenuStyle.MenuFont.SizeInPoints * menu.DeviceDpi / 72;
                    ToolStripItem heading = menu.Items.Cast<ToolStripItem>().First(item => Equals(item.Tag, MenuParts.HeaderTag));
                    double headingDrawn = heading.Font.SizeInPoints * Native.DirectText.TypeDpi / 72;
                    double headingWanted = MenuStyle.MenuHeaderFont.SizeInPoints * menu.DeviceDpi / 72;
                    output.WriteLine($"{screen.DeviceName} at {menu.DeviceDpi} dpi: words {drawn:F1} px (want {wanted:F1}), headings {headingDrawn:F1} px (want {headingWanted:F1})");
                    Assert.InRange(drawn, wanted - 0.6, wanted + 0.6);
                    Assert.InRange(headingDrawn, headingWanted - 0.6, headingWanted + 0.6);
                    menu.Close();
                }
            });
        }
        finally { DisplayScale.Set(before); }
    }
}
