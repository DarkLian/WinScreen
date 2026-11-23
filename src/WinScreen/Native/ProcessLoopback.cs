using System.Runtime.InteropServices;

namespace WinScreen.Native;

// Windows' process loopback (Windows 10 2004 and later): one program's own sound, with the programs it started.
internal static class ProcessLoopback
{
    private const string Device = "VAD\\Process_Loopback";
    private const int ActivationProcessLoopback = 1, IncludeTargetTree = 0;
    internal const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvertPcm = 0x80000000, DefaultQuality = 0x08000000;
    internal const uint Silent = 0x2;

    internal static unsafe IAudioClient Activate(uint process, TimeSpan limit)
    {
        var parameters = new ActivationParameters { Type = ActivationProcessLoopback, Process = process, Mode = IncludeTargetTree };
        var blob = new PropVariantBlob { Type = 65 /* VT_BLOB */, Size = (uint)sizeof(ActivationParameters), Data = (nint)(&parameters) };
        var done = new Completion();
        Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(Device, typeof(IAudioClient).GUID, (nint)(&blob), done,
            out IActivateAudioInterfaceAsyncOperation operation));
        try
        {
            if (!done.Finished.Wait(limit)) throw new TimeoutException("Windows didn't answer for the program's sound.");
            operation.GetActivateResult(out int result, out object activated);
            Marshal.ThrowExceptionForHR(result);
            return (IAudioClient)activated;
        }
        finally { Marshal.ReleaseComObject(operation); }
    }

    [ComVisible(true)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal ManualResetEventSlim Finished { get; } = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Finished.Set();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters
    {
        public int Type;
        public uint Process;
        public int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort Tag;
        public ushort Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string device, in Guid interfaceId,
        nint parameters, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    // The activation's callback must be agile.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, in WaveFormat format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long period, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(in Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong position);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
