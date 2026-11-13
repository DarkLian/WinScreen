using System.ComponentModel;
using System.Runtime.InteropServices;
using WinScreen.Native;

namespace WinScreen.Hotkeys;

/// <summary>
/// A hidden top-level window that receives global hotkeys. It also answers WM_CLOSE, so an
/// ordinary close request (for example <c>taskkill /im WinScreen.exe</c>) shuts WinScreen down cleanly.
/// </summary>
public sealed class HotkeyHost : NativeWindow, IDisposable
{
    public const string WindowTitle = "WinScreen.HotkeyHost";

    private readonly List<int> _registered = [];
    private readonly Dictionary<int, (uint Modifiers, Keys Key)> _bindings = [];
    private readonly Dictionary<int, int> _dispatch = [];
    private int _nextId = 0x4000;

    public HotkeyHost()
    {
        CreateHandle(new CreateParams { Caption = WindowTitle });
    }

    public event Action<int>? Pressed;
    public event Action? CloseRequested;

    /// <summary>Answers a WM_COPYDATA (its lParam), such as a command from the command line; null leaves it unanswered.</summary>
    internal Func<nint, nint?>? CopyDataReceived { get; set; }

    internal bool IsRegistered(int command) => _dispatch.ContainsValue(command);

    /// <summary>Claims a global hotkey. Returns false with a reason when another program holds it.</summary>
    public bool TryRegister(int id, uint modifiers, Keys key, out string? failure)
    {
        if (User32.RegisterHotKey(Handle, id, modifiers | User32.MOD_NOREPEAT, (uint)key))
        {
            _registered.Add(id);
            _bindings[id] = (modifiers, key);
            _dispatch[id] = id;
            failure = null;
            return true;
        }

        failure = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return false;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered)
            User32.UnregisterHotKey(Handle, id);
        _registered.Clear();
        _bindings.Clear();
        _dispatch.Clear();
    }

    /// <summary>
    /// Claims every combination that is free and returns the commands whose combination isn't, so one
    /// hotkey held by another app can't take the rest down with it. For startup, when nothing is bound yet.
    /// </summary>
    internal IReadOnlyList<int> RegisterAvailable(IReadOnlyDictionary<int, (uint Modifiers, Keys Key)> desired)
    {
        UnregisterAll();
        var conflicts = new List<int>();
        foreach (var (command, combination) in desired)
        {
            int nativeId = _nextId++;
            if (!User32.RegisterHotKey(Handle, nativeId, combination.Modifiers | User32.MOD_NOREPEAT,
                    (uint)combination.Key))
            {
                conflicts.Add(command);
                continue;
            }

            _registered.Add(nativeId);
            _bindings[nativeId] = combination;
            _dispatch[nativeId] = command;
        }

        return conflicts;
    }

    /// <summary>Reserve new combinations while every old binding stays active. Commit preferences before releasing obsolete bindings.</summary>
    internal bool TryReplaceBindings(IReadOnlyDictionary<int, (uint Modifiers, Keys Key)> desired, Action persist,
        out IReadOnlyList<int> conflicts)
    {
        var reserved = new List<int>();
        var dispatch = new Dictionary<int, int>();
        var problems = new List<int>();
        var used = new HashSet<(uint, Keys)>();
        foreach (var (command, combination) in desired)
        {
            if (!used.Add(combination))
            {
                problems.Add(command);
                continue;
            }

            int nativeId = _bindings.FirstOrDefault(pair => pair.Value == combination).Key;
            if (nativeId == 0)
            {
                while (_registered.Contains(_nextId)) _nextId = _nextId >= 0xbffe ? 0x4000 : _nextId + 1;
                nativeId = _nextId;
                _nextId = _nextId >= 0xbffe ? 0x4000 : _nextId + 1;
                if (!User32.RegisterHotKey(Handle, nativeId, combination.Modifiers | User32.MOD_NOREPEAT,
                        (uint)combination.Key))
                {
                    problems.Add(command);
                    continue;
                }

                reserved.Add(nativeId);
            }

            dispatch[nativeId] = command;
        }

        if (problems.Count > 0)
        {
            foreach (int id in reserved) User32.UnregisterHotKey(Handle, id);
            conflicts = problems;
            return false;
        }

        try
        {
            persist();
        }
        catch
        {
            foreach (int id in reserved) User32.UnregisterHotKey(Handle, id);
            throw;
        }

        foreach (int obsolete in _registered.Where(id => !dispatch.ContainsKey(id)).ToArray())
            User32.UnregisterHotKey(Handle, obsolete);
        _registered.Clear();
        _bindings.Clear();
        _dispatch.Clear();
        foreach (var (nativeId, command) in dispatch)
        {
            _registered.Add(nativeId);
            _bindings[nativeId] = desired[command];
            _dispatch[nativeId] = command;
        }

        conflicts = [];
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case User32.WM_HOTKEY:
                if (_dispatch.TryGetValue(m.WParam.ToInt32(), out int command))
                    Pressed?.Invoke(command);
                break;
            case User32.WM_CLOSE:
                CloseRequested?.Invoke();
                return;
            case User32.WM_COPYDATA when CopyDataReceived?.Invoke(m.LParam) is { } answer:
                m.Result = answer;
                return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
