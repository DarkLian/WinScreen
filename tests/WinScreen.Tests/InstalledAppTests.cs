using System.Diagnostics;
using System.Text;
using WinScreen.Native;
using WinScreen.Pip;
using WinScreen.Settings;
using WinScreen.Tests.Support;
using WinScreen.Windows;
using Xunit;

namespace WinScreen.Tests;

/// <summary>
/// Common desktop apps installed on the test PC, used through a view: Office, IntelliJ, LTspice and chat apps.
/// They aren't portable, so these run only where they are installed and only when WINSCREEN_TEST_INSTALLED_APPS is
/// 1, since some use the person's own copy. Apps that are already running (a chat app) are never typed into or
/// closed: those get a pointer visit and a context menu only.
/// </summary>
public sealed partial class AppCompatibilityTests
{
    private static bool InstalledAppsWanted => Environment.GetEnvironmentVariable("WINSCREEN_TEST_INSTALLED_APPS") == "1";
    private static string Office(string exe) => Path.Combine(@"C:\Program Files\Microsoft Office\root\Office16", exe);

    /// <summary>A right-click opens the app's own menu by the pointer, and Escape closes it.</summary>
    private static void ContextMenuByPointer(int process, nint window, Point at, StringBuilder log, bool required = true)
    {
        int before = Popups(process, window).Count;
        Click(at, right: true);
        List<nint> menus = [];
        bool opened = Desktop.WaitFor(() => (menus = Popups(process, window)).Count > before, 3000);
        if (!opened && !required) { log.AppendLine("no context menu here"); return; }
        Assert.True(opened, "No context menu.");
        Rectangle menu = Desktop.BoundsOf(menus[^1]);
        log.AppendLine($"menu {menu} ({WindowInfo.ClassName(menus[^1])}) pointer {Desktop.CursorPosition()}");
        Assert.True(Rectangle.Inflate(menu, 80, 80).Contains(Desktop.CursorPosition()), $"The menu isn't by the pointer: {menu}.");
        Desktop.Press(Keys.Escape);
        Assert.True(Desktop.WaitFor(() => Popups(process, window).Count <= before, 3000), "The menu stayed open.");
    }

