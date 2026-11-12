using static WinScreen.Localization.UiText;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// Puts windows where the person asks. These are the person's own choices, so unlike
/// <see cref="WindowController"/>'s effects they aren't journaled and Restore all leaves them alone. A
/// short memory, gone at exit, backs Restore prior geometry.
/// </summary>
public sealed class WindowPlacer(WindowController windows)
{
    private const int HistoryDepth = 5;
    private const int SettleMilliseconds = 250;
    private const int SettleStep = 15;
    private const int Tolerance = 1;
    private const uint Flags = User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;

    private readonly Dictionary<nint, (WindowIdentity Identity, List<WindowPlacement> Earlier)> _history = [];

    public void Align(nint window, WindowAnchor anchor)
    {
        Check(window);
        Remember(window);
        Screen screen = Screen.FromHandle(window);
        Rectangle frame = FrameOf(window, screen);
        Place(window, new Rectangle(PlacementGeometry.Align(frame.Size, screen.WorkingArea, anchor), frame.Size), screen, unmaximize: true);
    }

    /// <summary>Sets the visible frame exactly, in screen pixels. A maximized window comes back to its usual size for it.</summary>
    public void SetFrame(nint window, Rectangle frame)
    {
        Check(window);
        Remember(window);
        Place(window, frame, Screen.FromRectangle(frame), unmaximize: true);
    }

    /// <exception cref="WindowOperationException">There's only one display, or the window refused.</exception>
    public void MoveToNextDisplay(nint window)
    {
        Check(window);
        Screen[] screens = Screen.AllScreens;
        Screen from = Screen.FromHandle(window);
        int next = PlacementGeometry.NextDisplay(Array.FindIndex(screens, screen => screen.DeviceName == from.DeviceName), screens.Length);
        if (next < 0)
            throw new WindowOperationException(T("errors.placement.singleDisplay"));

        MoveToDisplay(window, screens[next].DeviceName);
    }

