using WinScreen.Native;
using static WinScreen.Localization.UiText;

namespace WinScreen.Windows;

// Browsers and Electron apps stop drawing once no part of their window shows. A wholly covered window that a view
// shows goes see-through and click-through above the rest instead, where it can't be seen or reached either, and goes
// back under the windows over it once a part of it would show. Unlike hiding, it stays in its place, on the taskbar
// and in Alt+Tab.
public sealed partial class WindowController
{
    private const uint StackFlags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;

    internal bool IsLiftedWhileCovered(nint window)
    {
        lock (_gate)
            return _changed.TryGetValue(window, out ChangedWindow? state) && state.Identity.Matches() && state.LiftedWhileCovered;
    }

    internal List<nint> LiftedWhileCovered()
    {
        lock (_gate)
            return [.. _changed.Where(pair => pair.Value.LiftedWhileCovered && pair.Value.Identity.Matches()).Select(pair => pair.Key)];
    }

    // False when something else of WinScreen has changed it in a way the lift would undo or fight.
    internal bool LiftWhileCovered(nint window)
    {
        lock (_gate)
        {
            ChangedWindow state = Track(window);
            if (state.Hidden || state.LiftedWhileCovered || state.Pinned || state.GlassColor is not null || state.Cropped ||
                state.PlacementBeforePeek is not null || state.PlacementBeforeBorderless is not null || state.PlacementBeforeRollUp is not null)
                return false;
            EnsureLayeringSafe(state);
            state.AlphaBeforeHiding = state.Layered ? state.Alpha : (byte)255;
            state.LiftedWhileCovered = true;
            Prepare(window, state, User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOPMOST);
            try
            {
                // See-through first, while still covered, so it is never seen on top.
                ApplyAlpha(window, state, HiddenOpacity);
                WriteExStyle(window, ReadExStyle(window) | User32.WS_EX_TRANSPARENT);
                HideBorder(window, state);
                state.Identity.Require();
                // Not through SetTopmost: this isn't a pin, and TopLayer leaves see-through windows alone.
                if (!User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, StackFlags) || !IsTopmost(window))
                    throw new WindowOperationException(string.Format(T("errors.window.changeIgnored"), T("actions.pin"), WindowInfo.Title(window)));
                NudgeInPlace(window);
            }
            catch (WindowOperationException)
            {
                // Still covered: under everything changes nothing that shows, where the top of the stack would.
                try { SettleAfterCover(window, state, IsTopmost(window) ? User32.HWND_BOTTOM : 0); }
                catch (WindowOperationException caught) { Diagnostics.Log.Swallowed(caught); }
                state.LiftedWhileCovered = false;
                TrySaveJournal();
                throw;
            }
            SaveJournal();
            return true;
        }
    }

    // under: the lowest window that still goes over it; HWND_NOTOPMOST when none does; 0 leaves the stack alone.
    // A window that doesn't answer stays lifted, and recorded, to be tried again.
    internal void SettleAfterCover(nint window, nint under)
    {
        lock (_gate)
        {
            if (!_changed.TryGetValue(window, out ChangedWindow? state) || !state.LiftedWhileCovered) return;
            SettleAfterCover(window, state, under);
            state.LiftedWhileCovered = false;
            TrySaveJournal();
        }
    }

    // Back in the stack first, while it still can't be seen, then solid again.
    private void SettleAfterCover(nint window, ChangedWindow state, nint under)
    {
        state.Identity.Require();
        EnsureResponsive(window);
        if (under != 0)
        {
            if (under != User32.HWND_NOTOPMOST && under != User32.HWND_BOTTOM && (!User32.IsWindow(under) || IsTopmost(under)))
                under = User32.HWND_NOTOPMOST;
            User32.SetWindowPos(window, User32.HWND_NOTOPMOST, 0, 0, 0, 0, StackFlags);
            if (under != User32.HWND_NOTOPMOST) User32.SetWindowPos(window, under, 0, 0, 0, 0, StackFlags);
        }
        RestoreBorder(window, state);
        if (state.Layered || state.AlphaBeforeHiding != 255)
            ApplyAlpha(window, state, state.AlphaBeforeHiding);
        if (!state.ClickThrough)
            WriteExStyle(window, ReadExStyle(window) & ~User32.WS_EX_TRANSPARENT);
        if (IsTopmost(window))
            throw new WindowOperationException(string.Format(T("errors.window.changeIgnored"), T("actions.unpin"), WindowInfo.Title(window)));
    }

    // A browser asks again whether it is covered when a window moves, not when the stack changes.
    private static void NudgeInPlace(nint window)
    {
        if (!User32.GetWindowRect(window, out Rect r)) return;
        const uint flags = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE;
        User32.SetWindowPos(window, 0, r.Left + 1, r.Top, 0, 0, flags);
        User32.SetWindowPos(window, 0, r.Left, r.Top, 0, 0, flags);
    }

    // Hiding a lifted window keeps it as it is: see-through and on top.
    private static bool TakeOverLift(ChangedWindow state)
    {
        if (!state.LiftedWhileCovered) return false;
        state.LiftedWhileCovered = false;
        return true;
    }
}
