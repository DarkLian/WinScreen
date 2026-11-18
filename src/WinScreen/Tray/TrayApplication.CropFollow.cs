using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Tray;

// A crop made by clicking a control follows it when the window is resized: found again by the same path down the
// window's controls. Not found, the crop keeps its rectangle.
public sealed partial class TrayApplication
{
    private readonly Dictionary<nint, CropControl> _cropControls = [];
    private bool _cropLooking;

    private sealed record CropControl(FoundElement Element, WindowIdentity Identity, Size Size);

    private void RememberCropControl(nint window, FoundElement? element, WindowIdentity identity)
    {
        if (element is null || element.Window != window || !User32.GetWindowRect(window, out Rect r))
        {
            _cropControls.Remove(window);
            return;
        }
        _cropControls[window] = new CropControl(element, identity, new Size(r.Right - r.Left, r.Bottom - r.Top));
    }

    // One window at a time and off this thread, since an app can be slow to answer.
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
