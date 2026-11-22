using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace WinScreen.TestTarget;

/// <summary>
/// Input for a test that doesn't run as administrator, sent from this program, which does. Each line on the test's
/// pipe is one command, answered "ok" once done, so the test's own timing holds:
/// "key VK FLAGS", "mouse FLAGS DX DY DATA", "cursor X Y", "front" (this window takes the focus), "exit".
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

    // A click on it, as a person would give it the focus. The pointer goes back where it was. Raised first: opened
    // while another window had the focus, it can be behind that one, and the click must never land on someone else's.
    private static bool Front(nint window)
    {
        if (GetForegroundWindow() == window) return true;
        SetWindowPos(window, -1, 0, 0, 0, 0, NoMoveSizeActivate);
        SetWindowPos(window, -2, 0, 0, 0, 0, NoMoveSizeActivate);
        GetWindowRect(window, out Bounds bounds);
        Point middle = new() { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
        if (GetAncestor(WindowFromPoint(middle), 2) != window) return false;
        GetCursorPos(out Point was);
        SetCursorPos(middle.X, middle.Y);
        Thread.Sleep(50);
        if (GetAncestor(WindowFromPoint(middle), 2) != window)
        {
            SetCursorPos(was.X, was.Y);
            return false;
        }
        mouse_event(0x0002, 0, 0, 0, 0);
        mouse_event(0x0004, 0, 0, 0, 0);
        long end = Environment.TickCount64 + 2000;
        while (GetForegroundWindow() != window && Environment.TickCount64 < end) Thread.Sleep(20);
        SetCursorPos(was.X, was.Y);
        return GetForegroundWindow() == window;
    }

    private const uint NoMoveSizeActivate = 0x0002 | 0x0001 | 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds { public int Left, Top, Right, Bottom; }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Bounds bounds);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);
}
