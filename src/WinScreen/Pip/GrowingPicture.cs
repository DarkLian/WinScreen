using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Pip;

/// <summary>
/// Softens the jump when a hidden original comes up for a drag (12.6, the owner, 18 Nov): a picture of what the view
/// showed grows from the view to where the window now is, and only then does the window show. The window takes the
/// press at once, nearly see-through, so nothing waits on this, and the pressed spot stays under the pointer all the
/// way (both ends have it there). The picture runs on its own thread: the view's work meanwhile, finding the pressed
/// file again, can't stall it. Skipped when Windows' animation effects are off.
/// </summary>
internal sealed class GrowingPicture : IDisposable
{
    internal const int Milliseconds = 160;
    private const int FrameMilliseconds = 15, ReadyWithin = 1000;

    private readonly Bitmap _picture;
    private readonly Rectangle _from;
    private readonly Func<Rectangle?> _target;
    private readonly Action _reveal;
    private readonly ManualResetEventSlim _shown = new();
    private volatile bool _stopped;

    private GrowingPicture(Bitmap picture, Rectangle from, Func<Rectangle?> target, Action reveal)
    {
        _picture = picture;
        _from = from;
        _target = target;
        _reveal = reveal;
    }

    /// <summary>
    /// Takes the picture at <paramref name="from"/> on the screen and starts growing it towards
    /// <paramref name="target"/> (null while the window isn't out yet; once it was, null stops it). At the end
    /// <paramref name="reveal"/> runs on the picture's thread, then the picture goes. Null, with
    /// <paramref name="reveal"/> run at once, when animations are off or the picture can't be taken.
    /// </summary>
    internal static GrowingPicture? Start(Rectangle from, Func<Rectangle?> target, Action reveal)
    {
        if (!WindowsAnimations.On || from.Width < 8 || from.Height < 8)
        {
            reveal();
            return null;
        }
        Bitmap picture = new(from.Width, from.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(picture);
            // Plain SourceCopy: .NET refuses SourceCopy | CaptureBlt as an undefined value (the owner's crash, 18 Nov), and
            // with desktop composition the screen's picture already holds see-through windows.
            graphics.CopyFromScreen(from.Location, Point.Empty, from.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            // A secure desktop or a locked screen: no picture to take. Never a reason for the drag to fail.
            Diagnostics.Log.Swallowed(failure);
            picture.Dispose();
            reveal();
            return null;
        }
        var growing = new GrowingPicture(picture, from, target, reveal);
        var thread = new Thread(growing.Run) { IsBackground = true, Name = "WinScreen growing picture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        growing._shown.Wait(100); // On the screen before the view steps aside, so nothing blinks.
        return growing;
    }

    /// <summary>Where the picture is at <paramref name="progress"/> (0 to 1) of the way: quick at first, settling at the end.</summary>
    internal static Rectangle Frame(Rectangle from, Rectangle to, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * eased);
        return Rectangle.FromLTRB(Lerp(from.Left, to.Left), Lerp(from.Top, to.Top), Lerp(from.Right, to.Right), Lerp(from.Bottom, to.Bottom));
    }

    /// <summary>Stops it where it is, without revealing anything: the window went back. Doesn't wait.</summary>
    public void Dispose() => _stopped = true;

    private void Run()
    {
        var window = new StandIn();
        bool started = false;
        try
        {
            window.Create(_from);
            Draw(window.Handle, _from);
            User32.ShowWindow(window.Handle, SW_SHOWNOACTIVATE);
            _shown.Set();
            var waiting = Stopwatch.StartNew();
            Stopwatch? clock = null;
            Rectangle last = _from;
            while (!_stopped)
            {
                Application.DoEvents();
                Rectangle? to = _target();
                if (to is null)
                {
                    if (started || waiting.ElapsedMilliseconds > ReadyWithin) { _stopped = true; break; }
                }
                else
                {
                    started = true;
                    clock ??= Stopwatch.StartNew();
                    double progress = clock.Elapsed.TotalMilliseconds / Milliseconds;
                    Rectangle frame = Frame(_from, to.Value, progress);
                    if (frame != last && frame.Width > 0 && frame.Height > 0)
                    {
                        Draw(window.Handle, frame);
                        last = frame;
                    }
                    if (progress >= 1) break;
                }
                Thread.Sleep(FrameMilliseconds);
            }
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or ArgumentException or OutOfMemoryException or ExternalException)
        {
            Diagnostics.Log.Swallowed(failure);
        }
        finally
        {
            _shown.Set();
            // The window first, then the picture off: both land in the same frame or the window a frame early.
            if (!_stopped && started)
            {
                try { _reveal(); }
                catch (Windows.WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
            }
            window.DestroyHandle();
            _picture.Dispose();
            _shown.Dispose();
        }
    }

    private void Draw(nint window, Rectangle frame)
    {
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_picture, new Rectangle(Point.Empty, frame.Size));
        }
        LayeredPicture.Show(window, bitmap, 255, frame.Location);
    }

    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>A see-through, click-through window above everything, never taking the focus or the taskbar.</summary>
    private sealed class StandIn : NativeWindow
    {
        internal void Create(Rectangle bounds) => CreateHandle(new CreateParams
        {
            Caption = "WinScreen.GrowingPicture",
            X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = (int)(User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOPMOST),
        });
    }
}
