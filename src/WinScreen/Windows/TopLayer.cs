using WinScreen.Native;

namespace WinScreen.Windows;

/// <summary>
/// WinScreen's own menus and Settings, while open, stay above everything else WinScreen keeps on top
/// (views, their title bars, chips, toolbars, pin frames). Anything that lifts itself to the top goes
/// through here, so a view can never cover the place someone went to change it.
/// </summary>
internal static class TopLayer
{
    private const uint Flags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;

    /// <summary>The menus and windows that belong above, top-level menu first; empty when none is open.</summary>
    internal static Func<IEnumerable<nint>>? Above { get; set; }

    /// <summary>Puts a window at the top of the always-on-top windows, still under WinScreen's open menus.</summary>
    internal static void Raise(nint window)
    {
        User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        KeepAbove();
    }

    /// <summary>Lifts the open menus and Settings back over whatever just went to the top.</summary>
    internal static void KeepAbove()
    {
        foreach (nint window in Above?.Invoke() ?? [])
            User32.SetWindowPos(window, User32.HWND_TOPMOST, 0, 0, 0, 0, Flags);
    }

    /// <summary>A menu's own window and every submenu open under it, outermost first.</summary>
    internal static IEnumerable<nint> OpenDropDowns(ToolStripDropDown? menu)
    {
        if (menu is not { Visible: true, IsHandleCreated: true, IsDisposed: false })
            yield break;
        yield return menu.Handle;
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripDropDownItem { HasDropDownItems: true, DropDown: var child })
                foreach (nint window in OpenDropDowns(child))
                    yield return window;
    }
}
