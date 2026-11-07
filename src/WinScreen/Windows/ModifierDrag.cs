using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using WinScreen.Settings;

namespace WinScreen.Windows;

/// <summary>
/// When the mouse hook may exist. It's only there while the feature could act, so a game, a full-screen app,
/// an excluded program or a locked PC never has it running behind them.
/// </summary>
internal static class ModifierHookPolicy
{
    public static bool ShouldRun(bool turnedOn, FullScreenState fullScreen, bool foregroundFillsMonitor,
        bool foregroundExcluded, bool away) =>
        turnedOn && fullScreen == FullScreenState.None && !foregroundFillsMonitor && !foregroundExcluded && !away;
}

/// <summary>
/// Hold a modifier and drag anywhere in a window: left button moves it, right button resizes it from the
/// nearest edge or corner. Raw input can see a click but can't stop it reaching the app, so this needs a
/// low-level mouse hook. That hook and its threads exist only while <see cref="Active"/> is on, and there is
/// no keyboard hook: the modifier is only read at the moment a mouse button goes down.
/// </summary>
internal sealed class ModifierDrag : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012, WM_HOTKEY = 0x0312, WM_APP = 0x8000;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int HC_ACTION = 0;
    private const int VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_ESCAPE = 0x1B;
    private const int MaskKey = 0xE8; // Unassigned: tapping it stops a held Alt or Win opening a menu or Start.
    private const int ClaimEscape = WM_APP + 1, ReleaseEscape = WM_APP + 2;
    private const int EscapeHotkey = 0x5751;
    private const uint KeyUp = 2;
    private const int ThreadExit = 2000;
    private static readonly Size Smallest = new(120, 60);

    private sealed record Gesture(nint Window, bool Resizing, Point Start, Rectangle Original, ResizeEdges Edges, int ReleaseMessage);

    private readonly Func<nint, bool> _eligible;
    private readonly Action<nint> _began;
    private readonly HookProc _proc; // Held here so the collector can't take it while Windows has it.
    private readonly AutoResetEvent _moved = new(false);
    private Thread? _hookThread, _mover;
    private uint _hookThreadId;
    private volatile Gesture? _gesture;
    private volatile bool _swallowRelease;
    private int _pendingRelease;
    private long _point;
    private volatile bool _stopping;
    private long _lastCallback;
    private long _longestCallbackTicks;

    /// <param name="eligible">Whether a window may be moved. Called inside the hook, so it must be quick.</param>
    /// <param name="began">Told once when a gesture starts, off the hook, so the move can be undone.</param>
    public ModifierDrag(Func<nint, bool> eligible, Action<nint> began)
    {
        _eligible = eligible;
        _began = began;
        _proc = Callback;
    }

    /// <summary>The key to hold. Read each time a button goes down, so a change applies straight away.</summary>
    public ModifierKey Key { get; set; }

    public bool Active { get; private set; }

    /// <summary>When the hook last saw any mouse input, for noticing that Windows has quietly removed it.</summary>
    public long LastCallback => Interlocked.Read(ref _lastCallback);

    /// <summary>The slowest the hook has taken to answer, which must stay far under Windows' limit.</summary>
    public TimeSpan LongestCallback => TimeSpan.FromTicks(Interlocked.Read(ref _longestCallbackTicks));

    /// <exception cref="Win32Exception">Windows wouldn't install the hook.</exception>
    public void SetActive(bool on)
    {
        if (on == Active)
            return;
        if (on) Start();
        else Stop();
    }

    public void Dispose()
    {
        Stop();
        _moved.Dispose();
    }

    private void Start()
    {
        _stopping = false;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            nint hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(0), 0);
            if (hook == 0)
                failure = new Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            if (hook == 0)
                return;
            try
            {
                while (GetMessage(out Msg message, 0, 0, 0) > 0)
                {
                    if (message.Message == ClaimEscape)
                        RegisterHotKey(0, EscapeHotkey, User32.MOD_NOREPEAT, VK_ESCAPE);
                    else if (message.Message == ReleaseEscape)
                        UnregisterHotKey(0, EscapeHotkey);
                    else if (message.Message == WM_HOTKEY && message.WParam == EscapeHotkey)
                        Cancel();
                }
            }
            finally
            {
                UnregisterHotKey(0, EscapeHotkey);
                UnhookWindowsHookEx(hook);
            }
        }) { IsBackground = true, Name = "WinScreen modifier hook", Priority = ThreadPriority.AboveNormal };
        _hookThread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _hookThread.Join(ThreadExit);
            _hookThread = null;
            throw failure;
        }

        _mover = new Thread(MoveWindows) { IsBackground = true, Name = "WinScreen modifier mover" };
        _mover.Start();
        Interlocked.Exchange(ref _lastCallback, Stopwatch.GetTimestamp());
        Active = true;
    }

    private void Stop()
    {
        if (!Active)
            return;
        Active = false;
        _stopping = true;
        _gesture = null;
        _swallowRelease = false;
        _moved.Set();
        PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(ThreadExit);
        _mover?.Join(ThreadExit);
        _hookThread = _mover = null;
    }

    private bool ModifierHeld() => Key == ModifierKey.Win
        ? GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0
        : GetAsyncKeyState(VK_MENU) < 0;

    private nint Callback(int code, nint message, nint data)
    {
        long started = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastCallback, started);
        if (code != HC_ACTION)
            return CallNextHookEx(0, code, message, data);

        var info = Marshal.PtrToStructure<MouseHookInfo>(data);
        bool swallow = false;
        switch ((int)message)
        {
            case WM_MOUSEMOVE:
                if (_gesture is not null)
                {
                    Interlocked.Exchange(ref _point, Pack(info.X, info.Y));
                    _moved.Set();
                }

                break;
            case WM_LBUTTONDOWN:
                swallow = _gesture is null && !_swallowRelease && Begin(new Point(info.X, info.Y), false);
                break;
            case WM_RBUTTONDOWN:
                swallow = _gesture is null && !_swallowRelease && Begin(new Point(info.X, info.Y), true);
                break;
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
                swallow = End((int)message);
                break;
        }

        long spent = Stopwatch.GetElapsedTime(started).Ticks;
        if (spent > Interlocked.Read(ref _longestCallbackTicks))
            Interlocked.Exchange(ref _longestCallbackTicks, spent);
        return swallow ? 1 : CallNextHookEx(0, code, message, data);
    }

    private bool Begin(Point point, bool resizing)
    {
        if (!ModifierHeld())
            return false;
        nint window = User32.GetAncestor(User32.WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), User32.GA_ROOT);
        if (window == 0 || !_eligible(window) || !User32.GetWindowRect(window, out Rect r))
            return false;

        var original = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        Interlocked.Exchange(ref _point, Pack(point.X, point.Y));
        _pendingRelease = resizing ? WM_RBUTTONUP : WM_LBUTTONUP;
        _gesture = new Gesture(window, resizing, point, original,
            resizing ? PlacementGeometry.EdgesAt(point, original) : ResizeEdges.None, _pendingRelease);
        _swallowRelease = true;
        PostThreadMessage(_hookThreadId, ClaimEscape, 0, 0);
        _moved.Set();
        return true;
    }

    /// <summary>The press went to WinScreen, so its release must too, or the app sees a release it never pressed.</summary>
    private bool End(int message)
    {
        if (!_swallowRelease || message != _pendingRelease)
            return false;
        _swallowRelease = false;
        _gesture = null;
        PostThreadMessage(_hookThreadId, ReleaseEscape, 0, 0);
        MaskModifier();
        return true;
    }

    /// <summary>Esc puts the window back. The button is still down, so its release is still WinScreen's to swallow.</summary>
    private void Cancel()
    {
        Gesture? gesture = _gesture;
        if (gesture is null)
            return;
        _gesture = null;
        Rectangle r = gesture.Original;
        User32.SetWindowPos(gesture.Window, 0, r.X, r.Y, r.Width, r.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS);
    }

    private void MaskModifier()
    {
        if (!ModifierHeld())
            return;
        keybd_event(MaskKey, 0, 0, 0);
        keybd_event(MaskKey, 0, KeyUp, 0);
    }

    /// <summary>Follows the pointer off the hook's thread. Asynchronous moves mean a hung app can't stall the mouse.</summary>
    private void MoveWindows()
    {
        Gesture? noted = null;
        while (!_stopping)
        {
            _moved.WaitOne();
            if (_stopping || _gesture is not { } gesture)
            {
                noted = null;
                continue;
            }

            if (!ReferenceEquals(noted, gesture))
            {
                noted = gesture;
                _began(gesture.Window);
            }

            long packed = Interlocked.Read(ref _point);
            var moved = new Size((int)(packed >> 32) - gesture.Start.X, (int)packed - gesture.Start.Y);
            const uint flags = User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS;
            Rectangle r = gesture.Resizing
                ? PlacementGeometry.Resized(gesture.Original, gesture.Edges, moved, Smallest)
                : new Rectangle(gesture.Original.Location + moved, gesture.Original.Size);
            User32.SetWindowPos(gesture.Window, 0, r.X, r.Y, r.Width, r.Height, gesture.Resizing ? flags : flags | User32.SWP_NOSIZE);
        }
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    private delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public int Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int kind, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg message, nint window, uint first, uint last);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(nint name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
