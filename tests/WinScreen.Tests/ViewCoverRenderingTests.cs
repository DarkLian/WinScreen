using WinScreen.Pip;
using WinScreen.Tests.Support;
using Xunit;

namespace WinScreen.Tests;

// Covers are blacked out of the captured frame itself, so the drawing, a copy and a frozen frame all have them, and
// taking one away brings the part back without waiting for the window to change.
[Trait("Category", "RealWindows")]
public sealed class ViewCoverRenderingTests
{
    [WgcFact]
    public void Covered_pixels_are_black_in_the_drawing_and_the_copy_and_come_back_when_uncovered()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var destination = new Form { ClientSize = new(140, 110), Location = new(900, 180),
                    StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false };
                destination.Show();
                Application.DoEvents();
                using var renderer = new D3D11Renderer(destination.Handle, WinScreen.Native.User32.MonitorFromWindow(destination.Handle),
                    new(), new PipDiagnostics(), false);
                var cover = new Rectangle(20, 30, 17, 11);
                var crop = new Rectangle(0, 0, 120, 90);
                // Covers set before the first frame apply to it.
                Assert.False(renderer.SetCovers([cover]));
                PipTurnRenderingTests.Load(renderer, 120, 90);
                Check(PipTurnRenderingTests.Draw(renderer, destination, ViewTurn.Upright, crop, crop.Size), [cover]);
                using (Bitmap copy = ReadBack(renderer)) CheckCopy(copy, [cover]);

                // Taken away and changed with no new frame: the part as the frame had it comes back.
                var other = new Rectangle(70, 10, 9, 40);
                Assert.True(renderer.SetCovers([other]));
                Check(PipTurnRenderingTests.Draw(renderer, destination, ViewTurn.Upright, crop, crop.Size), [other]);
                Assert.True(renderer.SetCovers([]));
                Check(PipTurnRenderingTests.Draw(renderer, destination, ViewTurn.Upright, crop, crop.Size), []);
                using (Bitmap copy = ReadBack(renderer)) CheckCopy(copy, []);

                // Covered from the first frame on; a new frame is covered as it arrives.
                renderer.SetCovers([cover, other]);
                PipTurnRenderingTests.Load(renderer, 120, 90);
                Check(PipTurnRenderingTests.Draw(renderer, destination, ViewTurn.Upright, crop, crop.Size), [cover, other]);
                // Turned, the black is turned with the picture.
                var turn = new ViewTurn(1, false);
                int[,] turned = PipTurnRenderingTests.Draw(renderer, destination, turn, crop, crop.Size);
                Point inside = turn.ToView(new Point(cover.X + 3, cover.Y + 3), crop.Size);
                Assert.Equal(0, turned[inside.Y + 2, inside.X + 2]);
                destination.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());

        static void Check(int[,] drawn, Rectangle[] covers)
        {
            for (int y = 0; y < 90; y++)
                for (int x = 0; x < 120; x++)
                {
                    int expected = covers.Any(cover => cover.Contains(x, y)) ? 0 : PipTurnRenderingTests.Pattern(x, y) & 0xFFFFFF;
                    Assert.True(expected == drawn[y + 2, x + 2], $"({x},{y}) shows {drawn[y + 2, x + 2]:X6}, expected {expected:X6}");
                }
        }

        static void CheckCopy(Bitmap copy, Rectangle[] covers)
        {
            Assert.Equal(new Size(120, 90), copy.Size);
            for (int y = 0; y < 90; y += 3)
                for (int x = 0; x < 120; x += 3)
                {
                    int expected = covers.Any(cover => cover.Contains(x, y)) ? 0 : PipTurnRenderingTests.Pattern(x, y) & 0xFFFFFF;
                    Assert.Equal(expected, copy.GetPixel(x, y).ToArgb() & 0xFFFFFF);
                }
        }

        static Bitmap ReadBack(D3D11Renderer renderer)
        {
            renderer.BeginReadBack();
            renderer.EndWork();
            for (int i = 0; i < 200; i++)
            {
                if (renderer.TryReadBack() is { } copy) return copy;
                Thread.Sleep(5);
            }
            throw new TimeoutException("The copy never arrived.");
        }
    }
}
