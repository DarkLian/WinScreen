using System.Runtime.InteropServices;

namespace WinScreen.Native;

// When Alt last went down or up, from raw input, which comes whatever is in front. Polling Alt misses a quick Alt+Tab
// that falls between two looks, and the "pressed since" bit of GetAsyncKeyState is shared with every other caller.
// A process has one keyboard registration for raw input, and a second takes it from the first: everything else that
// needs the keys (the shortcut sheet) listens here.
internal static class AltKeyWatch
{
    // Each key going down (true) or up, on this watch's own thread.
    internal static event Action<int, bool>? Key;

    private const int WM_INPUT = 0x00FF, VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100, RIM_TYPEKEYBOARD = 1;
    private static readonly object Gate = new();
    private static bool s_started;
    private static long s_lastAlt = long.MinValue / 2;

    internal static long LastAltAt
    {
        get
        {
            Start();
            return Interlocked.Read(ref s_lastAlt);
        }
    }

    internal static void Start()
    {
        if (Volatile.Read(ref s_started)) return;
        lock (Gate)
        {
            if (s_started) return;
            s_started = true;
        }
        var thread = new Thread(() =>
        {
            using var window = new Sink();
            if (!window.Listening) return;
            Application.Run();
        }) { IsBackground = true, Name = "Alt key watch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private sealed class Sink : NativeWindow, IDisposable
    {
        public Sink()
        {
            CreateHandle(new CreateParams { Parent = -3 }); // HWND_MESSAGE
            var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = Handle };
            Listening = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }

        public bool Listening { get; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) Read(m.LParam);
            base.WndProc(ref m);
        }

        private static unsafe void Read(nint input)
        {
            uint header = (uint)Marshal.SizeOf<RawInputHeader>();
            uint size = 0;
            // A keyboard's input is a few dozen bytes: on the stack, not an allocation per key system-wide.
            if (GetRawInputData(input, RID_INPUT, 0, ref size, header) != 0 || size < header + 16 || size > 256) return;
            byte* bytes = stackalloc byte[(int)size];
            nint buffer = (nint)bytes;
            if (GetRawInputData(input, RID_INPUT, buffer, ref size, header) != size) return;
            if (((RawInputHeader*)bytes)->Type != RIM_TYPEKEYBOARD) return; // Read in place: PtrToStructure boxes.
            ushort key = (ushort)Marshal.ReadInt16(buffer, (int)header + 6); // RAWKEYBOARD.VKey
            ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)header + 2); // RAWKEYBOARD.Flags: 1 is a key going up
            if (key is VK_MENU or VK_LMENU or VK_RMENU) Interlocked.Exchange(ref s_lastAlt, Environment.TickCount64);
            Key?.Invoke(key, (flags & 1) == 0);
        }

        public void Dispose() => DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
}
