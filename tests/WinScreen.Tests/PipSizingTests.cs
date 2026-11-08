using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

[Trait("Category", "RealWindows")]
public sealed class PipSizingTests
{
    [Fact]
    public void Native_hit_tests_identify_all_edges_and_corners()
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            view.ShowOnTop();
            view.Location = new Point(700, 200);
            var cases = new[] { (1, 1, 13), (view.Width / 2, 1, 12), (view.Width - 1, 1, 14),
                (1, view.Height / 2, 10), (view.Width - 1, view.Height / 2, 11),
                (1, view.Height - 1, 16), (view.Width / 2, view.Height - 1, 15), (view.Width - 1, view.Height - 1, 17) };
            foreach (var (x, y, expected) in cases)
            {
                var point = new Point(view.Left + x, view.Top + y);
                Assert.NotEqual(0, User32.SendMessageTimeout(view.Handle, User32.WM_NCHITTEST, 0,
                    (nint)((point.Y << 16) | (point.X & 0xffff)), 2, 100, out nint result));
                Assert.Equal((nint)expected, result);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_sizing_keeps_the_shown_crop_ratio_only_when_selected(bool keep)
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([]));
            view.ShowOnTop();
            view.SetCrop(new Rectangle(50, 50, 200, 100));
            view.Overrides = view.Overrides with { KeepAspectRatio = keep };
            nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
            try
            {
                for (int edge = 1; edge <= 8; edge++)
                {
                    var original = new Rect(700, 200, 1103, 477);
                    Marshal.StructureToPtr(original, memory, false);
                    User32.SendMessageTimeout(view.Handle, 0x0214, edge, memory, 2, 100, out _);
                    Rect result = Marshal.PtrToStructure<Rect>(memory);
                    if (keep)
                    {
                        Assert.InRange(Math.Abs((result.Right - result.Left - 4) / 2.0 - (result.Bottom - result.Top - 4)), 0, 1);
                        if (edge is 1 or 4 or 7) Assert.Equal(original.Right, result.Right);
                        if (edge is 3 or 4 or 5) Assert.Equal(original.Bottom, result.Bottom);
                    }
                    else Assert.Equal(original, result);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Interactive")]
    public void Dragging_a_sizing_edge_resizes_the_view_including_during_direct_use(bool direct)
    {
        using var source = new TargetProcess(new Rectangle(200, 160, 400, 300));
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            using var view = new PipWindow(source.Handle, new WindowController([])) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(700, 300);
            view.SetInteractive(direct);
            Rectangle before = view.Bounds;
            Point home = Desktop.CursorPosition();
            Exception? failure = null;
            var driver = new Thread(() =>
            {
                try
                {
                    if (direct)
                    {
                        Desktop.SlideMouse(new Point(before.Left + before.Width / 2, before.Top + before.Height / 2));
                        Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null));
                    }
                    Point edge = new(before.Right - 1, before.Top + before.Height / 2);
                    Desktop.MoveMouse(edge);
                    Thread.Sleep(100);
                    Desktop.MouseButton(true);
                    Thread.Sleep(80);
                    Desktop.SlideMouse(edge + new Size(80, 0));
                    Thread.Sleep(200);
                    Desktop.MouseButton(false);
                }
                catch (Exception e) { failure = e; }
                finally { Desktop.MouseButton(false); Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
            Assert.True(view.Width > before.Width + 40, $"Before {before}, after {view.Bounds}");
            Assert.InRange(Math.Abs((view.Width - 4) * .75 - (view.Height - 4)), 0, 1);
        });
    }
}
