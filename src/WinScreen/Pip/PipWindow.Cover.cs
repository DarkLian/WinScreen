using WinScreen.Native;
using WinScreen.Windows;

namespace WinScreen.Pip;

// With Keep hidden off, a view froze once another window covered its original, as browsers stop drawing then.
// While no part of it shows, the original goes see-through above the rest (WindowController.LiftWhileCovered),
// and goes back under whatever is still over it once a part would show again.
public sealed partial class PipWindow
{
    // Over it when lifted, and every window brought to the front since; null while it isn't lifted.
    private HashSet<nint>? _over;
    private HashSet<nint> _popupsAtLift = [];
    private CoverWatch? _coverWatch;
    private bool _coverCheckQueued;
    private long _liftRefusedUntil, _settleRetryAt;
    private int _coverTick, _liftFailures;
    private nint _liftFront; // The last window in front while lifted, not counting the taskbar or switcher.
    private const long LiftRetryMilliseconds = 5000, SettleRetryMilliseconds = 1000;
    private const int MostLiftFailures = 3;

    internal bool LiftedWhileCovered => _over is not null;
    internal int CoverLifts { get; private set; }
    internal CoverEnd LastCoverEnd { get; private set; }

    // For tests that need a covered original to freeze as it would without this.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool LiftsWhenCovered { get; set; } = true;

    private bool MayLift => LiftsWhenCovered && !_cleaned && !_suspended && _thumbnail is not null && !_frozen &&
        _origin.IsWindow && _origin.IsAvailable && !_hideItem.Checked && _awayFrom == Guid.Empty && !_usingOriginal &&
        _live is null && _dropSince == 0 && _goingToUntil == 0 && !_pickingUp && !SteppedAside;

