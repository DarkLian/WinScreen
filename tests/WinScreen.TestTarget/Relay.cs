using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace WinScreen.TestTarget;

/// <summary>
/// Input for a test that doesn't run as administrator, sent from this program, which does. Each line on the test's
/// pipe is one command, answered "ok" once done, so the test's own timing holds:
/// "key VK FLAGS", "mouse FLAGS DX DY DATA", "cursor X Y", "held" and "release" (mouse buttons Windows takes for held; release lets them go), "front" (this window
/// takes the focus, by its own Ctrl+Alt+F23), "exit".
/// The program ends when the pipe closes.
/// </summary>
internal static partial class Relay
{
    public static void HideConsole() => FreeConsole();

    public static void Serve(string pipe, Form window)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            stream.Connect(15000);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            nint handle = (nint)window.Invoke(() => window.Handle);
            window.Invoke(() =>
            {
                RegisterHotKey(0, HotkeyId, 0x0002 | 0x0001 | 0x4000, F23); // Ctrl+Alt+F23, no repeat.
                Application.AddMessageFilter(new HotkeyFilter(handle));
            });
            while (reader.ReadLine() is { } line)
            {
                string[] part = line.Split(' ');
                if (part[0] == "exit") break;
                string answer = "ok";
                switch (part[0])
                {
                    case "key":
                        keybd_event((byte)int.Parse(part[1]), 0, uint.Parse(part[2]), 0);
                        break;
                    case "mouse":
                        mouse_event(uint.Parse(part[1]), int.Parse(part[2]), int.Parse(part[3]), int.Parse(part[4]), 0);
                        break;
                    case "cursor":
                        SetCursorPos(int.Parse(part[1]), int.Parse(part[2]));
                        break;
                    case "held":
                        answer = "ok " + Held();
                        break;
                    case "release":
                        string held = Held();
                        if (held.Contains('L')) mouse_event(0x0004, 0, 0, 0, 0);
                        if (held.Contains('R')) mouse_event(0x0010, 0, 0, 0, 0);
                        if (held.Contains('M')) mouse_event(0x0040, 0, 0, 0, 0);
                        answer = "ok " + held;
                        break;
                    case "front":
                        answer = Front(handle) ? "ok" : "failed";
                        break;
                    default:
                        answer = "unknown";
                        break;
                }
                writer.WriteLine(answer);
            }
        }
        catch (Exception)
        {
            // The test went: so does this.
        }
        window.BeginInvoke(window.Close);
    }

    // Its own hotkey, pressed from here: Windows lets the program that gets it take the focus. A click would do too, but
    // the windows of a test that isn't elevated can then take the left button for held, and hover never starts.
    private static bool Front(nint window)
    {
        if (GetForegroundWindow() == window) return true;
        SetWindowPos(window, -1, 0, 0, 0, 0, NoMoveSizeActivate);
        SetWindowPos(window, -2, 0, 0, 0, 0, NoMoveSizeActivate);
        foreach (byte key in new byte[] { 0x11, 0x12, F23 }) keybd_event(key, 0, 0, 0);
        foreach (byte key in new byte[] { F23, 0x12, 0x11 }) keybd_event(key, 0, 2, 0);
        long end = Environment.TickCount64 + 2000;
        while (GetForegroundWindow() != window && Environment.TickCount64 < end) Thread.Sleep(20);
        return GetForegroundWindow() == window;
    }

    // The mouse buttons Windows takes for held, as "L", "R", "M" or none.
    private static string Held() =>
        ((GetAsyncKeyState(1) & 0x8000) != 0 ? "L" : "") + ((GetAsyncKeyState(2) & 0x8000) != 0 ? "R" : "") + ((GetAsyncKeyState(4) & 0x8000) != 0 ? "M" : "");

    private const int HotkeyId = 0x5754;
    private const byte F23 = 0x86;

    private sealed class HotkeyFilter(nint window) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x0312 || m.WParam != HotkeyId) return false;
            SetForegroundWindow(window);
            return true;
        }
    }

    private const uint NoMoveSizeActivate = 0x0002 | 0x0001 | 0x0010;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, int dx, int dy, int data, nuint extra);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