    /// <summary>
    /// Asks every window of the process to close, again while new ones appear (IntelliJ shows its Welcome window when
    /// its last editor closes), and ends what is left only when allowed. A JetBrains IDE ended by force leaves a lock
    /// behind that Windows then can't remove, and the IDE won't start again until it's gone, so those are never ended.
    /// </summary>
    private static void CloseApp(Process process, bool mayEnd = true)
    {
        for (int round = 0; round < 20 && !process.HasExited; round++)
        {
            foreach (int id in ProcessTree(process.Id))
                Desktop.EachProcessWindow(id, w => { if (User32.IsWindowVisible(w)) User32.PostMessage(w, 0x0010, 0, 0); });
            process.WaitForExit(3000);
        }
        if (!process.HasExited && mayEnd) CloseTree(process);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint LoadKeyboardLayout(string id, uint flags);

    /// <summary>Typed letters must arrive as letters, not into an input method: a US layout for this window only.</summary>
    private static void EnglishKeys(nint window)
    {
        User32.PostMessage(window, 0x50 /* WM_INPUTLANGCHANGEREQUEST */, 0, LoadKeyboardLayout("00000409", 0));
        Thread.Sleep(300);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GuiThreadInfo { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    /// <summary>
    /// Characters straight to the app's focused control. Keys never pass through WinScreen (the app is in front and
    /// Windows gives them to it), and on a PC with an input method real key presses can end up in its composition.
    /// </summary>
    private static void Type(nint window, string text)
    {
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        nint target = GetGUIThreadInfo(User32.GetWindowThreadProcessId(window, out _), ref info) && info.Focus != 0 ? info.Focus : window;
        foreach (char c in text) { User32.PostMessage(target, 0x0102 /* WM_CHAR */, c, 1); Thread.Sleep(40); }
        Thread.Sleep(200);
    }

    /// <summary>The file as saved so far, read while the app still holds it open (Excel locks what it has open).</summary>
    private static string SavedShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    /// <summary>The text of a saved RTF file, without its control words, groups and line breaks.</summary>
    private static string RtfText(string file) =>
        System.Text.RegularExpressions.Regex.Replace(SavedShared(file), @"\\[a-z]+-?\d* ?|[{}\r\n]", "");

    private static Point Middle(nint window)
    {
        Rectangle bounds = Desktop.BoundsOf(window);
        return new Point(bounds.Width / 2, bounds.Height / 2);
    }

    [Fact]
    public void Word_takes_a_click_typing_saving_and_its_context_menu_through_a_view()
    {
        string word = Office("WINWORD.EXE");
        if (!InstalledAppsWanted || !File.Exists(word) || Process.GetProcessesByName("WINWORD").Length > 0) return;
        string root = Directory.CreateTempSubdirectory("winscreen-word").FullName;
        string file = Path.Combine(root, "probe.rtf");
        // One long word per paragraph in large type, so a double-click anywhere on the page lands on a word.
        string paragraph = @"{\fs56 " + string.Concat(Enumerable.Repeat("probeword", 4)) + @"\par}";
        File.WriteAllText(file, @"{\rtf1\ansi " + string.Concat(Enumerable.Repeat(paragraph, 30)) + "}");
        var start = new ProcessStartInfo(word) { UseShellExecute = false };
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add(file);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("probe"), 60000);
            Assert.True(window != 0, "Word didn't open the file.");
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 120, 80, 1100, 800, 0x14);
            Settle(3000);
            EnglishKeys(window);
            Visit(window, (view, at) =>
            {
                Point middle = Middle(window);
                string Others() => string.Join(", ", Popups(process.Id, window).Select(w =>
                    $"{WindowInfo.ClassName(w)} owner {User32.GetWindow(w, 4) == window} {Desktop.BoundsOf(w)} ex 0x{Desktop.ExStyle(w):X}"));
                Desktop.SlideMouse(at(middle)); Thread.Sleep(1000);
                // A click puts the caret in the text; End, Z and Ctrl+S save it.
                Click(at(middle));
                Type(window, "qqq");
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                Thread.Sleep(1000);
                Assert.True(Desktop.WaitFor(() => RtfText(file).Contains("qqq"), 6000), "After a click, qqq and Ctrl+S the file doesn't have them.");
                ContextMenuByPointer(process.Id, window, at(middle + new Size(0, 60)), log);

                // A double-click to select a word, only noted: Word sometimes opens a small window of its own under
                // the pointer after the first click, and the second lands there.
                Click(at(middle + new Size(0, -60)), times: 2);
                log.AppendLine("after the double-click: " + Others());
                Type(window, "yyy");
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                log.AppendLine("double-click selected a word: " + Desktop.WaitFor(() => RtfText(file).Contains("yyy"), 4000));
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseApp(process);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Excel_takes_a_click_on_a_cell_typing_saving_and_its_context_menu_through_a_view()
    {
        string excel = Office("EXCEL.EXE");
        if (!InstalledAppsWanted || !File.Exists(excel) || Process.GetProcessesByName("EXCEL").Length > 0) return;
        string root = Directory.CreateTempSubdirectory("winscreen-excel").FullName;
        string file = Path.Combine(root, "probe.csv");
        File.WriteAllText(file, string.Concat(Enumerable.Repeat(string.Join(",", Enumerable.Repeat("abc", 26)) + "\r\n", 60)));
        var start = new ProcessStartInfo(excel) { UseShellExecute = false };
        start.ArgumentList.Add(file);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("probe"), 60000);
            Assert.True(window != 0, "Excel didn't open the file.");
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 120, 80, 1100, 800, 0x14);
            Settle(3000);
            EnglishKeys(window);
            Visit(window, (view, at) =>
            {
                Point middle = Middle(window);
                // A click picks the cell under the pointer; typing replaces it; Enter keeps it.
                Click(at(middle));
                Type(window, "qqq");
                Desktop.Press(Keys.Enter);
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                Assert.True(Desktop.WaitFor(() => SavedShared(file).Contains("qqq"), 6000), "After a click, qqq, Enter and Ctrl+S the file doesn't have them.");
                // Only noted: a double-click to edit a cell. Excel won't start editing in a cell while its window is
                // past the edge of the screen (the same posted double-click works on screen).
                Click(at(middle + new Size(0, 60)), times: 2);
                Thread.Sleep(300);
                Desktop.Press(Keys.End);
                Type(window, "yyy");
                Desktop.Press(Keys.Enter);
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                log.AppendLine("double-click edited the cell under the pointer: " + Desktop.WaitFor(() => SavedShared(file).Contains("abcyyy"), 4000));
                ContextMenuByPointer(process.Id, window, at(middle + new Size(80, 40)), log);
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseApp(process);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>A file of long identifiers, so a double-click anywhere in the editor selects a whole line.</summary>
    private static string IdeFile(string root)
    {
        string file = Path.Combine(root, "probe.txt");
        File.WriteAllText(file, string.Concat(Enumerable.Repeat(string.Join("_", Enumerable.Repeat("alpha", 60)) + "\n", 80)));
        return file;
    }

    private static bool EditedLine(string file) => Saved(file).Split('\n').Any(line => line.Trim() is "z" or "Z");

    /// <summary>
    /// The IDE's own editor (Java). Java ignores input while its window is mostly off every display, as a parked
    /// original is (probed 13 Nov 2026: the same posted clicks work on screen, wherever the real pointer is). So a
    /// view of a Java window sends it nothing: a click shows the original, which then takes real clicks and keys.
    /// </summary>
    private void UseIdeEditor(nint window, string file, StringBuilder log)
    {
        User32.ShowWindow(window, 9);
        User32.SetWindowPos(window, 0, 120, 80, 1100, 800, 0x14);
        Settle(3000);
        EnglishKeys(window);
        WindowMenuBuilderTests.OnUiThread(() =>
        {
            Point home = Desktop.CursorPosition();
            try
            {
                Desktop.GainForegroundRights();
                var windows = new WindowController([]);
                using var view = new PipWindow(window, windows) { AllowInteractive = true };
                view.ShowOnTop();
                view.Location = new Point(900, 200);
                view.SetInteractive(true);
                Assert.True(view.KeepsUpdating, "The view doesn't keep showing the editor.");
                Assert.True(view.OpensOriginalOnClick, "A Java window isn't given Show original.");
                var inside = new Point(view.Left + view.Width / 2, view.Top + view.Height / 2);
                Desktop.SlideMouse(inside);
                Thread.Sleep(400);
                Assert.Null(view.LiveInteraction); // Nothing is sent to it.
                Desktop.ClickOn(inside, view.Handle);
                Assert.True(Desktop.WaitFor(() => view.UsingOriginal, 3000, Application.DoEvents), "The click didn't show the editor.");
                Assert.False(windows.IsHidden(window));
                Thread.Sleep(800);

                // Used as it is, on the screen: a real double-click and a real key.
                Rectangle bounds = Desktop.BoundsOf(window);
                Point middle = Middle(window);
                Click(new Point(bounds.Left + middle.X, bounds.Top + middle.Y), times: 2);
                Desktop.Press(Keys.Z);
                Thread.Sleep(300);
                Desktop.Press(Keys.S, Keys.ControlKey);
                bool edited = Desktop.WaitFor(() => EditedLine(file), 4000, Application.DoEvents);
                log.AppendLine("the shown editor took a double-click and typing: " + edited);
                Assert.True(edited, "The editor shown by a click on its view didn't take a double-click and a key.");
                view.Close();
            }
            finally { Desktop.MoveMouse(home); }
        });
    }

    [Fact]
    public void IntelliJ_shows_its_original_on_a_click_on_its_view_and_works_there()
    {
        string idea = Directory.Exists(@"C:\Program Files\JetBrains")
            ? Directory.GetFiles(@"C:\Program Files\JetBrains", "idea64.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "" : "";
        if (!InstalledAppsWanted || !File.Exists(idea) || Process.GetProcessesByName("idea64").Length > 0) return;
        string root = Directory.CreateTempSubdirectory("winscreen-idea").FullName;
        string file = IdeFile(root);
        // Its own settings and data folders, so the person's own are never touched. A new settings folder makes it ask
        // whether to import settings first; the folder is made beforehand and the question turned off.
        string folders = root.Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "config", "options"));
        File.WriteAllLines(Path.Combine(root, "idea.properties"),
        [
            $"idea.config.path={folders}/config", $"idea.system.path={folders}/system",
            $"idea.plugins.path={folders}/plugins", $"idea.log.path={folders}/log",
            "idea.initially.ask.config=never",
        ]);
        var start = new ProcessStartInfo(idea) { UseShellExecute = false };
        start.Environment["IDEA_PROPERTIES"] = Path.Combine(root, "idea.properties");
        start.ArgumentList.Add("-e"); // Its light editor: one file, no project.
        start.ArgumentList.Add(file);
        using var process = Process.Start(start)!;
        var log = new StringBuilder();
        try
        {
            nint window = 0;
            Desktop.WaitFor(() => (window = Desktop.FindProcessWindow(process.Id, t => t.Contains("probe.txt"))) != 0, 90000);
            Assert.True(window != 0, "IntelliJ didn't open the file.");
            UseIdeEditor(window, file, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseApp(process, mayEnd: false);
            Assert.True(process.HasExited, "IntelliJ didn't close when asked.");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LTspice_opens_its_menus_by_the_pointer_through_a_view()
    {
        string ltspice = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ADI\LTspice\LTspice.exe");
        if (!InstalledAppsWanted || !File.Exists(ltspice) || Process.GetProcessesByName("LTspice").Length > 0) return;
        using var process = Process.Start(new ProcessStartInfo(ltspice) { UseShellExecute = false })!;
        var log = new StringBuilder();
        try
        {
            nint window = WaitForWindow(process, t => t.Contains("LTspice"), 60000);
            Assert.True(window != 0, "LTspice didn't open.");
            User32.ShowWindow(window, 9);
            User32.SetWindowPos(window, 0, 120, 80, 1000, 700, 0x14);
            Settle(2500);
            log.AppendLine($"class {WindowInfo.ClassName(window)} menu {GetMenu(window)}");
            Visit(window, (view, at) =>
            {
                ContextMenuByPointer(process.Id, window, at(Middle(window)), log, required: false);
                // The menu bar's first menu, found where Windows says it is.
                nint bar = GetMenu(window);
                if (bar == 0) { log.AppendLine("no standard menu bar"); return; }
                Assert.True(GetMenuItemRect(window, bar, 0, out Rect item));
                Rectangle bounds = Desktop.BoundsOf(window);
                Point first = new((item.Left + item.Right) / 2 - bounds.Left, (item.Top + item.Bottom) / 2 - bounds.Top);
                int before = Popups(process.Id, window).Count;
                Click(at(first));
                List<nint> menus = [];
                Assert.True(Desktop.WaitFor(() => (menus = Popups(process.Id, window)).Count > before, 3000), "The first menu didn't open.");
                log.AppendLine($"menu {Desktop.BoundsOf(menus[^1])}");
                Desktop.Press(Keys.Escape);
                Desktop.Press(Keys.Escape);
                Assert.DoesNotContain(PressOutcome.Unconfirmed, view.PressOutcomes);
            }, log);
        }
        finally
        {
            output.WriteLine(log.ToString());
            CloseApp(process);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetMenu(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMenuItemRect(nint window, nint menu, uint item, out Rect bounds);

    /// <summary>
    /// Chat apps the person has open: never typed into or closed. The view must recognise them, and a visit through
    /// it must reach them. Enhanced hover is tried only where asked for, since it runs inside the app.
    /// </summary>
    [Theory]
    [InlineData("claude", false)]
    [InlineData("ChatGPT", true)]
    public void A_chat_app_that_is_open_is_recognised_and_can_be_used_through_a_view(string program, bool enhancedHover)
    {
        if (!InstalledAppsWanted) return;
        Process? app = Process.GetProcessesByName(program).FirstOrDefault(p => p.MainWindowHandle != 0 && User32.IsWindowVisible(p.MainWindowHandle));
        if (app is null) return;
        using (app)
        {
            nint window = app.MainWindowHandle;
            var log = new StringBuilder();
            PipHoverPreferences before = PipHoverApps.Preferences;
            Rectangle home = Desktop.BoundsOf(window);
            try
            {
                PipHoverApps.Preferences = new PipHoverPreferences();
                (PipHoverApp kind, bool native) = PipEnhancedHover.Describe(window);
                log.AppendLine($"{program}: {kind}, native {native}, class {WindowInfo.ClassName(window)}, at {home}");
                Assert.Equal(PipHoverApp.Electron, kind);
                Assert.True(native);
                Visit(window, (view, at) =>
                {
                    Assert.Equal(enhancedHover, view.LiveInteraction!.EnhancedHover);
                    Point middle = Middle(window);
                    for (int step = 0; step < 6; step++) { Desktop.SlideMouse(at(middle + new Size(step * 20, step * 10))); Thread.Sleep(150); }
                    // It starts once the pointer has been over the app's content.
                    if (enhancedHover) Assert.True(Desktop.WaitFor(() => view.LiveInteraction?.HoverStatus.StartsWith("active") == true, 3000),
                        view.LiveInteraction?.HoverStatus);
                    ContextMenuByPointer(app.Id, window, at(middle), log, required: false);
                }, log, enhancedHover);
            }
            finally
            {
                PipHoverApps.Preferences = before;
                output.WriteLine(log.ToString());
            }
            Assert.Equal(home, Desktop.BoundsOf(window));
            Assert.False(WindowInfo.IsConcealed(window));
        }
    }
}