    private void FollowCover()
    {
        if (_over is not null)
        {
            FollowLifted();
            return;
        }
        if (User32.GetForegroundWindow() == _source) _liftFailures = 0; // Used again: Windows lets it go on top now.
        if (++_coverTick % 2 != 0 || !MayLift || Environment.TickCount64 < _liftRefusedUntil || _liftFailures >= MostLiftFailures ||
            User32.GetForegroundWindow() == _source || User32.IsIconic(_source) || _windows.IsHidden(_source) ||
            _windows.IsLiftedWhileCovered(_source) || WindowInfo.IsConcealed(_source) ||
            (User32.GetWindowLongPtr(_source, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0)
            return;
        // A borderless game in front keeps the screen to itself only with nothing over it, a see-through window included.
        if (FullScreen.IsActive() || !WindowInfo.TryGetFrameBounds(_source, out Rectangle bounds, out _)) return;
        List<nint> over = WindowStack.Over(_source);
        if (!CoverTracker.IsCovered(bounds, CoverRects(over), Screen.AllScreens.Select(screen => screen.Bounds)) ||
            !VirtualDesktops.OnCurrentDesktop(_source))
            return;
        // Its own popups would go up with it, over the windows covering it.
        HashSet<nint> popups = VisiblePopups();
        if (popups.Count > 0) return;
        try
        {
            if (!_windows.LiftWhileCovered(_source)) return;
        }
        catch (WindowOperationException failure)
        {
            // It won't be see-through, isn't WinScreen's to change, doesn't answer, or Windows won't put it on top (a
            // window never used): it freezes as before, unsaid, and is tried again a few times.
            _liftRefusedUntil = Environment.TickCount64 + LiftRetryMilliseconds;
            _liftFailures++;
            Diagnostics.Log.Swallowed(failure);
            return;
        }
        _liftFailures = 0;
        _liftFront = User32.GetForegroundWindow();
        _over = [.. over];
        _popupsAtLift = popups;
        _coverWatch = new CoverWatch(window => window == _source || _over?.Contains(window) == true, QueueCoverCheck);
        CoverLifts++;
        Diagnostics.Log.Event("view.coverLifted", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("over", over.Count));
    }

    private void QueueCoverCheck()
    {
        if (_coverCheckQueued) return;
        _coverCheckQueued = true;
        BeginInvokeIfAlive(() =>
        {
            _coverCheckQueued = false;
            if (_over is not null) FollowLifted();
        });
    }

    private void FollowLifted()
    {
        if (_over is null) return;
        if (!_origin.IsWindow || !User32.IsWindow(_source) || !_windows.IsLiftedWhileCovered(_source))
        {
            // Gone, or hidden or put back by something else: nothing of the lift is left to undo.
            ForgetLift(CoverEnd.TakenOver);
            return;
        }
        if (!MayLift) { EndLift(CoverEnd.View); return; }
        if (FullScreen.IsActive()) { EndLift(CoverEnd.FullScreen); return; }
        if (User32.IsIconic(_source)) { EndLift(CoverEnd.Minimized); return; }
        nint foreground = User32.GetForegroundWindow();
        if (foreground == _source)
        {
            LiftedArrived(_foregroundBefore);
            return;
        }
        if (foreground != 0) _foregroundBefore = foreground;
        // A taskbar click puts the taskbar in front first; the window that was in use is the one that counts.
        if (foreground != 0 && foreground != Handle && !IsPassing(foreground))
        {
            nint before = _liftFront;
            _liftFront = foreground;
            if (foreground != before && !_over.Contains(foreground) && before != 0 && User32.IsWindow(before) && User32.IsIconic(before))
            {
                // The window in front was minimized and Windows passed the see-through original over, for the next
                // window down, one that was under it: without the lift the original would have had the focus, so it
                // does. (A window closing hands the focus to the one used before it instead, wherever that is.)
                if (EndLift(CoverEnd.Arrived, User32.HWND_NOTOPMOST)) User32.SetForegroundWindow(_source);
                return;
            }
            if (WindowStack.CoverBounds(foreground) is not null) _over.Add(foreground);
        }
        if (VisiblePopups().Any(popup => !_popupsAtLift.Contains(popup))) { EndLift(CoverEnd.Popup); return; }
        if (!StillCovered()) EndLift(CoverEnd.Uncovered);
    }

    // The focus came to the lifted original. Windows hands it over this way when the window in front is minimized or
    // closed, being the highest; then it goes to the next window if the original is still covered. Picked by the
    // person or brought forward by its app, it comes to the front.
    private void LiftedArrived(nint before)
    {
        _sourceWasFocused = true;
        nint front = _liftFront != 0 ? _liftFront : before;
        bool handOff = front != _source && GoneFromFront(front) && !PickedFromTaskbarOrSwitcher(before);
        if (!handOff)
        {
            EndLift(CoverEnd.Arrived, User32.HWND_NOTOPMOST);
            return;
        }
        if (!StillCovered())
        {
            EndLift(CoverEnd.Uncovered);
            return;
        }
        nint next = NextWindowForFocus();
        User32.SetForegroundWindow(next != 0 ? next : User32.GetShellWindow());
        _sourceWasFocused = false;
        Diagnostics.Log.Event("view.coverHandOffKept", ("program", Diagnostics.LogValue.ProgramOf(_source)),
            ("before", Diagnostics.LogValue.ClassOf(front)));
    }

    private bool IsPassing(nint window)
    {
        string name = WindowInfo.ClassName(window);
        if (TaskbarClasses.Contains(name) || SwitcherClasses.Contains(name) || WindowController.IsShell(window)) return true;
        User32.GetWindowThreadProcessId(window, out uint process);
        return process == Environment.ProcessId;
    }

    // For tests and the log: what the lift counts as over the original.
    internal string DescribeCover() =>
        _over is null ? "not lifted" : $"foreground {WindowInfo.ClassName(User32.GetForegroundWindow())}; over: " + string.Join(", ",
            _over.Select(window => $"{WindowInfo.ClassName(window)} '{WindowInfo.Title(window)}' {WindowStack.CoverBounds(window)?.ToString() ?? "covers nothing"}"));

    private static bool GoneFromFront(nint window) =>
        window != 0 && (!User32.IsWindow(window) || User32.IsIconic(window) || !User32.IsWindowVisible(window) || WindowInfo.IsConcealed(window));

    private bool StillCovered() =>
        _over is not null && WindowInfo.TryGetFrameBounds(_source, out Rectangle bounds, out _) &&
        CoverTracker.IsCovered(bounds, CoverRects(_over), Screen.AllScreens.Select(screen => screen.Bounds));

    private static IEnumerable<Rectangle> CoverRects(IEnumerable<nint> windows)
    {
        foreach (nint window in windows)
            if (WindowStack.CoverBounds(window) is { } bounds)
                yield return bounds;
    }

    private HashSet<nint> VisiblePopups()
    {
        User32.GetWindowThreadProcessId(_source, out uint process);
        var popups = new HashSet<nint>();
        const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), GW_CHILD);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
            if (window != _source && User32.IsWindowVisible(window) && !WindowInfo.IsConcealed(window) &&
                SourcePopups.IsPopupOf(window, _source, process) && WindowInfo.TryGetFrameBounds(window, out Rectangle bounds, out _) &&
                bounds.Width > 2 && bounds.Height > 2)
                popups.Add(window);
        return popups;
    }

