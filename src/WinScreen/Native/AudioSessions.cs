using System.Runtime.InteropServices;

namespace WinScreen.Native;

/// <summary>One program's sound on one output device, as Windows' volume mixer has it.</summary>
/// <param name="Device">The output device's id.</param>
/// <param name="Identifier">The same for the program after it restarts: Windows remembers the mute under it.</param>
/// <param name="Instance">This session only, while it lasts.</param>
internal sealed record AudioSession(string Device, string Identifier, string Instance, uint ProcessId, bool Muted);

/// <summary>What WinScreen asks of Windows' audio sessions: reading them, and muting one. Tests give their own.</summary>
internal interface IAudioSessions
{
    /// <summary>Every program's session on every active output device; empty when Windows can't say.</summary>
    AudioSession[] List();

    /// <summary>Mutes or unmutes the sessions on a device that <paramref name="matches"/> picks. How many were set.</summary>
    int SetMuted(string device, Func<AudioSession, bool> matches, bool muted);

    /// <summary>The ids of the output devices there now.</summary>
    string[] Devices();
}

/// <summary>
/// Windows' audio sessions through their COM tables, called the way <see cref="Windows.ElementFinder"/> calls UI
/// Automation. Each call runs on its own short-lived thread with a time limit, so an audio service that doesn't answer
/// holds nothing up, and so a crash handler on any thread can use it.
/// </summary>
internal sealed unsafe partial class AudioSessions : IAudioSessions
{
    private const int Render = 0, Active = 1, AllContexts = 0x17, InProcess = 1, TimeLimitMilliseconds = 3000;

    // Places in the COM tables.
    private const int QueryInterface = 0, EnumAudioEndpoints = 3, CollectionCount = 3, CollectionItem = 4, Activate = 3, GetId = 5,
        GetSessionEnumerator = 5, SessionCount = 3, GetSession = 4, GetSessionIdentifier = 12, GetSessionInstanceIdentifier = 13,
        GetProcessId = 14, SetMute = 5, GetMute = 6;

    private static readonly Guid DeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid DeviceEnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid SessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid SessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid SimpleVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static AudioSessions Instance { get; } = new();

    public AudioSession[] List()
    {
        var found = new List<AudioSession>();
        return OnOwnThread(() => Visit(null, (session, _) => found.Add(session))) ? found.ToArray() : [];
    }

    public string[] Devices()
    {
        var found = new List<string>();
        return OnOwnThread(() => Visit(null, null, found.Add)) ? found.ToArray() : [];
    }

    public int SetMuted(string device, Func<AudioSession, bool> matches, bool muted)
    {
        int set = 0;
        OnOwnThread(() => Visit(device, (session, volume) =>
        {
            if (!matches(session)) return;
            Guid none = Guid.Empty;
            if (((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Slot(volume, SetMute))(volume, muted ? 1 : 0, &none) == 0)
                set++;
        }));
        return set;
    }

    private static bool OnOwnThread(Action work)
    {
        bool done = false;
        var thread = new Thread(() =>
        {
            int started = CoInitializeEx(0, 0); // Multithreaded.
            try { work(); done = true; }
            catch (Exception failure) when (failure is COMException or InvalidOperationException) { Diagnostics.Log.Swallowed(failure); }
            finally { if (started >= 0) CoUninitialize(); }
        }) { IsBackground = true, Name = "WinScreen audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread.Join(TimeLimitMilliseconds) && done;
    }

    /// <summary>Calls <paramref name="each"/> with every session and its volume, on one device or on all.</summary>
    private static void Visit(string? onlyDevice, Action<AudioSession, nint>? each, Action<string>? eachDevice = null)
    {
        nint enumerator = 0, devices = 0;
        try
        {
            Guid type = DeviceEnumeratorClass, wanted = DeviceEnumeratorInterface;
            if (CoCreateInstance(&type, 0, InProcess, &wanted, &enumerator) != 0 || enumerator == 0) return;
            if (((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Slot(enumerator, EnumAudioEndpoints))(enumerator, Render, Active, &devices) != 0)
                return;
            uint count;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(devices, CollectionCount))(devices, &count) != 0) return;
            for (uint i = 0; i < count; i++)
            {
                nint device = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(devices, CollectionItem))(devices, i, &device) != 0) continue;
                try
                {
                    string id = TakeString(((delegate* unmanaged[Stdcall]<nint, char**, int>)Slot(device, GetId)), device);
                    if (id.Length == 0 || (onlyDevice is not null && id != onlyDevice)) continue;
                    eachDevice?.Invoke(id);
                    if (each is not null) VisitDevice(id, device, each);
                }
                finally { Marshal.Release(device); }
            }
        }
        finally
        {
            if (devices != 0) Marshal.Release(devices);
            if (enumerator != 0) Marshal.Release(enumerator);
        }
    }

    private static void VisitDevice(string id, nint device, Action<AudioSession, nint> each)
    {
        nint manager = 0, sessions = 0;
        try
        {
            Guid wanted = SessionManager2;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, Activate))(device, &wanted, AllContexts, 0, &manager) != 0)
                return;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, GetSessionEnumerator))(manager, &sessions) != 0) return;
            int count;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(sessions, SessionCount))(sessions, &count) != 0) return;
            for (int i = 0; i < count; i++)
            {
                nint control = 0, control2 = 0, volume = 0;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(sessions, GetSession))(sessions, i, &control) != 0) continue;
                    Guid second = SessionControl2, simple = SimpleVolume;
                    if (Ask(control, &second, &control2) != 0 || Ask(control, &simple, &volume) != 0) continue;
                    uint process;
                    // The system sounds session, and any that can't say whose it is, are left alone.
                    if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(control2, GetProcessId))(control2, &process) != 0 || process == 0) continue;
                    string identifier = TakeString(((delegate* unmanaged[Stdcall]<nint, char**, int>)Slot(control2, GetSessionIdentifier)), control2);
                    string instance = TakeString(((delegate* unmanaged[Stdcall]<nint, char**, int>)Slot(control2, GetSessionInstanceIdentifier)), control2);
                    int muted;
                    if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(volume, GetMute))(volume, &muted) != 0) continue;
                    each(new AudioSession(id, identifier, instance, process, muted != 0), volume);
                }
                finally
                {
                    if (volume != 0) Marshal.Release(volume);
                    if (control2 != 0) Marshal.Release(control2);
                    if (control != 0) Marshal.Release(control);
                }
            }
        }
        finally
        {
            if (sessions != 0) Marshal.Release(sessions);
            if (manager != 0) Marshal.Release(manager);
        }
    }

    private static int Ask(nint unknown, Guid* wanted, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(unknown, QueryInterface))(unknown, wanted, result);

    /// <summary>A string the object hands over, which is then freed.</summary>
    private static string TakeString(delegate* unmanaged[Stdcall]<nint, char**, int> get, nint self)
    {
        char* text = null;
        if (get(self, &text) != 0 || text == null) return "";
        try { return new string(text); }
        finally { Marshal.FreeCoTaskMem((nint)text); }
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* wanted, nint* instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
