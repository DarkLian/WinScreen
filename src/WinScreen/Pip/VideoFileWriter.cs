using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static Vortice.Direct3D11.D3D11;

namespace WinScreen.Pip;

// An MP4 file of H.264 frames (BGRA in, top row first) and, if asked for, AAC sound (16-bit stereo at 48 kHz in).
// Used by one thread only, which may wait on it: never the capture's worker.
internal sealed class VideoFileWriter : IDisposable
{
    internal const int SoundRate = 48000, SoundChannels = 2, SoundFrameBytes = 4;
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup().Success);
    private static readonly Lazy<bool> CanEncode = new(() => Encoders(hardware: false) > 0);
    private static readonly Lazy<bool> HasHardware = new(() => Encoders(hardware: true) > 0);

    private readonly IMFSinkWriter _writer;
    private readonly int _video, _sound = -1;
    private readonly Size _size;
    private readonly long _frameDuration;
    private readonly ID3D11Device? _device;
    private readonly IMFDXGIDeviceManager? _manager;
    private IMFMediaBuffer? _frame;
    private bool _finished;
    internal bool Hardware { get; }
    internal bool HasSound => _sound >= 0;

    // Windows N editions come without Windows' video encoders.
    internal static bool Supported => Started.Value && CanEncode.Value;

    // A hardware encoder is used once it has finished a file of its own. One (AMD Radeon RX 9060 XT, driver
    // 32.0.32029.2010) took every frame and never finished: the file stayed empty and Finalize never returned, with or
    // without a device manager. So it is tried once per run on a scratch file, off to the side, and Windows' own
    // encoder does the work until it passes, or for good if it never does.
    private static Task<bool>? _hardwareCheck;
    private const int HardwareCheckMilliseconds = 5000;

    internal static bool HardwareProven => _hardwareCheck is { IsCompletedSuccessfully: true, Result: true };

    internal static Task<bool> HardwareCheck => _hardwareCheck ?? Task.FromResult(false);

    internal static void CheckHardware()
    {
        lock (H264Gate)
        {
            if (_hardwareCheck is not null) return;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hardwareCheck = answer.Task;
            if (!Started.Value || !HasHardware.Value) { answer.SetResult(false); return; }
            // One an earlier run's encoder never let go of goes now.
            foreach (string old in Directory.EnumerateFiles(System.IO.Path.GetTempPath(), "WinScreen encoder check *.mp4"))
                try { File.Delete(old); }
                catch (Exception held) when (held is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(held); }
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"WinScreen encoder check {Environment.ProcessId}.mp4");
            var check = new Thread(() =>
            {
                bool finished = false;
                try
                {
                    var size = new Size(640, 360);
                    using var writer = new VideoFileWriter(path, size, RecordingGeometry.Rate, sound: false, hardware: true);
                    writer.Frame(new byte[size.Width * size.Height * 4]);
                    for (int tick = 0; tick < 15; tick++) writer.Write(tick);
                    writer.Finish();
                    finished = new FileInfo(path).Length > 0;
                }
                // Its own thread: anything uncaught here would end WinScreen.
                catch (Exception failure)
                {
                    Diagnostics.Log.Swallowed(failure);
                }
                finally
                {
                    try { File.Delete(path); }
                    catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(gone); }
                }
                if (answer.TrySetResult(finished)) Diagnostics.Log.Event("recording.hardwareCheck", ("finished", finished));
            }) { IsBackground = true, Name = "WinScreen encoder check" };
            check.SetApartmentState(ApartmentState.MTA);
            check.Start();
            // An encoder that never finishes leaves its thread waiting; it is a background thread, and only this one.
            _ = Task.Delay(HardwareCheckMilliseconds).ContinueWith(_ =>
            {
                if (answer.TrySetResult(false)) Diagnostics.Log.Event("recording.hardwareCheck", ("finished", false));
            }, TaskScheduler.Default);
        }
    }

    private static readonly object H264Gate = new();

    private VideoFileWriter(string path, Size size, int rate, bool sound, bool hardware)
    {
        if (!Started.Value) throw new InvalidOperationException("Media Foundation didn't start.");
        _size = size;
        _frameDuration = 10_000_000L / rate;
        Hardware = hardware;
        try
        {
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(3);
            if (hardware)
            {
                D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                    out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
                context?.Dispose();
                _device = device!;
                // Media Foundation's own threads use the device too.
                using (ID3D11Multithread guard = _device.QueryInterface<ID3D11Multithread>())
                    guard.SetMultithreadProtected(true);
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(_device);
                attributes.Set(SinkWriterAttributeKeys.D3DManager, _manager);
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            }
            _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

            using (IMFMediaType output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)RecordingGeometry.Bitrate(size, rate));
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // High
                Describe(output, size, rate);
                _video = _writer.AddStream(output);
            }
            using (IMFMediaType input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                Describe(input, size, rate);
                // Without it, Media Foundation takes RGB as bottom row first.
                input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(size.Width * 4));
                input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
                _writer.SetInputMediaType(_video, input, null);
            }
            if (sound)
            {
                using (IMFMediaType output = MediaFactory.MFCreateMediaType())
                {
                    output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    DescribeSound(output);
                    // The AAC encoder takes only 12000, 16000, 20000 or 24000 bytes a second.
                    output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u);
                    _sound = _writer.AddStream(output);
                }
                using IMFMediaType input = MediaFactory.MFCreateMediaType();
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                DescribeSound(input);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)SoundFrameBytes);
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(SoundRate * SoundFrameBytes));
                _writer.SetInputMediaType(_sound, input, null);
            }
            _writer.BeginWriting();
        }
        catch
        {
            Release();
            throw;
        }
    }

    private static void Describe(IMFMediaType type, Size size, int rate)
    {
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)size.Width << 32) | (uint)size.Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)rate << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
    }

    private static void DescribeSound(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SoundRate);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)SoundChannels);
    }

    // Hardware first where there is a hardware encoder; Windows' own encoder otherwise, or if the hardware one won't
    // take these frames. The first frame is written here, so a writer that would fail on it isn't kept.
    internal static VideoFileWriter Open(string path, Size size, int rate, bool sound, bool softwareOnly, byte[] first)
    {
        if (!softwareOnly && HardwareProven)
        {
            VideoFileWriter? hardware = null;
            try
            {
                hardware = new VideoFileWriter(path, size, rate, sound, hardware: true);
                hardware.Frame(first);
                hardware.Write(0);
                return hardware;
            }
            catch (Exception failure) when (failure is SharpGenException or COMException or InvalidOperationException)
            {
                Diagnostics.Log.Error("recording.hardwareRefused", failure);
                hardware?.Dispose();
                try { File.Delete(path); } catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { Diagnostics.Log.Swallowed(gone); }
            }
        }
        var software = new VideoFileWriter(path, size, rate, sound, hardware: false);
        try
        {
            software.Frame(first);
            software.Write(0);
            return software;
        }
        catch
        {
            software.Dispose();
            throw;
        }
    }

    // The picture written by the next Write calls, until another comes. A buffer already handed to the encoder is
    // never changed: each new picture gets its own.
    internal unsafe void Frame(byte[] pixels)
    {
        int stride = _size.Width * 4, length = stride * _size.Height;
        if (pixels.Length < length) throw new ArgumentException("Frame too small.", nameof(pixels));
        IMFMediaBuffer buffer;
        if (_device is not null)
        {
            fixed (byte* data = pixels)
            {
                using ID3D11Texture2D texture = _device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    (uint)_size.Width, (uint)_size.Height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget),
                    [new SubresourceData((nint)data, (uint)stride)]);
                buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
            }
            using (IMF2DBuffer flat = buffer.QueryInterface<IMF2DBuffer>())
                buffer.CurrentLength = flat.ContiguousLength;
        }
        else
        {
            buffer = MediaFactory.MFCreateMemoryBuffer(length);
            buffer.Lock(out nint target, out _, out _);
            try
            {
                fixed (byte* data = pixels) Buffer.MemoryCopy(data, (void*)target, length, length);
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = length;
        }
        _frame?.Dispose();
        _frame = buffer;
    }

    internal void Write(long tick)
    {
        if (_frame is null) return;
        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(_frame);
        sample.SampleTime = tick * _frameDuration;
        sample.SampleDuration = _frameDuration;
        _writer.WriteSample(_video, sample);
    }

    // pcm is whole sample frames; at is where its first falls, in sample frames from the start.
    internal unsafe void Sound(ReadOnlySpan<byte> pcm, long at)
    {
        if (_sound < 0 || pcm.Length < SoundFrameBytes) return;
        int length = pcm.Length / SoundFrameBytes * SoundFrameBytes;
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(length);
        try
        {
            buffer.Lock(out nint target, out _, out _);
            try { pcm[..length].CopyTo(new Span<byte>((void*)target, length)); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = length;
            using IMFSample sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = at * 10_000_000L / SoundRate;
            sample.SampleDuration = length / SoundFrameBytes * 10_000_000L / SoundRate;
            _writer.WriteSample(_sound, sample);
        }
        finally { buffer.Dispose(); }
    }

    // Completes the file's index; without it the file doesn't open.
    internal void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.Finalize();
    }

    private void Release()
    {
        _frame?.Dispose();
        _frame = null;
        _writer?.Dispose();
        _manager?.Dispose();
        _device?.Dispose();
    }

    public void Dispose() => Release();

    private static int Encoders(bool hardware)
    {
        if (!Started.Value) return 0;
        const uint SyncMft = 0x1, AsyncMft = 0x2, HardwareMft = 0x4, SortAndFilter = 0x40;
        try
        {
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                (hardware ? HardwareMft | AsyncMft : SyncMft | AsyncMft | HardwareMft) | SortAndFilter, null,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = H264 }, out nint found, out uint count);
            if (found == 0) return 0;
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(found, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(found);
            return (int)count;
        }
        catch (Exception failure) when (failure is SharpGenException or COMException)
        {
            Diagnostics.Log.Swallowed(failure);
            return 0;
        }
    }
}
