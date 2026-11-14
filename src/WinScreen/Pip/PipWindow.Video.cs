using System.Drawing.Imaging;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

/// <summary>
/// Show the largest video (roadmap step 8.2): the view crops to the largest moving picture in its source and keeps
/// following it, looking again every few seconds. It looks at the captured picture, since browsers don't tell
/// accessibility where a playing video is. With Windows' own thumbnail, which hands over no frames (on Windows 10 a
/// view of a hidden window uses it), the view reads its own picture from the screen instead: coarser, but the same idea.
/// </summary>
public sealed partial class PipWindow
{
    private const int SamplesPerLook = 10, SampleMilliseconds = 150, PauseMilliseconds = 2500;

    private readonly System.Windows.Forms.Timer _videoTimer = new() { Interval = SampleMilliseconds };
    private MotionFinder _motion = new();
    private bool _followingVideo, _videoFound, _sampleOut, _cropByFollow, _videoTimerHooked, _fromScreen;
    private int _samples;
    private long _nextLook;

    /// <summary>True while the view follows the largest moving picture in its source.</summary>
    internal bool FollowsVideo => _followingVideo;

    internal void FollowLargestVideo(bool on)
    {
        if (on == _followingVideo) return;
        _followingVideo = on;
        _videoFound = false;
        _samples = 0;
        _motion = new MotionFinder();
        _nextLook = 0;
        if (!_videoTimerHooked)
        {
            _videoTimer.Tick += (_, _) => AskForFrame();
            _videoTimerHooked = true;
        }
        _videoTimer.Enabled = on;
        Diagnostics.Log.Event("pip.video", ("on", on));
    }

    private void AskForFrame()
    {
        if (!_followingVideo || _sampleOut || _suspended || Environment.TickCount64 < _nextLook) return;
        if (_thumbnail is not { } capture) return;
        if (capture.Kind == PipBackendKind.DwmThumbnail)
        {
            Took(ScreenSample(), fromScreen: true);
            return;
        }
        _sampleOut = true;
        capture.RequestSample(sample =>
        {
            try { BeginInvoke(() => Took(sample, fromScreen: false)); }
            catch (InvalidOperationException closed) { Diagnostics.Log.Swallowed(closed); } // The view closed meanwhile.
        });
    }

    /// <summary>The view's own picture as it is on screen, or null while it can't be read.</summary>
    private unsafe FrameSample? ScreenSample()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return null;
        Rectangle area = RectangleToScreen(PictureArea);
        if (area.Width < 2 * FrameSample.Tile || area.Height < 2 * FrameSample.Tile) return null;
        try
        {
            using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, area.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { return FrameSample.From((byte*)data.Scan0, data.Stride, area.Size); }
            finally { bitmap.UnlockBits(data); }
        }
        catch (System.ComponentModel.Win32Exception locked)
        {
            Diagnostics.Log.Swallowed(locked); // The screen can't be read, such as while it is locked.
            return null;
        }
    }

    private void Took(FrameSample? sample, bool fromScreen)
    {
        if (!fromScreen) _sampleOut = false;
        if (!_followingVideo || IsDisposed || sample is null) return;
        if (fromScreen != _fromScreen)
        {
            _fromScreen = fromScreen;
            _motion = new MotionFinder();
            _samples = 0;
        }
        _motion.Add(sample);
        if (++_samples < SamplesPerLook) return;

        Rectangle? moving = _motion.Largest();
        Size surface = _motion.Surface;
        _motion = new MotionFinder();
        _samples = 0;
        _nextLook = Environment.TickCount64 + PauseMilliseconds;
        Rectangle crop = Rectangle.Empty;
        if (moving is { } found && fromScreen && _thumbnail is { } shown)
        {
            // Found in the view's picture: mapped back to the window as a crop drawn by hand is.
            Rectangle picture = PictureArea;
            found.Offset(picture.Location);
            crop = PipGeometry.SelectionToSource(found, picture, ShownPart(shown.SourceSize));
            crop.Offset(-_whole.X, -_whole.Y);
        }
        else if (moving is { } inFrame)
        {
            Rectangle whole = _origin.Whole(surface);
            crop = Rectangle.Intersect(inFrame, whole);
            crop.Offset(-whole.X, -whole.Y);
        }
        if (crop.Width > 0 && crop.Height > 0)
        {
            // A video that only moved a little, or one that paused, keeps its crop.
            if (!_videoFound || Overlap(crop, _crop) < 0.85)
            {
                _cropByFollow = true;
                try { SetCrop(crop); }
                finally { _cropByFollow = false; }
                Diagnostics.Log.Event("pip.video.found", ("width", crop.Width), ("height", crop.Height), ("again", _videoFound));
            }
            _videoFound = true;
        }
        else if (!_videoFound)
        {
            FollowLargestVideo(false);
            Notice?.Invoke(T("pip.video.none"));
        }
    }

    /// <summary>Called by every crop: one made any other way ends following.</summary>
    private void CropChanging()
    {
        if (_followingVideo && !_cropByFollow) FollowLargestVideo(false);
    }

    internal static double Overlap(Rectangle a, Rectangle b)
    {
        Rectangle both = Rectangle.Intersect(a, b);
        if (both.IsEmpty) return 0;
        double shared = (double)both.Width * both.Height;
        return shared / ((double)a.Width * a.Height + (double)b.Width * b.Height - shared);
    }
}
