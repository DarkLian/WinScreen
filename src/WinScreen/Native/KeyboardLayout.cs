using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>Asks the active keyboard layout what a key produces.</summary>
internal static partial class KeyboardLayout
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftControl = 0xA2;
    private const int VkRightMenu = 0xA5;
    private const int Pressed = 0x80;
    private const uint KeepKernelState = 0x4;

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint thread);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    private static partial uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint virtualKey, uint scanCode, byte* state, char* buffer, int size,
        uint flags, nint layout);

    /// <summary>
    /// The text this key types with AltGr, which Windows reports as Ctrl+Alt, or null when it types nothing.
    /// A global Ctrl+Alt hotkey on such a key would stop people typing that character.
    /// </summary>
    public static unsafe string? TextWithAltGr(Keys key, bool shift)
    {
        nint layout = GetKeyboardLayout(0);
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        state[VkControl] = Pressed;
        state[VkMenu] = Pressed;
        state[VkLeftControl] = Pressed;
        state[VkRightMenu] = Pressed;
        if (shift)
            state[VkShift] = Pressed;

        char* buffer = stackalloc char[8];
        int length = ToUnicodeEx((uint)key, MapVirtualKeyEx((uint)key, 0, layout), state, buffer, 8, KeepKernelState,
            layout);
        if (length == 0)
            return null;
        var text = new string(buffer, 0, Math.Abs(length));
        return text.Length > 0 && !char.IsControl(text[0]) ? text : null;
    }
}
