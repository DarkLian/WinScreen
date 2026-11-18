using System.Runtime.InteropServices;

namespace WinScreen.Native;

// Asks only Windows, never another program, so hooks may use it.
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

    internal static bool IsHand()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return Hand != 0 && GetCursorInfo(ref info) && info.Cursor == Hand;
    }
}
