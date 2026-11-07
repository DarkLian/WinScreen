using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

/// <summary>One thing a view could show: a window, or a display when <see cref="Device"/> is set.</summary>
internal sealed record PipChoice(string Title, string Program, nint Window, string? Device, bool Minimized);

/// <summary>What's on offer in the source picker. Read once each time the list is opened or searched.</summary>
internal static class PipSourceList
{
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc callback, nint parameter);

    private delegate bool EnumProc(nint window, nint parameter);

    /// <param name="allowed">Whether WinScreen may show this window, which rules out its own and excluded ones.</param>
    internal static List<PipChoice> Windows(Func<nint, bool> allowed)
    {
        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            found.Add(window);
            return true;
        }, 0);

        return found.Where(window => IsOffered(window) && allowed(window))
            .Select(window => new PipChoice(WindowInfo.Title(window), WindowInfo.ProgramOf(window), window, null,
                User32.IsIconic(window)))
            .ToList();
    }

    internal static List<PipChoice> Screens() => Screen.AllScreens
        .Select(screen => new PipChoice(new ScreenPipSource(screen.DeviceName, null).Title,
            $"{screen.Bounds.Width}×{screen.Bounds.Height}" + (screen.Primary ? " " + T("pip.sources.mainDisplay") : ""), 0,
            screen.DeviceName, false))
        .ToList();

    /// <summary>True for a window a person would recognize that is on this desktop and not already concealed.</summary>
    private static bool IsOffered(nint window) =>
        WindowInfo.IsMainWindow(window) && !WindowInfo.IsConcealed(window) &&
        (Dwmapi.DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0);

    internal static bool Matches(PipChoice choice, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(word =>
            choice.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            choice.Program.Contains(word, StringComparison.CurrentCultureIgnoreCase));
}
