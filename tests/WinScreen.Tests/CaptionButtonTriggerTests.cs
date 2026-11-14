using System.Diagnostics;
using WinScreen.Native;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Tray;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// The minimize trigger follows the minimize button where Windows draws it. Notepad++ and PrismLauncher answer
/// "minimize" over the drawn maximize button (an older, narrower layout), so the toolbar opened only when resting on
/// maximize (the owner, 15 Nov). Asked directly, without the mouse.
/// </summary>
[Trait("Category", "RealWindows")]
public sealed class CaptionButtonTriggerTests
{
    private static Point Middle(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static void Check(nint window, string app)
    {
        Rectangle minimize = WindowInfo.CaptionButtonBounds(window, WindowInfo.CaptionButton.Minimize)
            ?? throw new InvalidOperationException($"Windows reports no minimize button for {app}.");
        Rectangle maximize = WindowInfo.CaptionButtonBounds(window, WindowInfo.CaptionButton.Maximize)
            ?? throw new InvalidOperationException($"Windows reports no maximize button for {app}.");
        Assert.True(WindowToolbar.MatchesTrigger(window, Middle(minimize), ToolbarTrigger.MinimizeButton),
            $"Resting on {app}'s minimize button at {Middle(minimize)} doesn't count.");
        Assert.False(WindowToolbar.MatchesTrigger(window, Middle(maximize), ToolbarTrigger.MinimizeButton),
            $"Resting on {app}'s maximize button at {Middle(maximize)} counts as minimize.");
    }

    [Fact]
    public void On_Notepad_plus_plus_the_drawn_minimize_button_counts_and_maximize_does_not()
    {
        string notepad = Path.Combine(AppContext.BaseDirectory, "test-apps", "notepad++", "notepad++.exe");
        if (!File.Exists(notepad)) { Skipped.Because("The portable Notepad++ isn't in the test output."); return; }
        string file = Path.Combine(Path.GetTempPath(), $"winscreen-caption-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "caption");
        using var app = Process.Start(new ProcessStartInfo(notepad) { ArgumentList = { "-multiInst", "-nosession", file } })!;
        try
        {
            nint window = 0;
            Assert.True(Desktop.WaitFor(() => { app.Refresh(); return (window = app.MainWindowHandle) != 0; }, 15000), "Notepad++ didn't open.");
            Thread.Sleep(1000);
            User32.SetWindowPos(window, 0, 200, 120, 900, 600, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Thread.Sleep(300);
            Check(window, "Notepad++");
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            app.WaitForExit(3000);
            File.Delete(file);
        }
    }

    [Fact]
    public void On_File_Explorer_the_drawn_minimize_button_counts_and_maximize_does_not()
    {
        string root = Directory.CreateTempSubdirectory("WinScreenCaption-").FullName;
        nint window = 0;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true })?.Dispose();
            Assert.True(Desktop.WaitFor(() =>
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                    using (process)
                        if (process.MainWindowTitle.StartsWith(Path.GetFileName(root), StringComparison.Ordinal)) window = process.MainWindowHandle;
                return window != 0;
            }, 15000), "File Explorer didn't open the folder.");
            Thread.Sleep(1500);
            User32.SetWindowPos(window, 0, 200, 120, 1000, 600, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Thread.Sleep(500);
            Check(window, "File Explorer");
        }
        finally
        {
            if (window != 0 && User32.IsWindow(window)) Desktop.PostMessage(window, User32.WM_CLOSE, 0, 0);
            Thread.Sleep(500);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
