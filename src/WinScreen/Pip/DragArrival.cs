using WinScreen.Native;

namespace WinScreen.Pip;

// Seen by the pointer rather than by drag and drop: Windows doesn't let a drag from a normal window reach an elevated
// one, so an elevated WinScreen is never asked.
// While Windows' drag and drop runs, the thread it started on holds the mouse with the drop's own window
// (CLIPBRDWNDCLASS); a held button or a selection box holds it with the app's own window. That thread is the pressed
// window's, which needn't be the one in front (File Explorer doesn't come forward for a press on a file).
// A left button pressed elsewhere, with that drop window holding the mouse, while the pointer rests on the picture, is
// a drag arriving; a button merely held over the view is not. A drag the view is asked about goes first.
internal sealed class DragArrival(Func<uint, bool> runningOnThread, Func<uint, bool> runningInProcess)
{
    internal const long Rests = 150, NotSeenAfter = 600, ProcessCheckEvery = 250;

    // The window the button went down on: FromElsewhere when it isn't one of WinScreen's own.
    internal readonly record struct Press(bool FromElsewhere, uint Thread, uint Process, string Class);

    private long _heldSince, _dragInSince, _overPictureSince, _processCheckedAt = -100_000;
    private bool _heldFromElsewhere, _notSeenLogged, _processDragRunning;
    private Press _press;

    // Called on each tick; true once a drag from elsewhere has rested on the picture, at most once a press.
    internal bool Step(long now, bool leftDown, Func<Press> pressed, Func<bool> overPicture)
    {
        if (!leftDown) { _heldSince = 0; _dragInSince = 0; return false; }
        if (_heldSince == 0)
        {
            _heldSince = now;
            _press = pressed();
            _heldFromElsewhere = _press.FromElsewhere;
            _overPictureSince = 0;
            _notSeenLogged = false;
            return false;
        }
        bool over = _heldFromElsewhere && overPicture();
        if (!over || !Running(now))
        {
            _dragInSince = 0;
            if (!over) { _overPictureSince = 0; return false; }
            // Held from elsewhere and resting on the picture, but no drag and drop seen: logged once per press, so a
            // report of "nothing came up" shows where it stopped.
            if (_overPictureSince == 0) _overPictureSince = now;
            else if (!_notSeenLogged && now - _overPictureSince >= NotSeenAfter)
            {
                _notSeenLogged = true;
                Diagnostics.Log.Event("view.dragNotSeen", ("program", Diagnostics.LogValue.Program(ProcessInfo.PathOf(_press.Process))),
                    ("class", Diagnostics.LogValue.WindowClass(_press.Class)));
            }
            return false;
        }
        if (_dragInSince == 0) { _dragInSince = now; return false; }
        if (now - _dragInSince < Rests) return false;
        _dragInSince = 0;
        _heldFromElsewhere = false; // Once per press.
        return true;
    }

    // On the pressed window's thread, or, every quarter second, on any thread of its program, in case it runs its drag
    // elsewhere.
    private bool Running(long now)
    {
        if (runningOnThread(_press.Thread)) return true;
        if (now - _processCheckedAt >= ProcessCheckEvery)
        {
            _processCheckedAt = now;
            _processDragRunning = runningInProcess(_press.Process);
        }
        return _processDragRunning;
    }
}
