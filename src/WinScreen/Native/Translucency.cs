using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinScreen.Native;

/// <summary>
/// Windows' "Transparency effects": with it on, Windows 11's own menus show a blurred, tinted glimpse of what is
/// behind them. WinScreen's menus follow the same setting, through the blur Windows offers any window behind its
/// see-through parts.
/// </summary>
internal static class Translucency
{
    private const int WCA_ACCENT_POLICY = 19, ACCENT_DISABLED = 0, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4, DRAW_ALL_BORDERS = 0x2;
    private static bool _on;
    private static long _readAt = -100_000; // Long enough ago that the first look reads it.

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public nint Data; public int Size; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint window, ref CompositionData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint window, in Margins margins);

    /// <summary>Whether Transparency effects is on in Windows 11, read again at most every couple of seconds.</summary>
    internal static bool On
    {
        get
        {
            if (Environment.TickCount64 - _readAt < 2000) return _on;
            _readAt = Environment.TickCount64;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                _on = Environment.OSVersion.Version.Build >= 22000 && key?.GetValue("EnableTransparency") is int value && value != 0;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { _on = false; }
            return _on;
        }
    }

    /// <summary>Blurs what is behind <paramref name="window"/>'s see-through parts, tinted <paramref name="tint"/>; or stops.</summary>
    internal static void Apply(nint window, Color? tint)
    {
        var accent = new AccentPolicy
        {
            State = tint is null ? ACCENT_DISABLED : ACCENT_ENABLE_ACRYLICBLURBEHIND, Flags = DRAW_ALL_BORDERS,
            GradientColor = tint is { } color ? color.A << 24 | color.B << 16 | color.G << 8 | color.R : 0,
        };
        // The see-through parts only let the blur through where the frame reaches; all of the window, while blurred.
        int all = tint is null ? 0 : -1;
        try { DwmExtendFrameIntoClientArea(window, new Margins { Left = all, Right = all, Top = all, Bottom = all }); }
        catch (DllNotFoundException) { }
        int size = Marshal.SizeOf<AccentPolicy>();
        nint data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new CompositionData { Attribute = WCA_ACCENT_POLICY, Data = data, Size = size };
            SetWindowCompositionAttribute(window, ref attribute);
        }
        catch (EntryPointNotFoundException) { } // Not in this Windows: the menu stays solid.
        finally { Marshal.FreeHGlobal(data); }
    }
}
