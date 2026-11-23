using System.Diagnostics;
using SharpGen.Runtime;

namespace WinScreen.Pip;

// File: the finished video, null when nothing was recorded. Problem: the error code when writing failed.
internal sealed record RecordingResult(string? File, long Frames, bool Hardware, bool Sound, bool SoundRefused, int Problem = 0, int Rate = 0);

// A view's picture as a video. The capture's worker hands in finished frames without ever waiting; the recording's own
// thread keeps a steady rate, writing the newest frame each tick or the last one again, and is the only one to touch
// the file. Finish may come from any thread, any number of times.
internal sealed class ViewRecording
{
    private static readonly List<ViewRecording> Unfinished = [];
    private const int Buffers = 3;

    private readonly string _path;
    private readonly uint _soundProcess;
    private readonly bool _softwareOnly;
    private readonly object _gate = new();
    // High resolution: at 144 a second a tick is 7 ms, less than Windows' ordinary 15.6 ms wait.
    private readonly PipWorkerSignal _signal = new();
    private readonly Stack<byte[]> _free = new();
    private readonly TaskCompletionSource<RecordingResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _allocated;
    private byte[]? _newest;
    private Size _size;
    private int _rate;
    private bool _finished;
    private volatile bool _muted;

    // The view's Mute: while it is on, the program's sound is recorded as silence.
    internal bool Muted { get => _muted; set => _muted = value; }

    // softwareOnly: Windows' own encoder even where a hardware one has proved itself (tests try both).
    internal ViewRecording(string path, uint soundProcess = 0, bool softwareOnly = false)
    {
        _path = path;
        _soundProcess = soundProcess;
        _softwareOnly = softwareOnly;
        lock (Unfinished) Unfinished.Add(this);
        if (!softwareOnly) VideoFileWriter.CheckHardware();
        var thread = new Thread(Run) { IsBackground = true, Name = "WinScreen recording" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal static bool Supported => VideoFileWriter.Supported;
    internal string Path => _path;
    internal Task<RecordingResult> Completion => _done.Task;
    internal bool Finished { get { lock (_gate) return _finished; } }
    // Frames a second: the view's capture rate when the first frame came, kept for the whole file.
    internal int Rate { get { lock (_gate) return _rate == 0 ? RecordingGeometry.DefaultRate : _rate; } }
    internal long Period => Stopwatch.Frequency / Rate;

    // Fixed by the first frame offered, with the view's rate then; every later frame is drawn into this size.
    internal Size FrameSize(Size shown, int viewRate)
    {
        lock (_gate)
        {
            if (_size.IsEmpty)
            {
                _size = RecordingGeometry.FrameSize(shown);
                _rate = RecordingGeometry.RateFor(viewRate, _size);
            }
            return _size;
        }
    }

    // A buffer for the next frame, top row first, 4 bytes a pixel; null when all are busy (that frame is skipped).
    internal byte[]? Rent()
    {
        lock (_gate)
        {
            if (_finished || _size.IsEmpty) return null;
            if (_free.TryPop(out byte[]? free)) return free;
            if (_allocated == Buffers) return null;
            _allocated++;
            return new byte[_size.Width * 4 * _size.Height];
        }
    }

    internal void Deliver(byte[] frame)
    {
        lock (_gate)
        {
            if (_finished) { _free.Push(frame); return; }
            if (_newest is not null) _free.Push(_newest);
            _newest = frame;
        }
        _signal.Set();
    }

    internal void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
        }
        _signal.Set();
    }

    // At exit: the views have closed, so every recording is finishing; their files are completed before WinScreen goes.
    internal static bool FinishAll(TimeSpan limit)
    {
        ViewRecording[] open;
        lock (Unfinished) open = [.. Unfinished];
        foreach (ViewRecording recording in open) recording.Finish();
        return Task.WaitAll([.. open.Select(recording => recording.Completion)], limit);
    }

    private byte[]? TakeNewest()
    {
        lock (_gate)
        {
            byte[]? newest = _newest;
            _newest = null;
            return newest;
        }
    }

    private void Return(byte[]? frame)
    {
        if (frame is null) return;
        lock (_gate) _free.Push(frame);
    }

