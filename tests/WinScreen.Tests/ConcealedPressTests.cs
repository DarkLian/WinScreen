using WinScreen.Glass;
using WinScreen.Native;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Glass and picture-keeping Dark make the original invisible and draw it again from a window that lets clicks
/// through, so every press has to reach the invisible original: its page, its title bar and its buttons. A window
/// of the tests' own sits behind, so a press that falls through lands there and says so, and nothing else is touched.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class ConcealedPressTests
{
    private static readonly Rectangle Spot = new(300, 220, 480, 320);
    private const int HTCAPTION = 2, HTCLOSE = 20, WM_NCHITTEST = 0x0084;

    /// <summary>Where the window itself says its title bar or close button is, scanning its top row.</summary>
    private static Point Find(nint window, int hit)
    {
        Rectangle bounds = Desktop.BoundsOf(window);
        for (int y = bounds.Top + 4; y < bounds.Top + 60; y += 2)
            for (int x = bounds.Right - 6; x > bounds.Left + 6; x -= 4)
            {
                User32.SendMessageTimeout(window, WM_NCHITTEST, 0, (nint)((y << 16) | (x & 0xFFFF)), 2, 500, out nint answer);
                if (answer == hit)
                    return hit == HTCAPTION ? new Point(bounds.Left + bounds.Width / 3, y) : new Point(x - 6, y);
            }
        throw new InvalidOperationException($"The window answered {hit} nowhere in its top row.");
    }

    /// <summary>Refuses to press anywhere but on the tests' own two windows.</summary>
    private static void RequireOurs(Point point, TargetProcess target, TargetProcess behind)
    {
        nint top = User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);
        if (top != target.Handle && top != behind.Handle && !OwnedByUs(top))
            throw new InvalidOperationException($"Refusing to press: {WindowInfo.ClassName(top)} is at {point}.");
    }

    private static bool OwnedByUs(nint window) =>
        User32.GetWindowThreadProcessId(window, out uint process) != 0 && process == Environment.ProcessId;

    private static string Under(Point point)
    {
        nint top = User32.GetAncestor(Desktop.TopWindowAt(point), User32.GA_ROOT);
        return $"{WindowInfo.ClassName(top)} 0x{top:X} ex=0x{Desktop.ExStyle(top):X} alpha={Desktop.Alpha(top)}";
    }

    private static void Drain(TargetProcess process)
    {
        while (process.NextReport(100) is not null) { }
    }

    /// <summary>Content, title bar and close button, in that order, each judged by what the window did.</summary>
    private static void PressEveryPart(TargetProcess target, TargetProcess behind, Action pump)
    {
        Point page = new(target.Bounds.Left + target.Bounds.Width / 2, target.Bounds.Top + target.Bounds.Height * 2 / 3);
        RequireOurs(page, target, behind);
        string under = Under(page);
        Drain(target);
        Drain(behind);
        Desktop.MoveMouse(page);
        Thread.Sleep(50);
        Desktop.MouseButton(true);
        Desktop.MouseButton(false);
        string? report = null;
        Desktop.WaitFor(() => (report = target.NextReport(50)) is not null, 2000, pump);
        Assert.True(report?.StartsWith("click", StringComparison.Ordinal) == true,
            $"A press on the page didn't reach the window (under the pointer: {under}; behind said {behind.NextReport(200) ?? "nothing"}).");

        Rectangle before = Desktop.BoundsOf(target.Handle);
        Point title = Find(target.Handle, HTCAPTION);
        RequireOurs(title, target, behind);
        under = Under(title);
        Desktop.MoveMouse(title);
        Thread.Sleep(50);
        Desktop.MouseButton(true);
        for (int i = 1; i <= 8; i++)
        {
            Desktop.MoveMouse(new Point(title.X + 60 * i / 8, title.Y + 40 * i / 8));
            Thread.Sleep(20);
            pump();
        }
        Thread.Sleep(150);
        Desktop.MouseButton(false);
        Assert.True(Desktop.WaitFor(() => Desktop.BoundsOf(target.Handle).Location != before.Location, 2000, pump),
            $"Dragging the title bar didn't move the window (under the pointer: {under}).");

        Point close = Find(target.Handle, HTCLOSE);
        RequireOurs(close, target, behind);
        under = Under(close);
        Desktop.MoveMouse(close);
        Thread.Sleep(100);
        Desktop.MouseButton(true);
        Desktop.MouseButton(false);
        Assert.True(Desktop.WaitFor(() => !User32.IsWindow(target.Handle), 3000, pump),
            $"The close button didn't close the window (under the pointer: {under}).");
    }

    /// <summary>The core, with no graphics card needed: the original made invisible the way both effects do it.</summary>
    [Fact]
    public void Every_part_of_a_window_concealed_for_an_effect_still_takes_a_press()
    {
        using var behind = new TargetProcess(new Rectangle(Spot.Left - 60, Spot.Top - 60, Spot.Width + 220, Spot.Height + 160), "1E64C8", "WinScreen behind");
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            try
            {
                windows.ConcealSolid(target.Handle);
                Assert.True(WindowInfo.IsConcealed(target.Handle));
                PressEveryPart(target, behind, () => Application.DoEvents());
            }
            finally { windows.RestoreAll(); }
        });
    }

    [GraphicsCardTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_part_of_a_window_under_a_picture_keeping_effect_still_takes_a_press(bool dark)
    {
        using var behind = new TargetProcess(new Rectangle(Spot.Left - 60, Spot.Top - 60, Spot.Width + 220, Spot.Height + 160), "1E64C8", "WinScreen behind");
        using var target = new TargetProcess(Spot, "F0F0F0");
        target.UseFrame();
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Desktop.GainForegroundRights();
            Desktop.Activate(target.Handle);
            var windows = new WindowController([]);
            using var effects = new ContentEffects(windows);
            try
            {
                Assert.True(effects.Toggle(target.Handle, dark ? ContentLook.Dark : ContentLook.Glass));
                long end = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
                PressEveryPart(target, behind, () => Application.DoEvents());
            }
            finally
            {
                effects.TurnAllOff();
                windows.RestoreAll();
            }
        });
    }
}
