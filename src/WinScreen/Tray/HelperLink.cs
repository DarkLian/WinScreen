using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinScreen.Native;

namespace WinScreen.Tray;

// Each end asks Windows who is at the other end and checks it: the same program file, the helper elevated and the
// ordinary copy not, and the helper the one copy it found.
// No window message is opened to lower rights, and nothing from anyone else is ever read.
internal static class HelperLink
{
    public const string Run = "run";
    public const string SettingsSaved = "settings";
    public const string Exit = "exit";
    public const string Alert = "alert";
    public const string OpenSettings = "openSettings";
    // The ordinary copy is the settings' one writer.
    public const string Save = "save";
    // The helper's own windows and views, which the ordinary copy's tray and keys reach through it.
    public const string State = "state";
    public const string ViewAction = "view";
    // The administrator app's window the ordinary copy's tray shows: the helper's state then says what is on for it.
    public const string Watch = "watch";

    internal sealed record HelperView(long Handle, long Source, string Title, bool Suspended, bool SourceIsWindow,
        bool ClickThrough, bool CanRetry, long PointedAt, long SuspendedAt);

    internal sealed record Message(string Kind, int Command = 0, long Window = 0, int[]? Display = null, string? Title = null,
        string? Text = null, int Icon = 0, int[]? Counts = null, HelperView[]? Views = null, int[]? On = null)
    {
        internal Rectangle? DisplayBounds => Display is [var x, var y, var width, var height] ? new Rectangle(x, y, width, height) : null;

        internal static int[]? From(Rectangle? display) => display is { } d ? [d.X, d.Y, d.Width, d.Height] : null;
    }

    internal static string PipeName(int ordinaryProcess) => "WinScreen.Ordinary." + ordinaryProcess;

    internal static string Encode(Message message) => JsonSerializer.Serialize(message);

    internal static Message? Decode(string? line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaximumLine) return null;
        try { return JsonSerializer.Deserialize<Message>(line) is { Kind.Length: > 0 } message ? message : null; }
        catch (JsonException) { return null; }
    }

    private const int MaximumLine = 2 * 1024 * 1024; // Settings travel whole; their file is at most 1 MB.

    internal static bool IsCopyOfThis(uint process, bool elevated) =>
        process != 0 && process != Environment.ProcessId &&
        string.Equals(ProcessInfo.PathOf(process), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        ProcessInfo.IsElevated(process) == elevated;

    internal static uint FindCopy(string hostTitle, bool elevated)
    {
        nint window = User32.FindWindow(null, hostTitle);
        if (window == 0) return 0;
        User32.GetWindowThreadProcessId(window, out uint process);
        return IsCopyOfThis(process, elevated) ? process : 0;
    }

    // Messages and Connected changes arrive on its own thread; Send may be called from any thread.
    internal abstract class End : IDisposable
    {
        private readonly object _gate = new();
        private StreamWriter? _writer;
        private volatile bool _stopped;
        protected readonly CancellationTokenSource Stopping = new();

        internal event Action<Message>? Received;
        internal event Action<bool>? ConnectionChanged;

        public bool Connected { get; private set; }

        public uint Peer { get; private set; }

        protected bool Stopped => _stopped;

        internal bool Send(Message message)
        {
            lock (_gate)
            {
                if (_writer is null) return false;
                try
                {
                    _writer.WriteLine(Encode(message));
                    _writer.Flush();
                    return true;
                }
                catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Diagnostics.Log.Swallowed(failure);
                    return false;
                }
            }
        }

        protected void Talk(PipeStream pipe, uint peer)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            lock (_gate) _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true);
            Peer = peer;
            Connected = true;
            ConnectionChanged?.Invoke(true);
            try
            {
                while (!_stopped && reader.ReadLine() is { } line)
                    if (Decode(line) is { } message) Received?.Invoke(message);
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Diagnostics.Log.Swallowed(failure);
            }
            finally
            {
                lock (_gate)
                {
                    try { _writer?.Dispose(); }
                    catch (Exception failure) when (failure is IOException or ObjectDisposedException) { Diagnostics.Log.Swallowed(failure); }
                    _writer = null;
                }
                reader.Dispose();
                Connected = false;
                Peer = 0;
                ConnectionChanged?.Invoke(false);
            }
        }

        public virtual void Dispose()
        {
            _stopped = true;
            Stopping.Cancel();
        }
    }

    // Takes only its own helper (a copy of this program file, elevated), one at a time.
    internal sealed class OrdinaryEnd : End
    {
        private NamedPipeServerStream? _pipe;

        internal void Start()
        {
            var thread = new Thread(Serve) { IsBackground = true, Name = "WinScreen helper link" };
            thread.Start();
        }

        private void Serve()
        {
            while (!Stopped)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    _pipe = pipe;
                    pipe.WaitForConnectionAsync(Stopping.Token).GetAwaiter().GetResult();
                    uint client = PipePeers.Client(pipe.SafePipeHandle);
                    if (!IsCopyOfThis(client, elevated: true))
                    {
                        Diagnostics.Log.Event("helper.refused", ("process", (int)client));
                        continue;
                    }
                    Diagnostics.Log.Event("helper.connected", ("process", (int)client));
                    Talk(pipe, client);
                    Diagnostics.Log.Event("helper.gone");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    Diagnostics.Log.Swallowed(failure);
                    if (Stopping.Token.WaitHandle.WaitOne(1000)) return;
                }
                finally { _pipe = null; }
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            try { _pipe?.Dispose(); }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException) { Diagnostics.Log.Swallowed(failure); }
        }
    }

    // Checks the server is that very process. gone is called once, when the ordinary copy goes or never comes.
    internal sealed class HelperEnd(Action gone) : End
    {
        private NamedPipeClientStream? _pipe;

        internal void Start(TimeSpan wait)
        {
            var thread = new Thread(() => Connect(wait)) { IsBackground = true, Name = "WinScreen helper link" };
            thread.Start();
        }

        private void Connect(TimeSpan wait)
        {
            long until = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            try
            {
                while (!Stopped && Environment.TickCount64 < until)
                {
                    uint ordinary = FindCopy(Hotkeys.HotkeyHost.WindowTitle, elevated: false);
                    if (ordinary != 0 && TryTalk(ordinary)) return;
                    if (Stopping.Token.WaitHandle.WaitOne(250)) return;
                }
                Diagnostics.Log.Event("helper.noOrdinaryCopy");
            }
            finally
            {
                if (!Stopped) gone();
            }
        }

        private bool TryTalk(uint ordinary)
        {
            try
            {
                // Not CurrentUserOnly here: .NET checks the pipe's owner against the token's owner, which for an
                // elevated
                // token is the Administrators group, so it refused the real ordinary copy. The server's process is
                // checked below instead, which is stricter: that very copy, not just this person.
                using var pipe = new NamedPipeClientStream(".", PipeName((int)ordinary), PipeDirection.InOut, PipeOptions.Asynchronous);
                _pipe = pipe;
                pipe.ConnectAsync(2000, Stopping.Token).GetAwaiter().GetResult();
                if (PipePeers.Server(pipe.SafePipeHandle) != ordinary)
                {
                    Diagnostics.Log.Event("helper.wrongServer");
                    return false;
                }
                Talk(pipe, ordinary);
                return true;
            }
            catch (Exception failure) when (failure is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Diagnostics.Log.Swallowed(failure);
                return false;
            }
            catch (OperationCanceledException) { return true; }
            finally { _pipe = null; }
        }

        public override void Dispose()
        {
            base.Dispose();
            try { _pipe?.Dispose(); }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException) { Diagnostics.Log.Swallowed(failure); }
        }
    }
}