    private void Run()
    {
        AppSoundCapture? sound = _soundProcess == 0 ? null : new AppSoundCapture(_soundProcess, () => _muted);
        VideoFileWriter? writer = null;
        long frames = 0;
        RecordingResult? result = null;
        try
        {
            byte[]? current;
            while ((current = TakeNewest()) is null)
            {
                if (Finished) { result = new(null, 0, false, false, false); return; }
                _signal.Wait(250);
            }
            bool soundRefused = sound is not null && !sound.WaitUntilReady(TimeSpan.FromSeconds(3));
            if (soundRefused) { sound!.Dispose(); sound = null; }
            Size size;
            int wanted;
            lock (_gate) { size = _size; wanted = _rate == 0 ? RecordingGeometry.DefaultRate : _rate; }
            writer = VideoFileWriter.Open(_path, size, wanted, sound is not null, _softwareOnly, current);
            int rate = writer.Rate;
            lock (_gate) _rate = rate;
            // The clock starts with the file, so waiting for the sound or the encoder never leaves a gap.
            long start = Stopwatch.GetTimestamp();
            Diagnostics.Log.Event("recording.started", ("hardware", writer.Hardware), ("sound", sound is not null),
                ("width", size.Width), ("height", size.Height), ("rate", rate));
            long tick = 1;
            frames = 1;
            var timeline = new SoundTimeline(writer);
            while (true)
            {
                long due = start + tick * Stopwatch.Frequency / rate;
                long wait = due - Stopwatch.GetTimestamp();
                if (wait > 0 && !Finished)
                {
                    _signal.Wait(TimeSpan.FromSeconds(Math.Min(wait, Stopwatch.Frequency / 10) / (double)Stopwatch.Frequency));
                    continue;
                }
                if (Finished) break;
                if (TakeNewest() is { } newest)
                {
                    Return(current);
                    current = newest;
                    writer.Frame(current);
                }
                writer.Write(tick);
                frames++;
                double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                tick = RecordingGeometry.NextTick(tick + 1, elapsed, rate);
                // Sound is written a little behind the pictures, so packets still on their way fit where they belong.
                if (sound is not null) timeline.Write(sound, start, (long)((elapsed - 0.25) * VideoFileWriter.SoundRate));
            }
            // The newest picture ends the video, at the time it was stopped: an encoder that fell behind (a slow processor at
            // a high rate) leaves fewer frames, never a shorter video.
            long stopped = (long)Math.Floor(Stopwatch.GetElapsedTime(start).TotalSeconds * rate);
            if (TakeNewest() is { } last)
            {
                Return(current);
                current = last;
                writer.Frame(current);
            }
            writer.Write(tick = Math.Max(tick, stopped));
            tick++;
            frames++;
            Return(current);
            if (sound is not null) timeline.Write(sound, start, tick * VideoFileWriter.SoundRate / rate);
            writer.Finish();
            result = new(_path, frames, writer.Hardware, sound is not null, soundRefused, Rate: rate);
            Diagnostics.Log.Event("recording.saved", ("frames", frames), ("hardware", writer.Hardware), ("sound", sound is not null));
        }
        // Its own thread: anything uncaught here would end WinScreen.
        catch (Exception failure)
        {
            Diagnostics.Log.Error("recording.failed", failure);
            result = new(null, frames, writer?.Hardware == true, false, false, failure.HResult == 0 ? -1 : failure.HResult);
        }
        finally
        {
            sound?.Dispose();
            writer?.Dispose();
            Finish();
            if (result?.File is null)
                try { if (File.Exists(_path)) File.Delete(_path); }
                catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(gone); }
            lock (Unfinished) Unfinished.Remove(this);
            _done.TrySetResult(result ?? new(null, frames, false, false, false, -1));
        }
    }

    // Sound on the pictures' clock: each packet where it was heard, silence wherever the program played nothing (Windows
    // sends nothing then), and nothing written twice.
    private sealed class SoundTimeline(VideoFileWriter writer)
    {
        private long _written;
        private static readonly byte[] Quiet = new byte[VideoFileWriter.SoundRate / 10 * VideoFileWriter.SoundFrameBytes];

        internal void Write(AppSoundCapture sound, long start, long until)
        {
            while (sound.Packets.TryDequeue(out AppSoundCapture.Packet packet))
            {
                long at = (packet.At - start) * VideoFileWriter.SoundRate / Stopwatch.Frequency;
                long count = packet.Pcm.Length / VideoFileWriter.SoundFrameBytes;
                if (at + count <= _written) continue;
                int skip = 0;
                if (at < _written) skip = (int)(_written - at);
                else if (at > _written + VideoFileWriter.SoundRate / 50) Silence(at);
                writer.Sound(packet.Pcm.AsSpan(skip * VideoFileWriter.SoundFrameBytes), _written);
                _written += count - skip;
            }
            Silence(until);
        }

        private void Silence(long until)
        {
            while (_written < until)
            {
                int count = (int)Math.Min(until - _written, Quiet.Length / VideoFileWriter.SoundFrameBytes);
                writer.Sound(Quiet.AsSpan(0, count * VideoFileWriter.SoundFrameBytes), _written);
                _written += count;
            }
        }
    }
}
