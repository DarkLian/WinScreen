using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinScreen.Native;
using static WinScreen.Native.ProcessLoopback;

namespace WinScreen.Pip;

// One program's own sound for a recording, as Windows mixes it, 16-bit stereo at 48 kHz whatever the program plays.
// Its own thread takes each packet as it comes; the recording's thread places them on its clock.
internal sealed class AppSoundCapture : IDisposable
{
    internal readonly record struct Packet(byte[] Pcm, long At);

    private readonly uint _process;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _stopping;
    private readonly Func<bool> _muted;
    internal ConcurrentQueue<Packet> Packets { get; } = new();
    internal bool Capturing { get; private set; }

    // muted: asked as each packet comes; a muted one is kept as silence, so the time it took stays.
    internal AppSoundCapture(uint process, Func<bool>? muted = null)
    {
        _process = process;
        _muted = muted ?? (static () => false);
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen recording sound" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // True once sound is coming; false when Windows refused or didn't answer in time.
    internal bool WaitUntilReady(TimeSpan limit) => _ready.Wait(limit) && Capturing;

    // The program whose sound a window plays: a Store app's own process, not the frame around it.
    internal static uint ProcessOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        if (!string.Equals(Windows.WindowInfo.ProgramOf(window), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return process;
        foreach (nint child in ChildWindows.Of(window))
        {
            User32.GetWindowThreadProcessId(child, out uint owner);
            if (owner != 0 && owner != process) return owner;
        }
        return process;
    }

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var arrived = new AutoResetEvent(false);
        try
        {
            var format = new WaveFormat
            {
                Tag = 1, Channels = VideoFileWriter.SoundChannels, SamplesPerSecond = VideoFileWriter.SoundRate,
                AverageBytesPerSecond = VideoFileWriter.SoundRate * VideoFileWriter.SoundFrameBytes,
                BlockAlign = VideoFileWriter.SoundFrameBytes, BitsPerSample = 16,
            };
            client = Activate(_process, TimeSpan.FromSeconds(3));
            int started = client.Initialize(0, Loopback | EventCallback | AutoConvertPcm | DefaultQuality, 200_000, 0, in format, 0);
            if (started < 0)
            {
                // Windows' own example passes the conversion flag where the period goes.
                Marshal.ReleaseComObject(client);
                client = Activate(_process, TimeSpan.FromSeconds(3));
                started = client.Initialize(0, Loopback | EventCallback, 200_000, AutoConvertPcm, in format, 0);
            }
            Marshal.ThrowExceptionForHR(started);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(arrived.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.GetService(typeof(IAudioCaptureClient).GUID, out object service));
            capture = (IAudioCaptureClient)service;
            Marshal.ThrowExceptionForHR(client.Start());
            Capturing = true;
            _ready.Set();
            while (!_stopping)
            {
                arrived.WaitOne(100);
                while (!_stopping && capture.GetNextPacketSize(out uint waiting) >= 0 && waiting > 0)
                {
                    long received = Stopwatch.GetTimestamp();
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                    try
                    {
                        var pcm = new byte[frames * VideoFileWriter.SoundFrameBytes];
                        if ((flags & Silent) == 0 && data != 0 && !_muted()) Marshal.Copy(data, pcm, 0, pcm.Length);
                        // Where its first sample was heard, on the pictures' clock.
                        Packets.Enqueue(new Packet(pcm, received - frames * Stopwatch.Frequency / VideoFileWriter.SoundRate));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
            client.Stop();
        }
        // Its own thread: anything uncaught here would end WinScreen. The recording goes on without sound.
        catch (Exception failure)
        {
            Diagnostics.Log.Event("recording.soundRefused", ("code", Diagnostics.LogValue.Code(failure.HResult)));
        }
        finally
        {
            Capturing = false;
            _ready.Set();
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    public void Dispose() => _stopping = true;
}
