using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinScreen.Native;

namespace WinScreen.Tray;

/// <summary>
/// What the ordinary copy and its administrator helper say to each other (10-2): one line of JSON per message, over a
/// named pipe the ordinary copy serves. Each end asks Windows who is at the other end and checks it: the same program
/// file, the helper elevated and the ordinary copy not, and the helper the one copy it found. No window message is
/// opened to lower rights, and anything from anyone else is never read.
/// </summary>
internal static class HelperLink
{
    /// <summary>The ordinary copy asks the helper to run a command on an administrator app's window (0: for every window).</summary>
    public const string Run = "run";
    /// <summary>The ordinary copy saved Settings: the helper reads them again.</summary>
    public const string SettingsSaved = "settings";
    /// <summary>Run as administrator was turned off: the helper puts its windows back and goes.</summary>
    public const string Exit = "exit";
    /// <summary>The helper has something to say; the ordinary copy shows it as its own.</summary>
    public const string Alert = "alert";
    /// <summary>The helper was asked for Settings (from its toolbar or window menu); the ordinary copy opens them.</summary>
    public const string OpenSettings = "openSettings";
    /// <summary>The helper changed settings (a rule, a view's preset): the ordinary copy, their one writer, saves them.</summary>
    public const string Save = "save";

    internal sealed record Message(string Kind, int Command = 0, long Window = 0, int[]? Display = null, string? Title = null,
        string? Text = null, int Icon = 0)
    {
        internal Rectangle? DisplayBounds => Display is [var x, var y, var width, var height] ? new Rectangle(x, y, width, height) : null;

        internal static int[]? From(Rectangle? display) => display is { } d ? [d.X, d.Y, d.Width, d.Height] : null;
    }

    internal static string PipeName(int ordinaryProcess) => "WinScreen.Ordinary." + ordinaryProcess;

    internal static string Encode(Message message) => JsonSerializer.Serialize(message);

    /// <summary>A message, or null for a line that isn't one (or one too long to be one).</summary>
    internal static Message? Decode(string? line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaximumLine) return null;
        try { return JsonSerializer.Deserialize<Message>(line) is { Kind.Length: > 0 } message ? message : null; }
        catch (JsonException) { return null; }
    }

    private const int MaximumLine = 2 * 1024 * 1024; // Settings travel whole; their file is at most 1 MB.

    /// <summary>Whether a process is a copy of this very program file, elevated or not as asked.</summary>
    internal static bool IsCopyOfThis(uint process, bool elevated) =>
        process != 0 && process != Environment.ProcessId &&
        string.Equals(ProcessInfo.PathOf(process), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        ProcessInfo.IsElevated(process) == elevated;

    /// <summary>The running copy whose hidden window has this title, if it is a copy of this program (elevated or not, as asked).</summary>
    internal static uint FindCopy(string hostTitle, bool elevated)
    {
        nint window = User32.FindWindow(null, hostTitle);
        if (window == 0) return 0;
        User32.GetWindowThreadProcessId(window, out uint process);
        return IsCopyOfThis(process, elevated) ? process : 0;
    }

    /// <summary>
    /// One end of the link. Messages arrive on its own thread (<see cref="Received"/>); <see cref="Send"/> may be called
    /// from any thread. <see cref="Connected"/> changes are reported on its thread too.
    /// </summary>
    internal abstract class End : IDisposable
    {
        private readonly object _gate = new();
        private StreamWriter? _writer;
        private volatile bool _stopped;
        protected readonly CancellationTokenSource Stopping = new();

        internal event Action<Message>? Received;
        internal event Action<bool>? ConnectionChanged;

        public bool Connected { get; private set; }

        /// <summary>The process at the other end while connected, else 0.</summary>
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

        /// <summary>Reads messages until the other end goes; then says so.</summary>
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

    /// <summary>
    /// The ordinary copy's end: serves the pipe and takes only its own helper (a copy of this program file, elevated),
    /// one at a time, for as long as this copy runs.
    /// </summary>
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

    /// <summary>
    /// The helper's end: finds the ordinary copy (its hidden window, a copy of this program that isn't elevated), connects
    /// to the pipe it serves and checks the server is that very process. <paramref name="gone"/> is called once, when the
    /// ordinary copy goes or never comes.
    /// </summary>
    internal sealed class HelperEnd(Action gone) : End
    {
        private NamedPipeClientStream? _pipe;

        /// <summary>Looks for the ordinary copy for up to <paramref name="wait"/>, then talks to it until it goes.</summary>
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

        /// <summary>True once it talked to the ordinary copy (and it has gone again).</summary>
        private bool TryTalk(uint ordinary)
        {
            try
            {
                // Not CurrentUserOnly here: .NET checks the pipe's owner against the token's owner, which for an elevated
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
