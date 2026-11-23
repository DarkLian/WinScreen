using System.Drawing.Imaging;
using Vortice.MediaFoundation;

namespace WinScreen.Tests.Support;

// An MP4 read back with Windows' own reader: how many frames, the ones asked for as pictures, and how loud its sound is.
internal sealed class VideoFile : IDisposable
{
    internal Size Size { get; private set; }
    internal int Frames { get; private set; }
    internal List<long> Times { get; } = [];
    internal List<(int Index, Bitmap Picture)> Kept { get; } = [];
    internal bool HasSound { get; private set; }
    // Root mean square of the samples, 0 to 1.
    internal double SoundLevel { get; private set; }
    internal double SoundSeconds { get; private set; }

    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup().Success);

    private static IMFSourceReader Open(string path)
    {
        if (!Started.Value) throw new InvalidOperationException("Media Foundation didn't start.");
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        return reader;
    }

    internal static VideoFile Read(string path, Func<int, bool>? keep = null)
    {
        keep ??= _ => false;
        var file = new VideoFile();
        using (IMFSourceReader reader = Open(path))
        {
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (IMFMediaType wanted = MediaFactory.MFCreateMediaType())
            {
                wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                wanted.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, wanted);
            }
            int defaultStride;
            using (IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                ulong frame = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                file.Size = new Size((int)(frame >> 32), (int)(frame & 0xFFFFFFFF));
                // Without a stride, Media Foundation's RGB is bottom row first.
                try { defaultStride = unchecked((int)current.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); }
                catch (SharpGen.Runtime.SharpGenException) { defaultStride = -file.Size.Width * 4; }
            }
            while (true)
            {
                using IMFSample? sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out SourceReaderFlag flags, out long time);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (sample is null) continue;
                file.Times.Add(time);
                if (keep(file.Frames)) file.Kept.Add((file.Frames, Picture(sample, file.Size, defaultStride)));
                file.Frames++;
            }
        }
        file.ReadSound(path);
        return file;
    }

    private void ReadSound(string path)
    {
        using IMFSourceReader reader = Open(path);
        try { reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true); }
        catch (SharpGen.Runtime.SharpGenException) { return; }
        using (IMFMediaType wanted = MediaFactory.MFCreateMediaType())
        {
            wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            wanted.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            wanted.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, wanted);
        }
        int channels, rate;
        using (IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
        {
            channels = (int)current.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
            rate = (int)current.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
        }
        HasSound = true;
        double sum = 0;
        long samples = 0;
        while (true)
        {
            using IMFSample? sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
                out _, out SourceReaderFlag flags, out _);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (sample is null) continue;
            using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out nint data, out _, out int length);
            try
            {
                for (int i = 0; i + 1 < length; i += 2)
                {
                    double value = System.Runtime.InteropServices.Marshal.ReadInt16(data, i) / 32768.0;
                    sum += value * value;
                    samples++;
                }
            }
            finally { buffer.Unlock(); }
        }
        SoundLevel = samples == 0 ? 0 : Math.Sqrt(sum / samples);
        SoundSeconds = samples / (double)Math.Max(1, channels) / Math.Max(1, rate);
    }

    private static unsafe Bitmap Picture(IMFSample sample, Size size, int defaultStride)
    {
        // The sample's own buffer knows its row pitch; a decoder keeps rows and columns to multiples of 16.
        using IMFMediaBuffer buffer = sample.BufferCount == 1 ? sample.GetBufferByIndex(0) : sample.ConvertToContiguousBuffer();
        var picture = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        BitmapData bits = picture.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        IMF2DBuffer? flat = null;
        try
        {
            try { flat = buffer.QueryInterface<IMF2DBuffer>(); }
            catch (SharpGen.Runtime.SharpGenException) { flat = null; }
            byte* top;
            int pitch;
            if (flat is not null) { flat.Lock2D(out nint scan, out pitch); top = (byte*)scan; }
            else
            {
                buffer.Lock(out nint data, out _, out int length);
                pitch = defaultStride;
                // The decoder's own size, rows and columns kept to multiples of 16 (432 x 304 for 420 x 300), whatever the
                // media type says (seen on Windows 11 26300).
                int padded = (size.Height + 15) / 16 * 16;
                if (length != Math.Abs(pitch) * size.Height)
                    pitch = Math.Sign(pitch) * (length % size.Height == 0 ? length / size.Height : length / padded);
                top = pitch >= 0 ? (byte*)data : (byte*)data + (long)(size.Height - 1) * -pitch;
            }
            try
            {
                for (int y = 0; y < size.Height; y++)
                    Buffer.MemoryCopy(top + (long)y * pitch, (byte*)bits.Scan0 + (long)y * bits.Stride, bits.Stride, size.Width * 4L);
            }
            finally
            {
                if (flat is not null) flat.Unlock2D();
                else buffer.Unlock();
            }
        }
        finally
        {
            flat?.Dispose();
            picture.UnlockBits(bits);
        }
        return picture;
    }

    public void Dispose()
    {
        foreach ((_, Bitmap picture) in Kept) picture.Dispose();
    }
}
