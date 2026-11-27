using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Windows;
using static WinScreen.Localization.UiText;

namespace WinScreen.Pip;

internal sealed record PipChoice(string Title, string Program, nint Window, string? Device, bool Minimized)
{
    internal WindowIdentity? Identity { get; init; }
}

internal static class PipSourceList
{
    private const int DWMWA_CLOAKED = 14;

    internal static List<PipChoice> Windows(Func<nint, bool> allowed)
    {
        var found = new List<nint>();
        TopWindows.Each(window =>
        {
            found.Add(window);
            return true;
        });

        return found.Where(window => IsOffered(window) && allowed(window))
            .Select(window => Choice(window)).OfType<PipChoice>()
            .ToList();
    }

    private static PipChoice? Choice(nint window)
    {
        try
        {
            return new PipChoice(WindowInfo.Title(window), WindowInfo.ProgramOf(window), window, null, User32.IsIconic(window))
                { Identity = WindowIdentity.Observe(window) };
        }
        catch (WindowOperationException) { return null; }
    }

    internal static List<PipChoice> Screens() => Screen.AllScreens
        .Select(screen => new PipChoice(new ScreenPipSource(screen.DeviceName, null).Title,
            $"{screen.DeviceName}: {screen.Bounds.Width}×{screen.Bounds.Height} @ {screen.Bounds.X},{screen.Bounds.Y}" + (screen.Primary ? " " + T("pip.sources.mainDisplay") : ""), 0,
            screen.DeviceName, false))
        .ToList();

    private static bool IsOffered(nint window) =>
        WindowInfo.IsMainWindow(window) && !WindowInfo.IsConcealed(window) &&
        (Dwmapi.DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0);

    internal static bool Matches(PipChoice choice, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(word =>
            choice.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            choice.Program.Contains(word, StringComparison.CurrentCultureIgnoreCase));
}
