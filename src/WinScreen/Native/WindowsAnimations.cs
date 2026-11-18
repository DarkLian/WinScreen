using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>Whether Windows' own animation effects are on (Settings, Accessibility, Visual effects).</summary>
internal static partial class WindowsAnimations
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    /// <summary>On unless Windows says they are off.</summary>
    internal static bool On => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int on, 0) || on != 0;
}
