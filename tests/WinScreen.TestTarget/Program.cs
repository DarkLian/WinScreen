using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

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
        using var unhang = new ManualResetEventSlim();

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
                    if (parts[0] == "animate")
                        window.BeginInvoke(() => window.Animate(int.Parse(parts[1])));
                    if (parts[0] == "size")
                        window.BeginInvoke(() =>
                        {
                            string[] dimensions = parts[1].Split(' ');
                            window.Size = new Size(int.Parse(dimensions[0]), int.Parse(dimensions[1]));
                        });
                    if (parts[0] == "minimum")
                        window.BeginInvoke(() =>
                        {
                            string[] dimensions = parts[1].Split(' ');
                            window.MinimumSize = new Size(int.Parse(dimensions[0]), int.Parse(dimensions[1]));
                            Console.WriteLine("minimum");
                            Console.Out.Flush();
                        });
                    if (parts[0] == "layered")
                        window.BeginInvoke(window.CreateUnattributedLayeredWindow);
                    if (parts[0] == "plain")
                        window.BeginInvoke(() => window.CreatePlainWindow(parts[1]));
                    if (parts[0] == "frame")
                        window.BeginInvoke(() =>
                        {
                            window.FormBorderStyle = FormBorderStyle.Sizable;
                            Console.WriteLine("framed");
                            Console.Out.Flush();
                        });
                    if (parts[0] == "button")
                        window.BeginInvoke(() =>
                        {
                            var button = new TargetForm.RecordingButton { Text = "Test control", Bounds = new Rectangle(20, 30, 140, 50) };
                            button.Click += (_, _) => { Console.WriteLine("control-click"); Console.Out.Flush(); };
                            window.Controls.Add(button);
                            Console.WriteLine("button");
                            Console.Out.Flush();
                        });
                    if (parts[0] == "popup")
                        window.BeginInvoke(() =>
                        {
                            // An owned popup at a screen position, like a browser's link preview.
                            string[] at = parts[1].Split(' ');
                            var popup = new Form
                            {
                                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                                Bounds = new Rectangle(int.Parse(at[0]), int.Parse(at[1]), 160, 40), BackColor = Color.Gold, Owner = window,
                            };
                            _ = popup.Handle;
                            if (at.Length > 2 && at[2] == "display")
                            {
                                // Click-through, as a browser's link text and tooltips are.
                                nint style = NativeShow.GetWindowLongPtr(popup.Handle, -20);
                                NativeShow.SetWindowLongPtr(popup.Handle, -20, style | 0x80000 | 0x20);
                                NativeShow.SetLayeredWindowAttributes(popup.Handle, 0, 255, 2);
                            }
                            NativeShow.ShowWindow(popup.Handle, 4); // Shown without taking the focus, as link previews are.
                            Console.WriteLine("popup " + popup.Handle.ToInt64());
                            Console.Out.Flush();
                        });
                    if (parts[0] == "dialog")
                        window.BeginInvoke(() =>
                        {
                            // A question the window asks, as "Save changes?" does.
                            var dialog = new Form
                            {
                                FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                                Bounds = new Rectangle(window.Left + 40, window.Top + 60, 260, 140), Text = "Save changes?",
                                MinimizeBox = false, MaximizeBox = false,
                            };
                            dialog.Show(window);
                            Console.WriteLine("dialog " + dialog.Handle.ToInt64());
                            Console.Out.Flush();
                        });
                    if (parts[0] == "slow")
                        window.BeginInvoke(() =>
                        {
                            window.SlowMilliseconds = int.Parse(parts[1]);
                            Console.WriteLine("slow");
                            Console.Out.Flush();
                        });
                    if (parts[0] == "recordmessages")
                        window.BeginInvoke(window.RecordMessages);
                    if (parts[0] == "browserlike")
                        window.BeginInvoke(window.BehaveLikeBrowser);
                    if (parts[0] == "captionhit")
                        window.BeginInvoke(() => window.UseCaptionHit(int.Parse(parts[1])));
                    if (parts[0] == "titlebar")
                        window.BeginInvoke(window.UseTitleBar);
                    if (parts[0] == "hang")
                    {
                        unhang.Reset();
                        window.BeginInvoke(() =>
                        {
                            Console.WriteLine("hung");
                            Console.Out.Flush();
                            var blocked = Stopwatch.StartNew();
                            while (!unhang.IsSet && blocked.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                        });
                    }
                    if (parts[0] == "unhang")
                    {
                        unhang.Set();
                        window.BeginInvoke(() => { Console.WriteLine("responsive"); Console.Out.Flush(); });
                    }
                }

                window.BeginInvoke(window.Close);
            }) { IsBackground = true };
            input.Start();
        };

        Application.Run(window);
    }
}

internal static partial class NativeShow
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
}

