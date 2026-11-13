using System.Diagnostics;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>Enhanced hover for a program added to Settings by name, rather than one of the kinds it knows.</summary>
[Trait("Category", "Interactive")]
public sealed class NamedProgramHoverTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string TestProgram = "WinScreen.TestTarget";

    [NativeHoverFact]
    public void A_program_added_by_name_keeps_hover_through_its_view_and_one_not_added_is_told_the_pointer_left()
    {
        using var source = new TargetProcess(new Rectangle(160, 120, 800, 600));
        source.BehaveLikeBrowser(); // Asks to hear when the pointer leaves, as a page with hover effects does.
        PipHoverPreferences before = PipHoverApps.Preferences;
        try
        {
            int unlisted = Visit(source, listed: false);
            int listed = Visit(source, listed: true);
            output.WriteLine($"Leaves while the pointer moved over the view: not added {unlisted}, added {listed}.");
            Assert.True(unlisted > 0, "Without Enhanced hover the app was never told the pointer left, so this test shows nothing.");
            Assert.Equal(0, listed);
        }
        finally { PipHoverApps.Preferences = before; }
    }

    /// <summary>Moves the pointer about over a view of the window and counts how often the app hears it left.</summary>
    private int Visit(TargetProcess source, bool listed)
    {
        int leaves = 0;
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            PipHoverApps.Preferences = new PipHoverPreferences { ChooseForMe = false, Programs = listed ? [TestProgram] : [] };
            Assert.Equal(listed ? PipHoverApp.Listed : PipHoverApp.None, PipEnhancedHover.Describe(source.Handle).App);
            Assert.True(PipEnhancedHover.Describe(source.Handle).Native);
            Assert.Equal(listed, PipEnhancedHover.StartsOn(source.Handle));

            var windows = new WindowController([]);
            Point home = Desktop.CursorPosition();
            Desktop.MoveMouse(new Point(860, 230));
            using var view = new PipWindow(source.Handle, windows) { AllowInteractive = true };
            view.ShowOnTop();
            view.Location = new Point(900, 250);
            view.SetInteractive(true); // Enhanced hover is never set here: it comes from Settings.
            Exception? failure = null;
            var driver = new Thread(() =>
            {
                try
                {
                    var start = new Point(view.Left + view.Width / 3, view.Top + view.Height / 3);
                    Desktop.SlideMouse(start);
                    Assert.True(Desktop.WaitFor(() => view.LiveInteraction is not null),
                        $"No direct-input visit started (added: {listed}, view {view.Bounds}, pointer {Desktop.CursorPosition()}, parked {windows.IsParked(source.Handle)}).");
                    Assert.Equal(listed, view.LiveInteraction!.EnhancedHover);
                    Thread.Sleep(400);
                    while (source.NextReport(100) is not null) { }

                    int moves = 0;
                    for (int step = 1; step <= 8; step++)
                    {
                        Desktop.SlideMouse(start + new Size(step * 12, step * 7));
                        Thread.Sleep(250);
                        while (source.NextReport(20) is { } report)
                        {
                            if (report.StartsWith("event 2A3")) leaves++;
                            if (report.StartsWith("mouse 200 ")) moves++;
                        }
                    }
                    Assert.True(moves > 0, "No pointer moves reached the app.");
                    if (!listed) return;
                    Assert.StartsWith("active", view.LiveInteraction?.HoverStatus ?? "no visit");

                    // Leaving the view still tells the app, so its hover clears.
                    Desktop.SlideMouse(new Point(view.Left - 60, view.Top + 100));
                    Assert.True(Desktop.WaitFor(() => source.NextReport(50) is { } report && report.StartsWith("event 2A3"), 4000),
                        "The app was not told the pointer left when it left the view.");
                }
                catch (Exception e) { failure = e; }
                finally { Desktop.MoveMouse(home); }
            });
            driver.Start();
            while (driver.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            view.Close();
            Assert.Equal(0, Native.User32.GetProp(source.Handle, WindowController.EnhancedHoverOwnerProperty));
            if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        });
        return leaves;
    }
}

/// <summary>The helper runs inside the app, so a program added by name gets it only when it is a 64-bit one.</summary>
[Trait("Category", "RealWindows")]
public sealed class NamedProgramArchitectureTests
{
    [Fact]
    public void A_32_bit_program_added_by_name_never_gets_enhanced_hover()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "charmap.exe");
        if (!Environment.Is64BitOperatingSystem || !File.Exists(exe)) { Skipped.Because("No 32-bit Character Map here."); return; }
        PipHoverPreferences before = PipHoverApps.Preferences;
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
        try
        {
            Assert.True(Desktop.WaitFor(() => { process.Refresh(); return process.MainWindowHandle != 0; }, 15000), "Character Map did not open.");
            nint window = process.MainWindowHandle;
            PipHoverApps.Preferences = new PipHoverPreferences { ChooseForMe = false, Programs = ["charmap"] };

            Assert.Equal((PipHoverApp.Listed, false), PipEnhancedHover.Describe(window));
            Assert.False(PipEnhancedHover.CanAttempt(window));
            Assert.False(PipEnhancedHover.StartsOn(window));
        }
        finally
        {
            PipHoverApps.Preferences = before;
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        }
    }
}