    // Under the lowest window still over it, or at the top of the ordinary windows when none is. An original that
    // doesn't answer stays lifted and is tried again a second later.
    private bool EndLift(CoverEnd why, nint? under = null)
    {
        if (_over is null) return true;
        if (Environment.TickCount64 < _settleRetryAt) return false;
        nint place = under ?? LowestOver();
        try { _windows.SettleAfterCover(_source, place); }
        catch (WindowOperationException failure)
        {
            if (_settleRetryAt == 0) Diagnostics.Log.Swallowed(failure);
            _settleRetryAt = Environment.TickCount64 + SettleRetryMilliseconds;
            return false;
        }
        ForgetLift(why);
        return true;
    }

    private void ForgetLift(CoverEnd why)
    {
        _over = null;
        _popupsAtLift = [];
        _liftFront = 0;
        _settleRetryAt = 0;
        _coverWatch?.Dispose();
        _coverWatch = null;
        LastCoverEnd = why;
        Diagnostics.Log.Event("view.coverLiftEnded", ("program", Diagnostics.LogValue.ProgramOf(_source)), ("why", why));
    }

    private nint LowestOver() => _over is null ? User32.HWND_NOTOPMOST : LowestOf(_over);

    // A closed view can't try again itself: an original that didn't answer is put back once it does, for a minute.
    private void SettleWhenItAnswers()
    {
        if (_over is null) return;
        HashSet<nint> over = _over;
        nint source = _source;
        WindowController windows = _windows;
        var retry = new System.Windows.Forms.Timer { Interval = (int)SettleRetryMilliseconds };
        int tries = 0;
        retry.Tick += (_, _) =>
        {
            try { windows.SettleAfterCover(source, LowestOf(over)); }
            catch (WindowOperationException) { if (++tries < 60) return; }
            retry.Dispose();
        };
        retry.Start();
        ForgetLift(CoverEnd.ViewClosed);
    }

    private static nint LowestOf(HashSet<nint> over)
    {
        nint lowest = User32.HWND_NOTOPMOST;
        const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
        nint window = User32.GetWindow(User32.GetDesktopWindow(), GW_CHILD);
        for (int seen = 0; window != 0 && seen < 4096; seen++, window = User32.GetWindow(window, GW_HWNDNEXT))
            if (over.Contains(window) && User32.IsWindowVisible(window) && !User32.IsIconic(window) &&
                (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) == 0)
                lowest = window;
        return lowest;
    }
}

internal enum CoverEnd { None, TakenOver, View, Minimized, Arrived, Popup, Uncovered, Mode, ViewMinimized, ViewClosed, FullScreen }

// Changes that can uncover a lifted original, so it goes back at once rather than at the next tick.
internal sealed class CoverWatch : IDisposable
{
    private const uint Foreground = 0x0003, MinimizeStart = 0x0016, Destroy = 0x8001, Hide = 0x8003, LocationChange = 0x800B,
        Cloaked = 0x8017, OutOfContext = 0;
    private readonly User32.WinEventProc _received;
    private readonly nint[] _hooks;
    private readonly Func<nint, bool> _watched;
    private readonly Action _changed;

    public CoverWatch(Func<nint, bool> watched, Action changed)
    {
        _watched = watched;
        _changed = changed;
        _received = OnEvent;
        nint callback = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_received);
        _hooks =
        [
            User32.SetWinEventHook(Foreground, Foreground, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(MinimizeStart, MinimizeStart, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(Destroy, Hide, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(LocationChange, LocationChange, 0, callback, 0, 0, OutOfContext),
            User32.SetWinEventHook(Cloaked, Cloaked, 0, callback, 0, 0, OutOfContext),
        ];
    }

    private void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (item != 0 || child != 0 || window == 0) return;
        if (kind == Foreground || _watched(window)) _changed();
    }

    public void Dispose()
    {
        foreach (nint hook in _hooks)
            if (hook != 0) User32.UnhookWinEvent(hook);
        GC.KeepAlive(_received);
    }
}
