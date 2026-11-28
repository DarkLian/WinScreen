using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

// A drag from elsewhere handed to the original, and one picked up from the view (File Explorer, browsers).
public sealed partial class PipWindow
{
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = DragDropEffects.None;
        AskForDrop();
    }

    // A drag that came in over the border reaches the picture later.
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = DragDropEffects.None;
        AskForDrop();
    }

    private bool MayTakeDrop => _dropSince == 0 && !_pickingUp && _hideItem.Checked && !_usingOriginal && !_suspended && _thumbnail is not null &&
        _origin.IsWindow && _windows.IsParked(_source) && !_lastShown.IsEmpty;

    private void AskForDrop()
    {
        if (_dropAsked || !MayTakeDrop) return;
        _dropAsked = true;
        // After this call returns: the drag is in the middle of asking this window.
        BeginInvoke(() =>
        {
            _dropAsked = false;
            HandOverDrop();
        });
    }

    private bool _dropAsked;

    private readonly DragArrival _dragArrival = new(DragAndDrop.Running, DragAndDrop.RunningInProcess);

    private void FollowDragArriving()
    {
        Point pointer = Cursor.Position;
        if (_dragArrival.Step(Environment.TickCount64, (User32.GetAsyncKeyState(0x01) & 0x8000) != 0, () => PressedOn(pointer),
                () => MayTakeDrop && RectangleToScreen(PictureArea).Contains(pointer)))
            HandOverDrop();
    }

    // Where the button went down, as near as the timer saw: on one of WinScreen's own windows (moving a view, its menu, a
    // visit's press) it isn't a drag from elsewhere.
    private DragArrival.Press PressedOn(Point pointer)
    {
        nint under = User32.WindowFromPoint(new NativePoint { X = pointer.X, Y = pointer.Y });
        uint thread = under == 0 ? 0 : User32.GetWindowThreadProcessId(under, out _);
        User32.GetWindowThreadProcessId(under, out uint process);
        return new(under != 0 && process != (uint)Environment.ProcessId && !Bounds.Contains(pointer), thread, process,
            under == 0 ? "" : WindowInfo.ClassName(under));
    }

    private void HandOverDrop()
    {
        if (IsDisposed || _dropSince != 0 || !_origin.IsAvailable || !_windows.IsParked(_source) || !DragButtonsDown()) return;
        ViewState.Modes before = CurrentModes();
        if (ViewState.Apply(before, ViewState.Request.DropStarts) == before) return;
        // Where the pointer is now: it may have gone on into the view since the drag came in at its edge.
        Point pointer = Cursor.Position;
        if (!RectangleToScreen(PictureArea).Contains(pointer)) return;
        EndLive();
        Point spot = SourceSpotAt(pointer);
        if (!User32.GetWindowRect(_source, out Rect bounds)) return;
        Rectangle at = DragAndDrop.DropPlace(pointer, spot, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
            Screen.FromPoint(pointer).WorkingArea);
        StartGrowing();
        try { _windows.ShowParkedForDrop(_source, at.Location, holdReveal: _growing is not null); }
        catch (WindowOperationException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            StopGrowing();
            return;
        }
        _dropSince = Environment.TickCount64;
        _dropReleasedAt = 0;
        _dropOutsideSince = 0;
        _dropTookFocus = false;
        _dropFromView = false;
        _foregroundBeforeDrop = User32.GetForegroundWindow();
        StepAside(true);
        CheckState();
        CheckRequest(before, ViewState.Request.DropStarts);
        Diagnostics.Log.Event("view.dropHandedOver", ("program", Diagnostics.LogValue.ProgramOf(_source)));
    }

    // Windows' own drag and drop follows the real pointer and can't be passed on as messages. In a page the same
    // press-and-move otherwise selects text, which works through the view as it is.
    private Compatibility.DragHandOver HandsOverDrags => _origin.IsWindow ? PipEnhancedHover.DragsOf(_source) : Compatibility.DragHandOver.None;

    private void PickUpDrag(Point pressed)
    {
        if (IsDisposed || _live is not { } live || _dropSince != 0 || !_origin.IsAvailable || !_windows.IsParked(_source)) return;
        ViewState.Modes modesBefore = CurrentModes();
        if (ViewState.Apply(modesBefore, ViewState.Request.DropStarts) == modesBefore) return;
        Point spot = SourceSpotAt(pressed);
        if (!User32.GetWindowRect(_source, out Rect bounds)) return;
        string item = live.PressedItem;
        nint before = _foregroundBeforeLive;
        EndLive(keepFocus: true); // The hook goes first: the press given below must reach the window.
        // The press was the hook's, so Windows doesn't know the button is down: let go meanwhile, there is nothing to
        // hand on.
        if (live.HandedOverReleased) return;
        // The original is resized below, and the view would show that.
        _pickingUp = true;
        StepAside(true);
        // Never shown: nearly see-through, it takes the press, and goes back out of sight once Windows carries the drag
        // (FollowDrop). Shown at its own size around the hand, it covered where a quick hand let go.
        const bool hold = true;
        Point pointer = Cursor.Position;
        Rectangle area = Screen.FromPoint(pointer).WorkingArea;
        Point? press = null;
        bool itemFound = false;
        long pickupStarted = Environment.TickCount64;
        try
        {
            // Never filling the screen: a maximized original, or one at the view's smaller size, lays itself out
            // differently, so the pressed item is found anew and the window moves to bring it under the pointer.
            if (item.Length > 0 && live.Identity is { } identity && _windows.NormalSize(_source) is { } normal)
            {
                Rectangle place = DragAndDrop.PickupPlace(normal, pointer, area);
                _windows.ShowParkedForDrop(_source, place.Location, place.Size, hold);
                for (long until = Environment.TickCount64 + PickupFindFor; press is null && Environment.TickCount64 < until;)
                {
                    Application.DoEvents();
                    // Messages run here: the view can close, or its original go back, before the item is found.
                    if (IsDisposed || !_windows.IsParked(_source)) break;
                    // On its name, near the start of the row: elsewhere in a row Windows 10 starts a selection box
                    // instead.
                    if (PipControlInput.Shared.ItemBounds(identity, item, 150) is { } found &&
                        new Point(found.Left + Math.Min(28, found.Width / 2), found.Top + found.Height / 2) is var onName &&
                        place.Contains(onName))
                    {
                        // Under the hand as it is now: it goes on moving while the item is found.
                        Rectangle moved = DragAndDrop.DropPlace(Cursor.Position, new Point(onName.X - place.Left, onName.Y - place.Top), place.Size, area);
                        if (moved.Location != place.Location) _windows.ShowParkedForDrop(_source, moved.Location, place.Size, hold);
                        press = onName + new Size(moved.Left - place.Left, moved.Top - place.Top);
                    }
                }
            }
            itemFound = press is not null;
            if (press is null)
            {
                // Not found (scrolled away at the smaller size, or no name), or a page's link: at its own size with the
                // pressed spot under the pointer.
                // A window as big as the screen can't be moved to put it there: the press goes where the spot really
                // is, the pointer going there and back below, or the press misses the link.
                Rectangle at = DragAndDrop.DropPlace(Cursor.Position, spot, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), area);
                _windows.ShowParkedForDrop(_source, at.Location, holdReveal: hold);
                press = User32.GetWindowRect(_source, out Rect shownAt) ? new Point(shownAt.Left + spot.X, shownAt.Top + spot.Y) : pointer;
            }
        }
        catch (WindowOperationException failure)
        {
            Diagnostics.Log.Swallowed(failure);
            _pickingUp = false;
            StepAside(false);
            return;
        }
        if (IsDisposed || !_windows.IsParked(_source))
        {
            // Nothing to press into any more: no input goes out for a view that has gone.
            _pickingUp = false;
            Diagnostics.Log.Event("view.pickupAbandoned");
            return;
        }
        LastPickup = item.Length == 0 ? "at its own size" : $"{(itemFound ? "item found" : "item not found")} after {Environment.TickCount64 - pickupStarted} ms";
        LastPickupPress = press.Value;
        // The pressed item is under the press now: the real window gets it, and the drag is its own from here. The press
        // is made at the item and the pointer goes straight back to the hand, in one batch of input: set apart, the
        // press could land where the hand had moved to by then (a quick hand once picked up a folder beside the file).
        DragAndDrop.PressThenBack(press.Value, Cursor.Position);
        _pickingUp = false;
        _dropSince = Environment.TickCount64;
        _dropReleasedAt = 0;
        _dropOutsideSince = 0;
        _dropTookFocus = false;
        _dropFromView = true;
        _foregroundBeforeDrop = before;
        StepAside(true);
        CheckState();
        CheckRequest(modesBefore, ViewState.Request.DropStarts);
        DragsPickedUp++;
        Diagnostics.Log.Event("view.dragPickedUp", ("program", Diagnostics.LogValue.ProgramOf(_source)));
    }

    private void StartGrowing()
    {
        StopGrowing();
        // It would grow a turned picture into an upright window.
        if (!_appliedTurn.IsUpright) return;
        // Only a softening: whatever goes wrong taking the picture, the window comes up as before, at once.
        try { _growing = GrowingPicture.Start(RectangleToScreen(PictureArea), ShownForDropBounds, RevealForDrop); }
        catch (Exception failure) when (failure is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Diagnostics.Log.Swallowed(failure);
            _growing = null;
        }
        if (_growing is not null) GrowsStarted++;
    }

    private void StopGrowing()
    {
        _growing?.Dispose();
        _growing = null;
    }

    // Asked on the picture's thread.
    private Rectangle? ShownForDropBounds() =>
        _windows.IsShownForDrop(_source) && User32.GetWindowRect(_source, out Rect r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;

    private void RevealForDrop()
    {
        try { _windows.RevealShownForDrop(_source); }
        catch (WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
    }

    private GrowingPicture? _growing;

    internal int GrowsStarted { get; private set; }

    private const long PickupFindFor = 600;

    internal string LastPickup { get; private set; } = "";

    internal Point LastPickupPress { get; private set; }

    internal int DragsPickedUp { get; private set; }

    private bool _pickingUp;

    internal Point SourceSpotAt(Point screen) => new PipMapping(PictureArea, _lastShown, _appliedTurn).ToSource(PointToClient(screen));

    // The drag is over once both buttons are up. A drop that opened something of its own (a "Replace or skip?" dialog,
    // a menu) keeps it out until the focus goes elsewhere; the original itself taking the focus doesn't (a browser
    // comes forward for any drop).
    // A drag that only passes over, leaving the original with a button still down, hides it at once, so it isn't in the
    // way of where the drag is going.
    private void FollowDrop()
    {
        if (_dropSince == 0) return;
        if (!_origin.IsAvailable || !_windows.IsShownForDrop(_source)) { EndDrop(); return; }
        long now = Environment.TickCount64;
        if (DragButtonsDown())
        {
            // Taken from the view: once Windows carries the drag, the original goes back out of sight at once, so it
            // never covers where the drag is going. (So it can't be dropped back into the same window; the view, too,
            // stays out of the way until the drag ends.)
            if (_dropFromView && DragAndDrop.Running(User32.GetWindowThreadProcessId(_source, out _)))
            {
                EndDrop(keepFocus: true);
                return;
            }
            Point pointer = Cursor.Position;
            bool inside = User32.GetWindowRect(_source, out Rect r) &&
                Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(pointer);
            // Its own drag, carried over another view: the original, at its own size, may cover that view, which is
            // where the drag is going (its view hands its own original over).
            if (_dropFromView && inside && Application.OpenForms.OfType<PipWindow>().Any(other =>
                    other != this && other.Visible && other.Bounds.Contains(pointer)))
            {
                EndDrop(keepFocus: true);
                return;
            }
            if (inside) _dropOutsideSince = 0;
            else if (_dropOutsideSince == 0) _dropOutsideSince = now;
            // Out of the way of where the drag is going. Its own drag goes on without it in sight (Windows carries it),
            // and the focus stays where it is: moving it can cancel a drag.
            else if (now - _dropOutsideSince >= DropPassedBy) EndDrop(keepFocus: _dropFromView);
            return;
        }
        if (_dropReleasedAt == 0) _dropReleasedAt = now;
        nint foreground = User32.GetForegroundWindow();
        User32.GetWindowThreadProcessId(_source, out uint process);
        if (foreground != 0 && foreground != _source && SourcePopups.IsPopupOf(foreground, _source, process))
        {
            _dropTookFocus = true;
            _dropReleasedAt = now;
            return;
        }
        if (now - _dropReleasedAt < (_dropTookFocus ? UseOriginalGrace : DropSettles)) return;
        EndDrop();
    }

    // Safe to call at any time. Focus the original had goes back to the window before the drag (or the view): keys
    // mustn't go to a window that can't be seen.
    private void EndDrop(bool keepFocus = false)
    {
        if (_dropSince == 0) return;
        ViewState.Modes before = CurrentModes();
        _dropSince = 0;
        StopGrowing();
        try { _windows.ReturnToParking(_source); }
        catch (WindowOperationException failure) { Diagnostics.Log.Swallowed(failure); }
        if (keepFocus) _focusBackAfterDrag = true;
        if (!_usingOriginal)
        {
            // Still dragging (the original went back as the drag went on elsewhere): the view stays out of the way
            // until the drag is dropped or given up.
            if (DragButtonsDown()) _backAfterRelease = true;
            else StepAside(false);
            if (!keepFocus && User32.GetForegroundWindow() == _source)
            {
                nint back = _foregroundBeforeDrop != 0 && _foregroundBeforeDrop != _source && User32.IsWindow(_foregroundBeforeDrop) &&
                            !_windows.IsHidden(_foregroundBeforeDrop) ? _foregroundBeforeDrop : Handle;
                User32.SetForegroundWindow(back);
            }
        }
        CheckRequest(before, ViewState.Request.DropEnds);
        Diagnostics.Log.Event("view.dropEnded", ("tookFocus", _dropTookFocus));
    }

    private static bool DragButtonsDown() =>
        (User32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x02) & 0x8000) != 0;

    private long _dropSince, _dropReleasedAt, _dropOutsideSince;
    private bool _dropTookFocus, _dropFromView, _focusBackAfterDrag, _backAfterRelease;

    private void FollowReleaseAfterDrag()
    {
        if (!_backAfterRelease || DragButtonsDown() || _dropSince != 0) return;
        _backAfterRelease = false;
        if (!_usingOriginal && !_suspended) StepAside(false);
    }

    // Keys mustn't stay with a window that can't be seen once the drag is let go.
    private void FollowFocusAfterDrag()
    {
        if (!_focusBackAfterDrag || DragButtonsDown()) return;
        _focusBackAfterDrag = false;
        if (_dropSince != 0 || User32.GetForegroundWindow() != _source || !_windows.IsHidden(_source)) return;
        nint back = _foregroundBeforeDrop != 0 && _foregroundBeforeDrop != _source && User32.IsWindow(_foregroundBeforeDrop) &&
                    !_windows.IsHidden(_foregroundBeforeDrop) ? _foregroundBeforeDrop : Handle;
        User32.SetForegroundWindow(back);
    }
    private nint _foregroundBeforeDrop;
    private const long DropSettles = 500, DropPassedBy = 300;

    internal bool HandingOverDrop => _dropSince != 0;

    private nint[] _steppedAside = [];

    private void StepAside(bool aside)
    {
        if (aside)
        {
            if (_steppedAside.Length > 0 || !IsHandleCreated) return;
            _steppedAside = [.. OwnedForms.Where(part => part.IsHandleCreated && User32.IsWindowVisible(part.Handle)).Select(part => part.Handle), Handle];
            foreach (nint window in _steppedAside) User32.ShowWindow(window, User32.SW_HIDE);
            return;
        }
        if (_steppedAside.Length == 0) return;
        nint[] back = _steppedAside;
        _steppedAside = [];
        foreach (nint window in back.Reverse())
            if (User32.IsWindow(window)) User32.ShowWindow(window, User32.SW_SHOWNOACTIVATE);
        StayOnTop();
    }

    internal bool SteppedAside => _steppedAside.Length > 0;
}
