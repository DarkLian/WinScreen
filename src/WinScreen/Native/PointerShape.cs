using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>The shape the pointer has on the screen now. Asks only Windows, never another program, so hooks may use it.</summary>
internal static class PointerShape
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size, Flags;
        public nint Cursor;
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint name);

    private static readonly nint Hand = LoadCursor(0, 32649); // IDC_HAND, Windows' own, shared by every program.

    /// <summary>Whether the pointer is the hand apps show over a link.</summary>
    internal static bool IsHand()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return Hand != 0 && GetCursorInfo(ref info) && info.Cursor == Hand;
    }
}
