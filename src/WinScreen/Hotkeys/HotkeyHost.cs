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

    public HotkeyHost()
    {
        CreateHandle(new CreateParams { Caption = WindowTitle });
    }

    public event Action<int>? Pressed;
    public event Action? CloseRequested;

    /// <summary>Claims a global hotkey. Returns false with a reason when another program holds it.</summary>
    public bool TryRegister(int id, uint modifiers, Keys key, out string? failure)
    {
        if (User32.RegisterHotKey(Handle, id, modifiers | User32.MOD_NOREPEAT, (uint)key))
        {
            _registered.Add(id);
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
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case User32.WM_HOTKEY:
                Pressed?.Invoke(m.WParam.ToInt32());
                break;
            case User32.WM_CLOSE:
                CloseRequested?.Invoke();
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
