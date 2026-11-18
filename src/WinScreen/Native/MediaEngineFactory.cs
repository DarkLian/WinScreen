using System.Runtime.InteropServices;

namespace WinScreen.Native;

internal static class MediaEngineFactory
{
    private static readonly Guid Class = new("B44392DA-499B-446B-A4CB-005FEAD0E6D5"); // CLSID_MFMediaEngineClassFactory
    private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");

    public static nint Create()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(Class, 0, 1, Unknown, out nint factory));
        return factory;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid type, nint outer, uint context, in Guid iid, out nint value);
}