    public void MoveToDisplay(nint window, string device)
    {
        Check(window);
        WindowIdentity identity = WindowIdentity.Capture(window);
        Screen from = Screen.FromHandle(window);
        Screen to = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == device)
            ?? throw new WindowOperationException(T("errors.pip.displayUnavailable"));
        Rectangle original = FrameOf(window, from);
        Rectangle target = PlacementGeometry.FrameOnDisplay(original, from.WorkingArea, to.WorkingArea, original.Size);
        Remember(window);
        identity.Require();
        if (User32.IsIconic(window))
        {
            PlaceHidden(window, target, to);
            return;
        }
        if (User32.IsZoomed(window))
        {
            // A maximized window stays where it is when given a new place to come back to. It comes back to its usual
            // size on the display chosen, then is maximized there again. The place is given again for that: some
            // apps, Windows Forms ones among them, put a window they restore back where their own code last put it.
            PlaceHidden(window, target, to, User32.SW_SHOWNOACTIVATE);
            Settle(window);
            identity.Require();
            PlaceHidden(window, target, to, User32.SW_MAXIMIZE);
            Settle(window);
            if (!User32.IsZoomed(window) || Screen.FromHandle(window).DeviceName != device)
                throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
            return;
        }
        Move(window, target, User32.SWP_NOSIZE);
        Settle(window);
        identity.Require();
        Screen current = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == device)
            ?? throw new WindowOperationException(T("errors.pip.displayUnavailable"));
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle moved, out _))
            throw new WindowOperationException(T("errors.window.targetUnavailable"));
        Place(window, PlacementGeometry.FrameOnDisplay(original, from.WorkingArea, current.WorkingArea, moved.Size), current);
    }

    /// <summary>Puts a window back where it was before the last placement. False when there's nothing to go back to.</summary>
    public bool RestorePrior(nint window)
    {
        Check(window);
        if (!_history.TryGetValue(window, out var history) || !history.Identity.IsSameWindow() || history.Earlier.Count == 0)
            return false;
        WindowPlacement placement = history.Earlier[^1];
        placement.Length = Marshal.SizeOf<WindowPlacement>();
        if (placement.ShowCommand == 1)
            placement.ShowCommand = User32.SW_SHOWNOACTIVATE;
        if (!User32.SetWindowPlacement(window, ref placement))
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
        Settle(window);
        var actual = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!User32.GetWindowPlacement(window, ref actual) ||
            Math.Abs(actual.NormalPosition.Left - placement.NormalPosition.Left) > Tolerance ||
            Math.Abs(actual.NormalPosition.Top - placement.NormalPosition.Top) > Tolerance ||
            Math.Abs(actual.NormalPosition.Right - placement.NormalPosition.Right) > Tolerance ||
            Math.Abs(actual.NormalPosition.Bottom - placement.NormalPosition.Bottom) > Tolerance)
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
        history.Earlier.RemoveAt(history.Earlier.Count - 1);
        return true;
    }

    /// <summary>Notes where a window is, so the move about to happen can be undone.</summary>
    internal void Remember(nint window)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!User32.GetWindowPlacement(window, ref placement))
            return;
        lock (_history)
        {
            foreach (nint gone in _history.Where(pair => !pair.Value.Identity.IsSameWindow()).Select(pair => pair.Key).ToArray())
                _history.Remove(gone);
            if (!_history.TryGetValue(window, out var history))
                _history[window] = history = (WindowIdentity.Observe(window), []);
            history.Earlier.Add(placement);
            if (history.Earlier.Count > HistoryDepth)
                history.Earlier.RemoveAt(0);
        }
    }

    private void Check(nint window)
    {
        if (windows.Refusal(window) is { } refusal)
            throw new WindowOperationException(refusal);
        if (windows.IsHidden(window) || windows.IsPeeking(window))
            throw new WindowOperationException(T("errors.window.showFirst"));
    }

    private static bool IsPlainlyOnScreen(nint window) => !User32.IsIconic(window) && !User32.IsZoomed(window);

    /// <summary>The visible frame, or for a minimized or maximized window where it would go back to.</summary>
    private static Rectangle FrameOf(nint window, Screen screen)
    {
        if (IsPlainlyOnScreen(window))
        {
            if (WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _))
                return frame;
            throw new WindowOperationException(string.Format(T("errors.window.operationFailed"), T("actions.readPosition"), WindowInfo.Title(window), ""));
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        User32.GetWindowPlacement(window, ref placement);
        Rect normal = placement.NormalPosition;
        Rectangle area = screen.WorkingArea, all = screen.Bounds;
        return Rectangle.FromLTRB(normal.Left + area.Left - all.Left, normal.Top + area.Top - all.Top,
            normal.Right + area.Left - all.Left, normal.Bottom + area.Top - all.Top);
    }

    /// <summary>
    /// Puts the visible frame at <paramref name="target"/>. A minimized window stays minimized and comes back there.
    /// A maximized one, which fills its display whatever its place, is made its usual size first with
    /// <paramref name="unmaximize"/>, so the place chosen can be seen; otherwise it too comes back there.
    /// </summary>
    private void Place(nint window, Rectangle target, Screen screen, bool unmaximize = false)
    {
        if (unmaximize && User32.IsZoomed(window) && !User32.IsIconic(window))
        {
            PlaceHidden(window, target, screen, User32.SW_SHOWNOACTIVATE);
            Settle(window);
        }
        if (!IsPlainlyOnScreen(window))
        {
            PlaceHidden(window, target, screen);
            return;
        }

        Move(window, target, 0);
        Settle(window);
        if (WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _) && frame.Size != target.Size)
            Move(window, target, 0); // Scaling changed on the way, so the invisible margins did too.
        if (!WindowInfo.TryGetFrameBounds(window, out frame, out _) ||
            Math.Abs(frame.Left - target.Left) > Tolerance || Math.Abs(frame.Top - target.Top) > Tolerance ||
            Math.Abs(frame.Width - target.Width) > Tolerance || Math.Abs(frame.Height - target.Height) > Tolerance)
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
    }

    private static void Move(nint window, Rectangle target, uint extra)
    {
        if (!WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out Rectangle outer))
            return;
        Rectangle wanted = PlacementGeometry.OuterFor(target, frame, outer);
        if (!User32.SetWindowPos(window, 0, wanted.X, wanted.Y, wanted.Width, wanted.Height, Flags | extra))
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
    }

    /// <summary>A minimized or maximized window stays that way, and is given a new place to come back to.</summary>
    private static void PlaceHidden(nint window, Rectangle target, Screen screen, int? show = null)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!User32.GetWindowPlacement(window, ref placement))
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
        if (show is { } command) placement.ShowCommand = command;
        Rectangle area = screen.WorkingArea, all = screen.Bounds;
        int x = area.Left - all.Left, y = area.Top - all.Top;
        placement.NormalPosition = new Rect(target.Left - x, target.Top - y, target.Right - x, target.Bottom - y);
        if (!User32.SetWindowPlacement(window, ref placement))
            throw new WindowOperationException(string.Format(T("errors.placement.refused"), WindowInfo.Title(window)));
    }

    /// <summary>Waits briefly for an app to finish reacting to a move, in case it resizes itself.</summary>
    private static void Settle(nint window)
    {
        Rectangle last = default;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < SettleMilliseconds)
        {
            WindowInfo.TryGetFrameBounds(window, out Rectangle frame, out _);
            if (frame == last)
                return;
            last = frame;
            Thread.Sleep(SettleStep);
        }
    }
}
