using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Tray;

/// <summary>
/// A crop made by clicking a control follows that control when the window is resized (roadmap P2-11, step 8.1): the
/// control is found again by the same way down the window's controls, and the crop moves to where it is now. When it
/// can't be found, the crop keeps its rectangle, as a crop dragged out by hand always does.
/// </summary>
public sealed partial class TrayApplication
{
    private readonly Dictionary<nint, CropControl> _cropControls = [];
    private bool _cropLooking;

    /// <summary>The control a window is cropped to, the window it belongs to, and the window's size when last fitted.</summary>
    private sealed record CropControl(FoundElement Element, WindowIdentity Identity, Size Size);

    /// <summary>Remembers the control a crop was made from, or forgets it for a crop dragged out by hand.</summary>
    private void RememberCropControl(nint window, FoundElement? element, WindowIdentity identity)
    {
        if (element is null || element.Window != window || !User32.GetWindowRect(window, out Rect r))
        {
            _cropControls.Remove(window);
            return;
        }
        _cropControls[window] = new CropControl(element, identity, new Size(r.Right - r.Left, r.Bottom - r.Top));
    }

    /// <summary>
    /// From the watch, four times a second: a window cropped to a control that has changed size is looked at again,
    /// one window at a time and away from this thread, since an app can be slow to answer.
    /// </summary>
    private void FollowCropControls()
    {
        if (_cropControls.Count == 0 || _cropLooking) return;
        foreach ((nint window, CropControl follow) in _cropControls.ToArray())
        {
            if (!follow.Identity.Matches() || !_windows.IsCropped(window))
            {
                _cropControls.Remove(window);
                continue;
            }
            if (!User32.GetWindowRect(window, out Rect r) || new Size(r.Right - r.Left, r.Bottom - r.Top) == follow.Size)
                continue;
            _cropLooking = true;
            Task.Run(() =>
            {
                Rectangle? now = null;
                try { now = ElementFinder.Locate(follow.Element); }
                catch (Exception failure) when (failure is InvalidCastException or System.Runtime.InteropServices.COMException)
                {
                    Diagnostics.Log.Swallowed(failure);
                }
                if (_toolbar.IsHandleCreated) _toolbar.BeginInvoke(() => Refit(window, follow, now));
                else _cropLooking = false;
            });
            return;
        }
    }

    /// <summary>Crops the window to where its control is now, or leaves the crop as it is when the control is gone.</summary>
    private void Refit(nint window, CropControl follow, Rectangle? control)
    {
        _cropLooking = false;
        if (!_cropControls.TryGetValue(window, out CropControl? current) || !ReferenceEquals(current, follow) ||
            !follow.Identity.Matches() || !_windows.IsCropped(window) || !User32.GetWindowRect(window, out Rect r))
            return;
        var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        _cropControls[window] = follow with { Size = bounds.Size };
        Diagnostics.Log.Event("crop.followed", ("found", control is not null));
        if (control is not { } found) return;
        Rectangle part = Rectangle.Intersect(new Rectangle(found.Location - (Size)bounds.Location, found.Size), new Rectangle(Point.Empty, bounds.Size));
        if (part.Width <= 0 || part.Height <= 0) return;
        try { _windows.Crop(window, part); }
        catch (Exception failure) when (IsExpected(failure)) { Diagnostics.Log.Swallowed(failure); }
    }
}
