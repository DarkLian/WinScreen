using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>
/// Where another program's text caret is, as accessibility reports it: Windows' own caret for classic text boxes,
/// and the one browsers and Electron apps report for screen magnifiers. Each call can wait on that program, so it
/// is only ever made from a thread of WinScreen's own that nothing else waits on, never from a hook.
/// </summary>
internal static partial class TextCaret
{
    private const int ObjectCaret = -8;
    private static readonly Guid AccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromEvent(nint window, int item, int child,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible, out object? childId);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint window, int item, in Guid id,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);

    /// <summary>The window with the keyboard focus in a thread, or 0.</summary>
    internal static nint FocusOf(uint thread)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    internal static bool IsHung(nint window) => IsHungAppWindow(window);

    /// <summary>The caret a caret event is about, on the screen; null when it can't be told or is empty.</summary>
    internal static Rectangle? FromEvent(nint window, int item, int child)
    {
        if (item != ObjectCaret || window == 0 || IsHungAppWindow(window)) return null;
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

    /// <summary>The caret in <paramref name="window"/> (the focused window of the program), on the screen, or null.</summary>
    internal static Rectangle? In(nint window)
    {
        if (window == 0 || IsHungAppWindow(window)) return null;
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

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg message, nint window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, int message, nint w, nint l);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, nint handles, bool all, uint milliseconds, uint wake);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Window; public uint Message; public nint W, L; public uint Time; public int X, Y; }

    /// <summary>The calling thread, with a message queue made for it, so a later quit can't be lost.</summary>
    internal static uint PrepareThread()
    {
        PeekMessageW(out _, 0, 0, 0, 0);
        return GetCurrentThreadId();
    }

    /// <summary>
    /// Pumps this thread's messages (WinEvent callbacks arrive through them) until <see cref="Quit"/>, calling
    /// <paramref name="tick"/> at least every <paramref name="milliseconds"/>.
    /// </summary>
    internal static void Pump(int milliseconds, Action tick)
    {
        while (true)
        {
            if (MsgWaitForMultipleObjects(0, 0, false, (uint)milliseconds, 0x04FF) != 0x102)
                while (PeekMessageW(out Msg message, 0, 0, 0, 1))
                {
                    if (message.Message == 0x0012) return;
                    TranslateMessage(message);
                    DispatchMessageW(message);
                }
            tick();
        }
    }

    internal static void Quit(uint thread) => PostThreadMessageW(thread, 0x0012, 0, 0);
}
