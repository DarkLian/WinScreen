using System.Runtime.InteropServices;

namespace WinScreen.Native;

// Each call can wait on the other program: only from a thread of WinScreen's own that nothing waits on, never from a
// hook.
internal static partial class TextCaret
{
    private const int ObjectCaret = -8;
    private static readonly Guid AccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromEvent(nint window, int item, int child,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible, out object? childId);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint window, int item, in Guid id,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);

    internal static nint FocusOf(uint thread)
    {
        var info = GuiThreadInfo.Empty;
        return thread != 0 && User32.GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    internal static bool IsHung(nint window) => User32.IsHungAppWindow(window);

    internal static Rectangle? FromEvent(nint window, int item, int child)
    {
        if (item != ObjectCaret || window == 0 || User32.IsHungAppWindow(window)) return null;
        try
        {
            if (AccessibleObjectFromEvent(window, item, child, out var accessible, out object? childId) != 0 || accessible is null) return null;
            return Bounds(accessible, childId ?? 0);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            Diagnostics.Log.Swallowed(e);
            return null;
        }
    }

    internal static Rectangle? In(nint window)
    {
        if (window == 0 || User32.IsHungAppWindow(window)) return null;
        try
        {
            if (AccessibleObjectFromWindow(window, ObjectCaret, AccessibleId, out var accessible) != 0 || accessible is null) return null;
            return Bounds(accessible, 0);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            Diagnostics.Log.Swallowed(e);
            return null;
        }
    }

    private static Rectangle? Bounds(Accessibility.IAccessible accessible, object childId)
    {
        accessible.accLocation(out int left, out int top, out int width, out int height, childId);
        // A caret that isn't showing is reported as an empty rectangle at 0,0.
        return width <= 0 && height <= 0 && left == 0 && top == 0 ? null : new Rectangle(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    // Makes the thread's message queue now, so a later quit can't be lost.
    internal static uint PrepareThread()
    {
        User32.PeekMessage(out _, 0, 0, 0, 0);
        return Kernel32.GetCurrentThreadId();
    }

    // WinEvent callbacks arrive through this thread's messages.
    internal static void Pump(int milliseconds, Action tick)
    {
        while (true)
        {
            if (User32.MsgWaitForMultipleObjects(0, 0, false, (uint)milliseconds, 0x04FF) != 0x102)
                while (User32.PeekMessage(out Msg message, 0, 0, 0, 1))
                {
                    if (message.Message == 0x0012) return;
                    User32.TranslateMessage(message);
                    User32.DispatchMessage(message);
                }
            tick();
        }
    }

    internal static void Quit(uint thread) => User32.PostThreadMessage(thread, 0x0012, 0, 0);
}
