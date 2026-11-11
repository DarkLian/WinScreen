using WinScreen.Settings;
using Xunit;

namespace WinScreen.Tests;

/// <summary>How Settings shows what is chosen and where the keyboard is, the Windows 11 way.</summary>
public sealed class SelectionLookTests
{
    /// <summary>The ring around where the keyboard is shows only while the keyboard is used to move; a click hides it.</summary>
    [Fact]
    public void The_focus_ring_follows_the_keyboard_and_a_click_hides_it()
    {
        const int WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201;
        FocusVisuals.Saw(WM_LBUTTONDOWN, 0);
        Assert.False(FocusVisuals.Keyboard);
        FocusVisuals.Saw(WM_KEYDOWN, (nint)Keys.A); // Typing isn't moving around.
        Assert.False(FocusVisuals.Keyboard);
        FocusVisuals.Saw(WM_KEYDOWN, (nint)Keys.Tab);
        Assert.True(FocusVisuals.Keyboard);
        FocusVisuals.Saw(WM_LBUTTONDOWN, 0);
        Assert.False(FocusVisuals.Keyboard);
    }

    /// <summary>
    /// A program in a list shows its icon even when it isn't running: its file is found once and remembered, so the
    /// icon is still there next time.
    /// </summary>
    [Fact]
    public void A_program_icon_is_found_and_its_file_remembered()
    {
        string folder = Directory.CreateTempSubdirectory("winscreen-icons-").FullName;
        string? before = ProgramIcons.File;
        try
        {
            ProgramIcons.File = Path.Combine(folder, "program-paths.json");
            nint shell = Native.User32.FindWindow("Shell_TrayWnd", null);
            Assert.NotEqual(0, shell);
            ProgramIcons.Remember("explorer", shell);
            Assert.NotNull(ProgramIcons.Of("explorer", 16));
            Assert.Contains("explorer.exe", File.ReadAllText(ProgramIcons.File), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProgramIcons.File = before;
            Directory.Delete(folder, true);
        }
    }
}