internal sealed partial class TargetForm : Form
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(SafeWaitHandle timer, in long due, int period,
        nint callback, nint argument, nint context, uint tolerance);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, int message, nint wparam, nint lparam);
    private readonly ManualResetEvent _animationStop = new(false);
    private bool _animating;
    private int _animationPending, _phase;

    public void Animate(int rate)
    {
        if (_animating) return;
        _animating = true;
        nint hwnd = Handle;
        FormClosed += (_, _) => _animationStop.Set();
        var worker = new Thread(() =>
        {
            using var timer = new EventWaitHandle(false, EventResetMode.AutoReset);
            nint native = CreateWaitableTimerEx(0, 0, 2, 0x1F0003);
            if (native == 0) return;
            timer.SafeWaitHandle = new SafeWaitHandle(native, true);
            WaitHandle[] waits = [timer, _animationStop];
            double deadline = Stopwatch.GetTimestamp();
            while (!_animationStop.WaitOne(0))
            {
                deadline = Math.Max(deadline + Stopwatch.Frequency / (double)Math.Clamp(rate, 1, 480), Stopwatch.GetTimestamp());
                long due = -Math.Max(1, (long)((deadline - Stopwatch.GetTimestamp()) * 10_000_000.0 / Stopwatch.Frequency));
                if (!SetWaitableTimerEx(timer.SafeWaitHandle, in due, 0, 0, 0, 0, 0) || WaitHandle.WaitAny(waits) == 1) return;
                if (Interlocked.Exchange(ref _animationPending, 1) == 0) PostMessage(hwnd, 0x8001, 0, 0);
            }
        }) { IsBackground = true, Name = "Pattern fixture" };
        worker.Start();
    }
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    /// <summary>A borderless window has no title bar to ask about, so the top strip claims to be one.</summary>
    public void RecordMessages()
    {
        _recordMessages = true;
        Report("recordmessages");
    }

    /// <summary>
    /// Behaves as a browser page does with the mouse: asks to hear when the pointer leaves, shows a text cursor,
    /// and holds the mouse while a button is down. Reports each of those messages too.
    /// </summary>
    public void BehaveLikeBrowser()
    {
        _browserLike = true;
        _recordMessages = true;
        Report("browserlike");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse
    {
        public int Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouse track);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")]
    private static partial nint SetCapture(nint window);
    private bool _browserLike;

    public void UseCaptionHit(int hit)
    {
        _hasTitleBar = true;
        _captionHit = hit;
        Report("captionhit");
    }

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

    /// <summary>
    /// A plain native window, for tests where WinForms itself gets in the way: on Windows 10 it puts a form
    /// back where its code placed it when the form is restored from minimized.
    /// </summary>
    public void CreatePlainWindow(string bounds)
    {
        int[] b = [.. bounds.Split(' ').Select(int.Parse)];
        const uint popup = 0x80000000, visible = 0x10000000;
        nint plain = CreateWindowEx(0, "STATIC", "Plain test window", popup | visible, b[0], b[1], b[2], b[3], 0, 0, 0, 0);
        Report("plain " + plain.ToInt64());
    }

    private const int WM_INPUT = 0x00FF;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2;
    private const int TitleBarHeight = 34;
    private const int RealInputWindowMilliseconds = 250;

    private Color[] _colors = [];
    private DateTime _lastRawInput = DateTime.MinValue;
    private bool _recordMessages;
    private bool _hasTitleBar;
    private int _captionHit = HTCAPTION;

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

    /// <summary>How long each move or resize takes to handle, like an app that's slow to lay itself out.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SlowMilliseconds { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (_recordMessages && m.Msg is >= 0x200 and <= 0x20e)
            Report($"mouse {m.Msg:X} {(short)m.LParam.ToInt64()} {(short)(m.LParam.ToInt64() >> 16)} {m.WParam.ToInt64()}");
        if (_browserLike)
        {
            if (m.Msg is 0x2A3 or 0x215 or 0x06 or 0x07 or 0x08 or 0x1C)
                Report($"event {m.Msg:X} {m.WParam.ToInt64()}");
            if (m.Msg == 0x200)
            {
                var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x2, Window = Handle };
                TrackMouseEvent(ref track);
                SetCursor(LoadCursor(0, 32513)); // IDC_IBEAM
            }
            if (m.Msg == 0x201) SetCapture(Handle);
            if (m.Msg == 0x20) // WM_SETCURSOR
            {
                SetCursor(LoadCursor(0, 32513));
                m.Result = 1;
                return;
            }
        }
        if (m.Msg == 0x0047 && SlowMilliseconds > 0) // WM_WINDOWPOSCHANGED
            Thread.Sleep(SlowMilliseconds);
        if (m.Msg == 0x8001)
        {
            Interlocked.Exchange(ref _animationPending, 0);
            _phase++;
            Invalidate();
            return;
        }
        if (m.Msg == WM_INPUT)
            _lastRawInput = DateTime.UtcNow;
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && _hasTitleBar)
        {
            int y = (short)(m.LParam.ToInt64() >> 16);
            if (y - Top < TitleBarHeight)
                m.Result = _captionHit;
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Report($"wheel {e.Delta} real={InputIsReal}");
    }

    internal sealed class RecordingButton : Button
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg is 0x201 or 0x202)
                Report($"child {m.Msg:X} {(short)m.LParam.ToInt64()} {(short)(m.LParam.ToInt64() >> 16)}");
            base.WndProc(ref m);
        }
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
        if (_animating)
        {
            using var brush = new SolidBrush(Color.FromArgb(_phase % 256, (_phase * 17) % 256, (_phase * 37) % 256));
            e.Graphics.FillRectangle(brush, 0, 0, ClientSize.Width, ClientSize.Height);
            for (int x = 0; x < ClientSize.Width; x += 32) e.Graphics.DrawLine(Pens.White, x + _phase % 32, 0, x, ClientSize.Height);
        }
    }
}
