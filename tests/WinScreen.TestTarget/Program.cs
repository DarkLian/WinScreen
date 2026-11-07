using System.Runtime.InteropServices;

namespace WinScreen.TestTarget;

/// <summary>
/// A plain window in its own process for the tests to act on, so they never touch a real app.
/// It prints its window handle, then obeys "color RRGGBB[,RRGGBB]" and "exit" on standard input.
/// It reports clicks and typing on standard output, saying whether raw input came with them: only
/// real input does, so a forged window message can't pass for a real click.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var options = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
        string Get(string name, string fallback) => options.GetValueOrDefault(name, fallback);

        var window = new TargetForm(
            new Rectangle(int.Parse(Get("--left", "200")), int.Parse(Get("--top", "200")),
                int.Parse(Get("--width", "400")), int.Parse(Get("--height", "300"))),
            Get("--title", "WinScreen test target"),
            Get("--colors", "C81E1E"));

        window.Shown += (_, _) =>
        {
            Console.Out.WriteLine(window.Handle.ToInt64());
            Console.Out.Flush();
            var input = new Thread(() =>
            {
                while (Console.In.ReadLine() is { } line)
                {
                    string[] parts = line.Split(' ', 2);
                    if (parts[0] == "exit")
                        break;
                    if (parts[0] == "color")
                        window.BeginInvoke(() => window.SetColors(parts[1]));
                    if (parts[0] == "layered")
                        window.BeginInvoke(window.CreateUnattributedLayeredWindow);
                    if (parts[0] == "titlebar")
                        window.BeginInvoke(window.UseTitleBar);
                    if (parts[0] == "hang")
                        window.BeginInvoke(() =>
                        {
                            Console.WriteLine("hung");
                            Console.Out.Flush();
                            Thread.Sleep(10000);
                        });
                }

                window.BeginInvoke(window.Close);
            }) { IsBackground = true };
            input.Start();
        };

        Application.Run(window);
    }
}

internal sealed partial class TargetForm : Form
{
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    /// <summary>A borderless window has no title bar to ask about, so the top strip claims to be one.</summary>
    public void UseTitleBar()
    {
        _hasTitleBar = true;
        Report("titlebar");
    }

    public void CreateUnattributedLayeredWindow()
    {
        // Native STATIC avoids WinForms automatically calling SetLayeredWindowAttributes on style changes.
        nint layered = CreateWindowEx(0x80000, "STATIC", "Unattributed layered test", 0,
            700, 200, 100, 100, 0, 0, 0, 0);
        Report("layered " + layered.ToInt64());
    }

    private const int WM_INPUT = 0x00FF;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2;
    private const int TitleBarHeight = 34;
    private const int RealInputWindowMilliseconds = 250;

    private Color[] _colors = [];
    private DateTime _lastRawInput = DateTime.MinValue;
    private bool _hasTitleBar;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    public TargetForm(Rectangle bounds, string title, string colors)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        Text = title;
        DoubleBuffered = true;
        SetColors(colors);
    }

    protected override bool ShowWithoutActivation => true;

    private bool InputIsReal => (DateTime.UtcNow - _lastRawInput).TotalMilliseconds < RealInputWindowMilliseconds;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const ushort desktopControls = 1, mouse = 2, keyboard = 6;
        RegisterRawInputDevices(
        [
            new RawInputDevice { UsagePage = desktopControls, Usage = mouse, Target = Handle },
            new RawInputDevice { UsagePage = desktopControls, Usage = keyboard, Target = Handle },
        ], 2, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
            _lastRawInput = DateTime.UtcNow;
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && _hasTitleBar)
        {
            int y = (short)(m.LParam.ToInt64() >> 16);
            if (y - Top < TitleBarHeight)
                m.Result = HTCAPTION;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Report($"click {e.X} {e.Y} real={InputIsReal}");
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        Report($"key {e.KeyChar} real={InputIsReal}");
    }

    private static void Report(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    /// <summary>One colour fills the window; two split it into a left and a right half.</summary>
    public void SetColors(string colors)
    {
        _colors = colors.Split(',').Select(c => ColorTranslator.FromHtml("#" + c)).ToArray();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int width = ClientSize.Width / _colors.Length;
        for (int i = 0; i < _colors.Length; i++)
        {
            using var brush = new SolidBrush(_colors[i]);
            int span = i == _colors.Length - 1 ? ClientSize.Width - i * width : width;
            e.Graphics.FillRectangle(brush, i * width, 0, span, ClientSize.Height);
        }
    }
}
